using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace SmartMagReloadMono
{
    /// <summary>
    /// 联机（Fika）下的弹匣落点 + 换完校正。
    ///
    /// 原则：不改 Fika 的换弹广播。换弹包照常发（远端能看到动作），我们只做两件事：
    ///   ① Before：原版没给旧弹匣落点时，用我们的算法给一个真实空位（包带着它走，天然一致）
    ///   ② Postfix：等这次换弹真的完成，再把物品“摆回去”——
    ///        小换大/同尺寸：旧弹匣直接挪回新弹匣原来的格子（一笔事务）
    ///        大换小：把挡路的那件挪进新弹匣腾出的格子，再把旧弹匣放进它让出来的位置（两笔事务）
    ///      全部走 InventoryController.TryRunNetworkTransaction，由 Fika 同步。
    /// </summary>
    internal static class FikaCoopPlacement
    {
        internal static bool Installed { get; private set; }

        private static bool _pendingFixup;
        private static bool _fixupRunning;
        private static float _fixupStartedAt;
        private static bool _reentering;          // 我们自己在重入 ReloadMag 时置位，让 prefix 直接放行
        private static bool _preMoveBusy;         // 预挪 + 重入 正在跑
        private static float _preMoveStartedAt;
        private static Player _lastPlayer;
        private static MethodInfo _reloadMagMethod;
        private static readonly List<Beautify> _beautify = new List<Beautify>();

        private static Player _player;
        private static InventoryController _ic;
        private static Weapon _weapon;
        private static Magazine _oldMag;
        private static Magazine _newMag;
        private static ItemAddress _destAddress;
        private static ItemAddress _fallback;

        internal static void Install(Harmony harmony)
        {
            if (!Plugin.FikaInstalled) return;
            try
            {
                Type ctrl = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name.IndexOf("Fika", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Type[] types;
                    try { types = a.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException ex)
                    { types = ex.Types; }
                    catch { continue; }
                    foreach (var t in types)
                    {
                        if (t != null && t.Name == "FikaClientFirearmController") { ctrl = t; break; }
                    }
                    if (ctrl != null) break;
                }
                if (ctrl == null) { Plugin.Log?.LogWarning("[KMR] Fika controller not found; co-op placement disabled"); return; }

                MethodInfo m = null;
                foreach (var cand in ctrl.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (cand.Name != "ReloadMag") continue;
                    var ps = cand.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(Magazine) && ps[1].ParameterType == typeof(ItemAddress))
                    { m = cand; break; }
                }
                if (m == null) { Plugin.Log?.LogWarning("[KMR] Fika ReloadMag signature not found"); return; }

                _reloadMagMethod = m;
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(FikaCoopPlacement), "Before")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(FikaCoopPlacement), "After")));
                Installed = true;
                Plugin.Log?.LogInfo("[KMR] co-op hook installed on " + ctrl.FullName + ".ReloadMag");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] co-op placement install failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- prefix

        private static bool Before(object __instance, Magazine magazine, ref ItemAddress itemAddress, Comfort.Common.Callback callback)
        {
            try
            {
                if (_reentering) return true;                         // 我们自己的重入：让原版照跑
                if (magazine == null || __instance == null) return true;
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return true;
                // 纯单机由 DirectSwap/ReloadFix 处理；测试构建(KMR_FORCE_COOP)允许在 Host+0 人时也走这条
                if (!Plugin.InFikaSession() && !(Plugin.ForceCoopForTest && Plugin.FikaSessionAny())) return true;

                // 两端都装本插件时，交给 DirectSwap（UIFixes 式）：各端在 Run 里做同样的交换，
                // 不需要临时位、不借道背包。这里只保留兜底（DirectSwap 关掉时才走预挪路径）。
                if (Plugin.UiFixesDelegated) return true;             // UIFixes 接管时让路（优先，避免两边都撒手）
                if (Plugin.RetentionOwns()) { Plugin.V("coop: retention owns this reload"); return true; }
                // ★ 只有“没开 CoopZeroGap”时才让路给 DirectSwap；
                //   开了就走本文件的事务路径（能同步重排）。
                if (Plugin.DirectSwapEnabled != null && Plugin.DirectSwapEnabled.Value
                    && !Plugin.CoopPathActive())
                {
                    Plugin.V("handoff to DirectSwap");
                    return true;
                }

                var player = GetField<Player>(__instance, "_player") ?? GetField<Player>(__instance, "player");
                var weapon = GetField<Weapon>(__instance, "Item") ?? GetField<Weapon>(__instance, "_item");
                if (player == null || weapon == null) { Diag.Coop("skip", "player/weapon not resolved"); return true; }
                if (!player.IsYourPlayer) return true;                // 只管本地玩家，别碰 bot / 观察副本

                if (!ReferenceEquals(_lastPlayer, player))
                {
                    _lastPlayer = player;
                    _beautify.Clear();
                    _pendingFixup = false;
                    _fixupRunning = false;
                    _preMoveBusy = false;
                }
                if (_preMoveBusy)
                {
                    if (UnityEngine.Time.time - _preMoveStartedAt < 10f)
                    {
                        // ★ 此刻还没改任何状态 ⇒ 直接放行原版（等价于重发，且不丢回调）
                        Diag.Coop("zerogap-busy", "another zero-gap reload is in flight; letting vanilla handle this one");
                        return true;
                    }
                    Diag.Coop("zerogap-watchdog", "previous zero-gap lock looked stuck; letting the vanilla reload through");
                    _preMoveBusy = false;
                    return true;
                }
                var ic = player.InventoryController;
                var current = weapon.GetCurrentMagazine();
                if (current == null || ReferenceEquals(current, magazine)) return true;
                if (magazine.IsMagazineWithBelt) return true;

                var dest = magazine.CurrentAddress as GridItemAddress;

                // 守卫失败不整体放弃：只是不做“零中间态”，仍然走落点 + 事后校正
                bool guardOk = dest != null;
                if (guardOk)
                {
                    try
                    {
                        if (!ReferenceEquals(dest.Grid.GetItemAt(dest.LocationInGrid), magazine)) { Diag.Coop("skip-zerogap", "grid cell mismatch"); guardOk = false; }
                        else if (!ReferenceEquals(dest.GetOwner(), ic)) { Diag.Coop("skip-zerogap", "different owner"); guardOk = false; }
                    }
                    catch { guardOk = false; }
                }
                else Diag.Coop("skip-zerogap", "new magazine is not in a grid");

                bool fits = false;
                if (guardOk)
                {
                    try
                    {
                        var rot = dest.LocationInGrid.r;
                        var a = current.CalculateRotatedSize(rot);
                        var b = magazine.CalculateRotatedSize(rot);
                        fits = a.X <= b.X && a.Y <= b.Y;
                    }
                    catch { guardOk = false; }
                }

                Diag.Host($"reload peers={Plugin.PeerCount()} old={Diag.Item(current)} new={Diag.Item(magazine)} dest={Diag.Addr(dest)} fits={fits} zerogap-ok={guardOk}");

                // 零中间态：先把新弹匣挪开（腾出它那格），必要时再把挡路物挪开，
                // 然后带“最终落点”重入换弹 —— 原版会把旧弹匣直接放进最终位置，没有任何“先落一下”。
                if (guardOk && Plugin.CoopPathActive())
                {
                    Displace reorder = null;
                    ItemAddress target = null;
                    if (fits) target = dest;
                    else
                    {
                        reorder = FindDisplace(ic, dest, magazine, current);
                        if (reorder != null && reorder.Spot != null)
                            target = reorder.Grid.CreateItemAddress(reorder.Spot);
                    }
                    if (target != null)
                    {
                        _preMoveBusy = true;
                        _preMoveStartedAt = UnityEngine.Time.time;
                        try
                        {
                            var originalLanding = itemAddress;      // ★ 原版给的落点（null 表示原版会掉地/各端自算）
                            Plugin.Instance.StartCoroutine(WrapReload(
                                PreMoveThenReload(__instance, ic, player, weapon, magazine, current, dest, reorder, target, callback, originalLanding),
                                ic, player, weapon, magazine, current, dest, reorder, callback, originalLanding));
                        }
                        catch (Exception ex)
                        {
                            _preMoveBusy = false;
                            Diag.Coop("zerogap-fallback", "coroutine start failed: " + ex.Message);
                            return true;                               // 取消预挪，本次交原版，回调不丢
                        }
                        return false;                                  // 取消本次，预挪完成后带新落点重入
                    }
                }

                ItemAddress expected = itemAddress;                    // 原版已给的落点（可能为空）
                if (expected == null)
                {
                    if (!TryFindFallbackNear(ic, current, out var fallback) || fallback == null)
                    {
                        Diag.Coop("placement-none", $"old={Diag.Item(current)} new={Diag.Item(magazine)} (vanilla will drop it on all clients)");
                        DirectSwap.NotifyNoSpace();
                        return true;
                    }
                    itemAddress = fallback;                            // 换弹包带着这个落点走，两端一致
                    expected = fallback;
                }

                _player = player; _ic = ic; _weapon = weapon;
                _oldMag = current; _newMag = magazine;
                _destAddress = dest; _fallback = expected;
                _pendingFixup = true;
                Diag.Coop("placement", $"old={Diag.Item(current)} new={Diag.Item(magazine)} landing={Diag.Addr(expected)}");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][coop] error: " + ex.Message); }
            return true;                                              // 永远让原版继续（Fika 包必须发）
        }

        private static void After(object __instance)
        {
            try
            {
                if (_beautify.Count > 0)
                {
                    var b = _beautify[0];
                    _beautify.Clear();
                    Plugin.Instance.StartCoroutine(CoopBeautify(b));
                }
                if (!_pendingFixup) return;
                _pendingFixup = false;
                if (_fixupRunning)
                {
                    // 看门狗：正常一次 fixup 最多几秒，超过 10 秒认定卡死，强制放行
                    if (UnityEngine.Time.time - _fixupStartedAt < 10f)
                    {
                        Diag.Coop("fixup-busy", "previous fixup still running; this reload leaves the mag in its landing cell");
                        return;
                    }
                    Diag.Coop("fixup-watchdog", "previous fixup stuck; releasing lock");
                }
                _fixupRunning = true;
                _fixupStartedAt = UnityEngine.Time.time;
                Plugin.Instance.StartCoroutine(CoopFixup(_player, _ic, _weapon, _oldMag, _newMag, _destAddress, _fallback));
            }
            catch (Exception ex)
            {
                _fixupRunning = false;
                Plugin.Log?.LogWarning("[KMR][coop] fixup start failed: " + ex.Message);
            }
        }

        // ------------------------------------------------- robustness wrappers

        /// <summary>协程外层包一层：内部任何未捕获异常都要走恢复，不能静默死掉。</summary>
        private static IEnumerator WrapReload(IEnumerator inner, InventoryController ic, Player player, Weapon weapon,
            Magazine newMag, Magazine oldMag, GridItemAddress dest, Displace plan, Comfort.Common.Callback callback,
            ItemAddress originalLanding)
        {
            while (true)
            {
                bool ok = false, crashed = false;
                object current = null;
                string err = null;
                try
                {
                    ok = inner.MoveNext();
                    if (ok) current = inner.Current;
                }
                catch (Exception ex)
                {
                    crashed = true;
                    err = ex.Message;
                }
                if (crashed)
                {
                    Diag.Coop("zerogap-crashed", "coroutine threw: " + err);
                    yield return CrashRecover(ic, player, weapon, newMag, oldMag, dest, plan, callback, originalLanding);
                    yield break;
                }
                if (!ok) yield break;
                yield return current;
            }
        }

        /// <summary>零中间态中途崩掉后的兜底：把东西放回去，再重新发起一次普通换弹。</summary>
        private static IEnumerator CrashRecover(InventoryController ic, Player player, Weapon weapon,
            Magazine newMag, Magazine oldMag, GridItemAddress dest, Displace plan, Comfort.Common.Callback callback,
            ItemAddress originalLanding)
        {
            bool restoreNew = false, restoreBlocker = false, reissue = false;
            try
            {
                restoreNew = newMag != null && dest != null && newMag.CurrentAddress != null
                    && !SameCell(newMag.CurrentAddress, dest)
                    && (weapon == null || !ReferenceEquals(weapon.GetCurrentMagazine(), newMag));
            }
            catch { }
            try
            {
                restoreBlocker = plan != null && plan.Blocker != null && plan.From != null
                    && !SameCell(plan.Blocker.CurrentAddress, plan.From);
            }
            catch { }
            try
            {
                reissue = player != null && weapon != null && ReferenceEquals(weapon.GetCurrentMagazine(), oldMag);
            }
            catch { }

            if (restoreNew) yield return Restore(ic, newMag, dest, 0);
            if (restoreBlocker) yield return Restore(ic, plan.Blocker, plan.From, 0);
            yield return null;
            if (reissue) ReissueWithFallback(null, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
            _preMoveBusy = false;
        }

        /// <summary>换弹中途被打断时，新弹匣可能被滚回临时位：几秒后把它送回原格。</summary>
        private static IEnumerator TrackTempNew(InventoryController ic, Weapon weapon, Magazine newMag, ItemAddress tempNew, ItemAddress dest)
        {
            float deadline = UnityEngine.Time.time + 6f;
            while (UnityEngine.Time.time < deadline)
            {
                if (weapon == null || newMag == null) yield break;
                if (ReferenceEquals(weapon.GetCurrentMagazine(), newMag)) yield break;   // 换上了
                if (newMag.CurrentAddress == null) yield break;
                if (!SameCell(newMag.CurrentAddress, tempNew)) yield break;               // 它被移去别处了，别抢
                yield return null;
            }
            if (weapon == null || newMag == null || ReferenceEquals(weapon.GetCurrentMagazine(), newMag)) yield break;
            if (SameCell(newMag.CurrentAddress, tempNew) && !SameCell(newMag.CurrentAddress, dest))
            {
                Diag.Coop("zerogap-temp-cleanup", "reload was interrupted; new magazine returned to its cell");
                yield return Restore(ic, newMag, dest, 0);
            }
        }

        // ------------------------------------------------- zero-gap (pre-move + re-enter)

        private sealed class Beautify
        {
            internal InventoryController Ic;
            internal Player Player;
            internal Weapon Weapon;
            internal Magazine NewMag;
            internal Item Blocker;
            internal ItemAddress From;
            internal ItemAddress To;
        }

        /// <summary>
        /// 旧弹匣更大时：先把挡路物挪到一个临时空位（走 Fika 事务并等回执），
        /// 然后带“修正后的落点”重入 ReloadMag —— 原版换弹会把旧弹匣直接放进最终区域，
        /// 包也带着这个地址发出去，所以两端一致且没有“先落一下”的中间态。
        /// </summary>
        private static IEnumerator PreMoveThenReload(object controller, InventoryController ic, Player player, Weapon weapon,
            Magazine newMag, Magazine oldMag, GridItemAddress dest, Displace plan, ItemAddress target, Comfort.Common.Callback callback,
            ItemAddress originalLanding)
        {
            int seq = Diag.NextSeq();
            ItemAddress tempNew = null, tempBlocker = null;
            bool newMoved = false, blockerMoved = false;
            string bname = plan != null && plan.Blocker != null ? plan.Blocker.ShortName : "none";
            Diag.Coop("zerogap-start", $"seq={seq} blocker={bname} target={Diag.Addr(target)}");
            try
            {
                // 1) 需要挤开时：先把挡路物挪到临时位
                if (plan != null && plan.Blocker != null)
                {
                    tempBlocker = FindTempSpot(ic, plan.Blocker, plan.Grid, plan.Spot, oldMag, allowBackpack: false);
                    if (tempBlocker == null)
                    {
                        Diag.Coop("zerogap-fallback", $"seq={seq} no temporary cell for the blocker");
                        ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                        yield break;
                    }
                    var rb = ItemManipulator.Move(plan.Blocker, tempBlocker, ic, simulate: true);
                    if (rb.Failed)
                    {
                        Diag.Coop("zerogap-fallback", $"seq={seq} blocker move failed: {rb.Error}");
                        ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                        yield break;
                    }
                    var tb = ic.TryRunNetworkTransaction(rb);
                    yield return WaitTask(tb, 5f);
                    if (!IsOk(tb))
                    {
                        Diag.Coop("zerogap-fallback", $"seq={seq} blocker move rejected by host");
                        ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                        yield break;
                    }
                    blockerMoved = true;
                    Diag.Coop("zerogap-premoved", $"seq={seq} {plan.Blocker.ShortName} -> {Diag.Addr(tempBlocker)}");
                }

                // 2) 关键：把新弹匣从它原来的格子挪走，腾出目标区域
                bool needVacate = SameCell(target, dest);
                bool newSitsInTarget = newMag.CurrentAddress != null && SameCell(newMag.CurrentAddress, dest);
                if (needVacate && newSitsInTarget)
                {
                    tempNew = FindTempSpot(ic, newMag, dest.Grid, dest.LocationInGrid, newMag, allowBackpack: false);
                    if (tempNew == null)
                    {
                        Diag.Coop("zerogap-fallback", $"seq={seq} no temporary cell for the new magazine");
                        if (blockerMoved) yield return ReturnBlocker(ic, plan, seq);
                        ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                        yield break;
                    }
                    var rn = ItemManipulator.Move(newMag, tempNew, ic, simulate: true);
                    if (rn.Failed)
                    {
                        Diag.Coop("zerogap-fallback", $"seq={seq} new magazine move failed: {rn.Error}");
                        if (blockerMoved) yield return ReturnBlocker(ic, plan, seq);
                        ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                        yield break;
                    }
                    var tn = ic.TryRunNetworkTransaction(rn);
                    yield return WaitTask(tn, 5f);
                    if (!IsOk(tn))
                    {
                        Diag.Coop("zerogap-fallback", $"seq={seq} new magazine move rejected by host");
                        if (blockerMoved) yield return ReturnBlocker(ic, plan, seq);
                        ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                        yield break;
                    }
                    newMoved = true;
                    Diag.Coop("zerogap-newmoved", $"seq={seq} new magazine -> {Diag.Addr(tempNew)}");
                }

                // 3) 重入前校验：手/枪没变、旧弹匣还在枪上、旧弹匣真能落进目标区域
                if (!ReferenceEquals(player.HandsController, controller) || !ReferenceEquals(weapon.GetCurrentMagazine(), oldMag))
                {
                    Diag.Coop("zerogap-fallback", $"seq={seq} state changed during pre-move");
                    if (newMoved) yield return Restore(ic, newMag, dest, seq);
                    if (blockerMoved) yield return ReturnBlocker(ic, plan, seq);
                    ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                    yield break;
                }
                var probe = ItemManipulator.Move(oldMag, target, ic, simulate: true);
                if (probe.Failed)
                {
                    Diag.Coop("zerogap-fallback", $"seq={seq} target no longer fits ({probe.Error})");
                    if (newMoved) yield return Restore(ic, newMag, dest, seq);
                    if (blockerMoved) yield return ReturnBlocker(ic, plan, seq);
                    ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                    yield break;
                }

                // 4) 带最终落点重入（美化计划在 invoke 前登记，After 会在 invoke 内部消费）
                if (blockerMoved && plan != null && plan.Blocker != null)
                {
                    _beautify.Add(new Beautify
                    {
                        Ic = ic, Player = player, Weapon = weapon, NewMag = newMag,
                        Blocker = plan.Blocker, From = tempBlocker, To = dest
                    });
                }
                bool invoked = false;
                try
                {
                    _reentering = true;
                    _reloadMagMethod.Invoke(controller, new object[] { newMag, target, callback });
                    invoked = true;
                }
                catch (Exception ex)
                {
                    Diag.Coop("zerogap-fallback", $"seq={seq} re-enter threw: {ex.InnerException?.Message ?? ex.Message}");
                }
                finally { _reentering = false; }

                if (!invoked)
                {
                    _beautify.Clear();
                    if (newMoved) yield return Restore(ic, newMag, dest, seq);
                    if (blockerMoved) yield return ReturnBlocker(ic, plan, seq);
                    ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                    yield break;
                }

                // 5) 重入后确认换弹真的发生
                yield return null;
                if (!ReferenceEquals(weapon.GetCurrentMagazine(), newMag))
                {
                    Diag.Coop("zerogap-reentry-rolled-back", $"seq={seq} reload did not take effect");
                    _beautify.Clear();
                    if (newMoved) yield return Restore(ic, newMag, dest, seq);
                    if (blockerMoved) yield return ReturnBlocker(ic, plan, seq);
                    ReissueWithFallback(controller, ic, player, weapon, newMag, oldMag, dest, callback, originalLanding);
                    yield break;
                }
                if (newMoved && tempNew != null)
                    Plugin.Instance.StartCoroutine(TrackTempNew(ic, weapon, newMag, tempNew, dest));
                Diag.Coop("zerogap-reentry-ok", $"seq={seq} reload running with the final landing");
                Diag.Host($"zerogap ok: old landing={Diag.Addr(target)} new-in-gun=true");
            }
            finally
            {
                _preMoveBusy = false;
            }
        }

        /// <summary>把某件物品送回原位（走事务，等回执）。</summary>
        private static IEnumerator Restore(InventoryController ic, Item item, ItemAddress to, int seq)
        {
            if (item == null || to == null) yield break;
            Task t = null;
            try
            {
                var rm = ItemManipulator.Move(item, to, ic, simulate: true);
                if (rm.Failed) { Diag.Coop("zerogap-restore-failed", rm.Error.ToString()); yield break; }
                t = ic.TryRunNetworkTransaction(rm);
            }
            catch (Exception ex) { Diag.Coop("zerogap-restore-failed", ex.Message); yield break; }
            yield return WaitTask(t, 5f);
            Diag.Coop(IsOk(t) ? "zerogap-restored" : "zerogap-restore-failed", $"seq={seq}");
        }

        /// <summary>把预挪出去的挡路物送回原位（走事务，等回执）。</summary>
        private static IEnumerator ReturnBlocker(InventoryController ic, Displace plan, int seq)
        {
            if (plan == null || plan.Blocker == null || plan.From == null) yield break;
            Task t = null;
            try
            {
                var rm = ItemManipulator.Move(plan.Blocker, plan.From, ic, simulate: true);
                if (rm.Failed) { Diag.Coop("zerogap-blocker-restore-failed", rm.Error.ToString()); yield break; }
                t = ic.TryRunNetworkTransaction(rm);
            }
            catch (Exception ex)
            {
                Diag.Coop("zerogap-blocker-restore-failed", ex.Message);
                yield break;
            }
            yield return WaitTask(t, 5f);
            Diag.Coop(IsOk(t) ? "zerogap-blocker-restored" : "zerogap-blocker-restore-failed", $"seq={seq}");
        }

        /// <summary>零中间态失败时的退路：带一个合法落点重新发起换弹，并交给事后校正。</summary>
        private static void ReissueWithFallback(object controller, InventoryController ic, Player player, Weapon weapon,
            Magazine newMag, Magazine oldMag, GridItemAddress dest, Comfort.Common.Callback callback,
            ItemAddress originalLanding)
        {
            ItemAddress fb = null;
            // ★ 先用原版给的落点（它代表“这一局本该发生的事”，两端一致）；
            //   验证不了再退回本地兜底（兜底只影响本端，是最后手段）
            try
            {
                if (originalLanding != null && !ItemManipulator.Move(oldMag, originalLanding, ic, simulate: true).Failed)
                    fb = originalLanding;
            }
            catch { }
            if (fb == null) { try { ReloadFix.TryFindFallback(ic, oldMag, out fb); } catch { } }
            if (fb == null)
            {
                Reissue(controller, newMag, null, callback);
                return;
            }
            _player = player; _ic = ic; _weapon = weapon;
            _oldMag = oldMag; _newMag = newMag;
            _destAddress = dest; _fallback = fb;
            _pendingFixup = true;
            Diag.Coop("zerogap-reissue", $"fallback landing {Diag.Addr(fb)}, fixup will run after the reload");
            Reissue(controller, newMag, fb, callback);
        }

        private static void Reissue(object controller, Magazine newMag, ItemAddress address, Comfort.Common.Callback callback)
        {
            try
            {
                _reentering = true;
                _reloadMagMethod.Invoke(controller, new object[] { newMag, address, callback });
            }
            catch (Exception ex)
            {
                Diag.Coop("zerogap-reissue-failed", ex.InnerException?.Message ?? ex.Message);
                _beautify.Clear();
            }
            finally { _reentering = false; }
        }

        /// <summary>挡路物的临时位：优先换一个网格，避免和旧弹匣要占的区域打架。</summary>
        /// <summary>收集玩家装备上的所有网格：先非背包（弹挂/口袋/箱子），背包放最后。</summary>
        private static List<Grid> AllGrids(InventoryController ic)
        {
            var list = new List<Grid>();
            try
            {
                var eq = ic.Inventory?.Equipment;
                if (eq == null) return list;
                var bp = eq.GetSlot(EquipmentSlot.Backpack)?.ContainedItem as CompoundItem;
                var backpackGrids = new List<Grid>();
                foreach (var it in eq.GetAllItems())
                {
                    var comp = it as CompoundItem;
                    if (comp == null) continue;
                    foreach (var c in comp.Containers)
                    {
                        var g = c as Grid;
                        if (g == null) continue;
                        bool isBp = false;
                        if (bp != null) { foreach (var bc in bp.Containers) { if (ReferenceEquals(bc, c)) { isBp = true; break; } } }
                        if (isBp) { if (!backpackGrids.Contains(g)) backpackGrids.Add(g); }
                        else if (!list.Contains(g)) list.Add(g);
                    }
                }
                list.AddRange(backpackGrids);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 这一格是否真的收得下这个物品：过滤器 + 游戏自己的 Add 校验（模拟）。
        /// 只算几何会选到“尺寸够但过滤器不收”的格子，实机表现为新弹匣被塞去背包/掉地。
        /// </summary>
        private static bool CanPlaceHere(InventoryController ic, Item item, Grid g, LocationInGrid loc, out string why)
        {
            why = null;
            if (ic == null || item == null || g == null || loc == null) { why = "null arg"; return false; }
            try { if (!g.CheckCompatibility(item)) { why = "CheckCompatibility=false"; return false; } }
            catch (Exception e) { why = "compat threw: " + e.Message; return false; }
            try
            {
                var r = ItemManipulator.Add(item, g.CreateItemAddress(loc), ic, simulate: true);
                if (r.Failed) { why = r.Error.GetType().Name + ": " + r.Error; return false; }
                return true;
            }
            catch (Exception e) { why = "add threw: " + e.Message; return false; }
        }

        private static ItemAddress FindTempSpot(InventoryController ic, Item item, Grid avoidGrid, LocationInGrid avoidLoc, Item avoidBy, bool allowBackpack = true)
        {
            try
            {
                var equipment = ic.Inventory?.Equipment;
                if (equipment == null) return null;
                var bpMain = ic.Inventory?.Equipment?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem as CompoundItem;
                Func<Grid, bool> isBackpack = g =>
                {
                    if (bpMain == null || g == null) return false;
                    foreach (var cont in bpMain.Containers) if (ReferenceEquals(cont, g)) return true;
                    return false;
                };
                var grids = new List<Grid>();
                foreach (var g in AllGrids(ic))
                {
                    if (g == null || ReferenceEquals(g, avoidGrid)) continue;   // 同格单独处理
                    if (!allowBackpack && isBackpack(g)) continue;              // ★ 不拿背包当临时位（1.1.0 行为）
                    grids.Add(g);
                }
                // 同格优先只在“原网格不是背包”时做（挡路物在背包里时，宁可回弹挂也不要留背包）
                bool avoidIsBackpack = false;
                try
                {
                    var bp = ic.Inventory?.Equipment?.GetSlot(EquipmentSlot.Backpack)?.ContainedItem as CompoundItem;
                    var bpc = bp?.GetContainer("Backpack") as Grid;
                    avoidIsBackpack = bpc != null && ReferenceEquals(bpc, avoidGrid);
                    if (!avoidIsBackpack && bp != null)
                    {
                        foreach (var cont in bp.Containers)
                        {
                            if (cont is Grid cg && ReferenceEquals(cg, avoidGrid)) { avoidIsBackpack = true; break; }
                        }
                    }
                }
                catch { }
                if (avoidGrid != null && !avoidIsBackpack)
                {
                    var near = FindSpotAvoiding(avoidGrid, item, item, avoidLoc, avoidBy);
                    if (near != null)
                    {
                        if (CanPlaceHere(ic, item, avoidGrid, near, out var nw)) return avoidGrid.CreateItemAddress(near);
                        Diag.Coop("temp-reject", $"{item.ShortName} rejected near-cell {avoidGrid.ID}: {nw}");
                    }
                }
                foreach (var g in grids)
                {
                    var spot = FindSpot(g, item, item);
                    if (spot == null) continue;
                    if (!CanPlaceHere(ic, item, g, spot, out var wy))
                    {
                        string owner = "?";
                        try { owner = g.ParentItem != null ? g.ParentItem.ShortName : "?"; } catch { }
                        Diag.Coop("temp-reject", $"{item.ShortName} rejected by grid {g.ID} in {owner}: {wy}");
                        continue;
                    }
                    return g.CreateItemAddress(spot);
                }
                Diag.Coop("temp-scan", $"no cell for {item.ShortName} ({item.Width}x{item.Height}) in {grids.Count} near grids nor backpack");
                return null;
            }
            catch { return null; }
        }

        /// <summary>换弹完成后，把挡路物从临时位摆进新弹匣腾出来的那格（纯美化，失败无所谓）。</summary>
        private static IEnumerator CoopBeautify(Beautify b)
        {
            int seen = ReloadFix.ReloadFinishedCount;
            float deadline = UnityEngine.Time.time + 6f;
            bool ready = false;
            while (UnityEngine.Time.time < deadline)
            {
                if (b.Player == null || b.Weapon == null) yield break;
                if (ReloadFix.ReloadFinishedCount != seen) { ready = true; break; }
                yield return null;
            }
            if (!ready) { Diag.Coop("beautify-skip", "reload did not finish (no idling transition)"); yield break; }
            yield return null;
            float lockDeadline = UnityEngine.Time.time + 2f;
            while (UnityEngine.Time.time < lockDeadline && IsInventoryLocked(b.Ic, b.Player))
                yield return null;
            if (IsInventoryLocked(b.Ic, b.Player))
            {
                Diag.Coop("beautify-skip", "inventory still locked; blocker stays in its temporary cell");
                yield break;
            }
            Task t2 = null;
            try
            {
                if (b.Blocker.CurrentAddress == null) yield break;
                if (b.From != null && !SameCell(b.Blocker.CurrentAddress, b.From))
                {
                    Diag.Coop("beautify-skip", "blocker was moved by something else");
                    yield break;
                }
                var r = ItemManipulator.Move(b.Blocker, b.To, b.Ic, simulate: true);
                if (r.Failed) { Diag.Coop("beautify-skip", r.Error.ToString()); yield break; }
                t2 = b.Ic.TryRunNetworkTransaction(r);
            }
            catch (Exception ex) { Diag.Coop("beautify-skip", ex.Message); yield break; }
            yield return WaitTask(t2, 3f);
            Diag.Coop(IsOk(t2) ? "beautify-done" : "beautify-skip", "blocker into the cell the new magazine vacated");
        }

        // ---------------------------------------------------------------- fixup

        private static IEnumerator CoopFixup(Player player, InventoryController ic, Weapon weapon, Magazine oldMag, Magazine newMag, ItemAddress destAddress, ItemAddress fallback)
        {
            int seq = Diag.NextSeq();
            try
            {
                // 1) 等这次换弹真的完成（新弹匣在枪上），最多 5 秒
                float deadline = UnityEngine.Time.time + 2f;
                bool done = false;
                while (UnityEngine.Time.time < deadline)
                {
                    if (player == null || !player.IsYourPlayer || player.HealthController == null || !player.HealthController.IsAlive) yield break;
                    var cur = weapon != null ? weapon.GetCurrentMagazine() : null;
                    if (ReferenceEquals(cur, newMag)) { done = true; break; }
                    yield return null;
                }
                if (!done) { Diag.Coop("fixup-skip", $"seq={seq} reload did not complete"); yield break; }
                yield return null;

                var dest = destAddress as GridItemAddress;

                // 2) 旧弹匣必须还有归属（原版换弹把它放到了 expected）；没了说明被别的东西动过，别抢
                if (oldMag == null || oldMag.CurrentAddress == null)
                {
                    Diag.Coop("fixup-skip", $"seq={seq} old mag has no address (moved by something else)");
                    yield break;
                }
                if (fallback != null && !SameCell(oldMag.CurrentAddress, fallback))
                    Diag.Coop("fixup-note", $"seq={seq} old mag sits at {Diag.Addr(oldMag.CurrentAddress)} instead of the landing cell; still repositioning");
                // 已经回到原格就不用再搬
                if (dest != null && SameCell(oldMag.CurrentAddress, dest))
                {
                    Diag.Coop("fixup-done", $"seq={seq} old mag is already in the cell the new one came from");
                    yield break;
                }
                // 枪不在手上（切枪/收枪）就放弃，别把弹匣塞进一把已经收起来的枪
                if (weapon == null || weapon.CurrentAddress == null)
                {
                    Diag.Coop("fixup-skip", $"seq={seq} weapon is no longer in hands");
                    yield break;
                }

                var plan = dest != null ? FindDisplace(ic, dest, newMag, oldMag) : null;

                // 换弹收尾时背包是锁着的（ProcessStatus != None），等它解锁再动，否则事务会在本地就被否掉
                float lockDeadline = UnityEngine.Time.time + 4f;
                int lockFrames = 0;
                while (UnityEngine.Time.time < lockDeadline && IsInventoryLocked(ic, player) && lockFrames++ < 400)
                    yield return null;
                Diag.Coop("fixup-lock", $"seq={seq} waited={lockFrames}f locked={IsInventoryLocked(ic, player)}");
                if (IsInventoryLocked(ic, player))
                {
                    Diag.Coop("fixup-skip", $"seq={seq} inventory still locked; leaving the magazine in its landing cell");
                    yield break;
                }

                // 3) 大换小：先把挡路的那件挪进新弹匣腾出来的格子（同格快速路径没有挡路物）
                if (plan != null && plan.Blocker != null)
                {
                    var r1 = ItemManipulator.Move(plan.Blocker, dest, ic, simulate: true);
                    if (r1.Failed)
                    {
                        Diag.Coop("fixup-failed", $"seq={seq} blocker step={r1.Error}");
                        yield break;
                    }
                    var t1 = ic.TryRunNetworkTransaction(r1);
                    yield return WaitTask(t1, 5f);
                    if (!IsOk(t1))
                    {
                        Diag.Coop("fixup-failed", $"seq={seq} blocker rejected by network");
                        yield return RollbackBlocker(ic, plan, seq);
                        yield break;
                    }
                    Diag.Coop("fixup-blocker", $"seq={seq} {plan.Blocker.ShortName} -> new mag cell");
                }

                // 4) 旧弹匣摆回原位（同尺寸/小换大就是 dest；大换小是挡路物让出来的位置）
                ItemAddress target = plan != null ? plan.Grid.CreateItemAddress(plan.Spot) : dest;
                if (target == null)
                {
                    if (plan != null) yield return RollbackBlocker(ic, plan, seq);
                    Diag.Coop("fixup-failed", $"seq={seq} no target");
                    yield break;
                }

                if (player == null || player.HealthController == null || !player.HealthController.IsAlive)
                {
                    if (plan != null) yield return RollbackBlocker(ic, plan, seq);
                    Diag.Coop("fixup-skip", $"seq={seq} player died before repositioning");
                    yield break;
                }
                if (!ReferenceEquals(weapon.GetCurrentMagazine(), newMag))
                {
                    if (plan != null) yield return RollbackBlocker(ic, plan, seq);
                    Diag.Coop("fixup-skip", $"seq={seq} magazine changed before repositioning");
                    yield break;
                }

                var r2 = ItemManipulator.Move(oldMag, target, ic, simulate: true);
                if (r2.Failed)
                {
                    if (plan != null) yield return RollbackBlocker(ic, plan, seq);
                    Diag.Coop("fixup-failed", $"seq={seq} old step={r2.Error}");
                    yield break;
                }
                var t2 = ic.TryRunNetworkTransaction(r2);
                yield return WaitTask(t2, 5f);
                if (IsOk(t2))
                {
                    Diag.Coop("fixup-done", $"seq={seq} old={Diag.Item(oldMag)} -> {Diag.Addr(target)}");
                    yield break;
                }

                if (plan != null) yield return RollbackBlocker(ic, plan, seq);
                Diag.Coop("fixup-failed", $"seq={seq} old rejected by network ({DescribeTask(t2)})");
            }
            finally
            {
                _fixupRunning = false;
            }
        }

        /// <summary>旧弹匣最终没摆回去时，把挡路的那件送回原处（也走网络事务）。</summary>
        private static IEnumerator RollbackBlocker(InventoryController ic, Displace plan, int seq)
        {
            if (plan == null || plan.From == null) yield break;
            Task trb = null;
            try
            {
                var rb = ItemManipulator.Move(plan.Blocker, plan.From, ic, simulate: true);
                if (rb.Failed) { Diag.Coop("fixup-rollback-failed", $"seq={seq} blocker step={rb.Error}"); yield break; }
                trb = ic.TryRunNetworkTransaction(rb);
            }
            catch (Exception ex)
            {
                Diag.Coop("fixup-rollback-failed", $"seq={seq} {ex.Message}");
                yield break;
            }
            yield return WaitTask(trb, 5f);
            Diag.Coop(IsOk(trb) ? "fixup-rollback" : "fixup-rollback-failed", $"seq={seq} blocker returned");
        }

        // ------------------------------------------------- 挡路物让位查找（联机/单机都用）

        /// <summary>能不能动它：锁定 / 任务物 / 不可拾取 一律不碰。</summary>
        private static bool CanRelocate(Item x, Magazine nextMag, Magazine oldMag)
        {
            if (x == null) return false;
            if (ReferenceEquals(x, oldMag) || ReferenceEquals(x, nextMag)) return false;
            try { if (x.PinLockState != EItemPinLockState.Free) return false; } catch { return false; }
            try { if (x.QuestItem) return false; } catch { return false; }
            try { if (!x.CanBePickedUp) return false; } catch { return false; }
            return true;
        }

        private static Displace FindDisplace(InventoryController ic, GridItemAddress dest, Magazine nextMag, Magazine oldMag)
        {
            try
            {
                var equipment = ic.Inventory?.Equipment;
                if (equipment == null) return null;

                var nr = nextMag.CalculateRotatedSize(dest.LocationInGrid.r);

                // ① 同格优先：新弹匣原格里另有空位，旧弹匣直接放进去（不搬东西）
                var same = FindSpotAvoiding(dest.Grid, nextMag, oldMag, dest.LocationInGrid);
                if (same != null)
                    return new Displace { Blocker = null, Grid = dest.Grid, Spot = same, From = null };

                // ② 同格里把挡路件挪进“腾出的那格”，旧弹匣填它空出的位置
                var bySize = new List<Item>();
                foreach (var it in dest.Grid.Items)
                    if (CanRelocate(it, nextMag, oldMag)) bySize.Add(it);
                bySize.Sort((a, b) =>
                {
                    var ca = a.CalculateCellSize(); var cb = b.CalculateCellSize();
                    return (ca.X * ca.Y).CompareTo(cb.X * cb.Y);
                });
                foreach (var x in bySize)
                {
                    var xr = x.CalculateRotatedSize(dest.LocationInGrid.r);
                    if (xr.X > nr.X || xr.Y > nr.Y) continue;
                    try { if (!dest.Grid.CheckCompatibility(x)) continue; } catch { continue; }
                    var spot = FindSpot(dest.Grid, x, oldMag);
                    if (spot == null) continue;
                    return new Displace { Blocker = x, Grid = dest.Grid, Spot = spot, From = x.CurrentAddress };
                }

                // ③ 别的格子里某件塞进腾出的那格，旧弹匣填它空出的位置（按体积从小到大）
                foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: false))
                {
                    if (g == null || ReferenceEquals(g, dest.Grid)) continue;
                    var cands = new List<Item>();
                    foreach (var it in g.Items)
                        if (CanRelocate(it, nextMag, oldMag)) cands.Add(it);
                    cands.Sort((a, b) =>
                    {
                        var ca = a.CalculateCellSize(); var cb = b.CalculateCellSize();
                        return (ca.X * ca.Y).CompareTo(cb.X * cb.Y);
                    });
                    foreach (var x in cands)
                    {
                        var xr = x.CalculateRotatedSize(dest.LocationInGrid.r);
                        if (xr.X > nr.X || xr.Y > nr.Y) continue;
                        try { if (!dest.Grid.CheckCompatibility(x)) continue; } catch { continue; }

                        var from = x.CurrentAddress;                  // ★ 必须在 probe 之前抓
                        var na = nextMag.CurrentAddress as GridItemAddress;
                        bool removed = false;
                        bool moveOk = false;
                        try
                        {
                            // ★ 新弹匣此刻还占着腾出的那格，不摘掉 probe 必然失败（整条变死代码）
                            if (na != null && ReferenceEquals(na.Grid, dest.Grid))
                            {
                                removed = true;
                                dest.Grid.RemoveItem(nextMag, na.LocationInGrid);
                            }
                            moveOk = !ItemManipulator.Move(x, dest, ic, simulate: true).Failed;
                        }
                        catch { moveOk = false; }
                        finally
                        {
                            if (removed && nextMag.CurrentAddress == null)
                            {
                                try
                                {
                                    if (dest.Grid.HasFreeSpace(nextMag, na.LocationInGrid)) dest.Grid.PlaceItem(nextMag, na.LocationInGrid);
                                    else Plugin.Log?.LogError("[KMR] co-op displace: dest not free when restoring new magazine");
                                }
                                catch (Exception ex) { Plugin.Log?.LogError("[KMR] co-op displace restore: " + ex.Message); }
                            }
                        }
                        if (!moveOk) continue;

                        var spot = FindSpot(g, x, oldMag);
                        if (spot == null) continue;
                        return new Displace { Blocker = x, Grid = g, Spot = spot, From = from };
                    }
                }
            }
            catch (Exception ex) { Plugin.V("co-op displace lookup error: " + ex.Message); }
            return null;
        }

        private static LocationInGrid FindSpotAvoiding(Grid grid, Item exclude, Item target, LocationInGrid avoid, Item avoidBy = null)
        {
            try
            {
                int w = grid.GridWidth, h = grid.GridHeight;
                bool[,] occ = new bool[w, h];
                foreach (var it in grid.Items)
                {
                    if (ReferenceEquals(it, exclude)) continue;
                    var a = it.CurrentAddress as GridItemAddress;
                    if (a == null) continue;
                    var cs0 = CellSize(it);
                    Mark(occ, w, h, a.LocationInGrid, cs0.X, cs0.Y);
                }
                // 只有新弹匣还真的占着那格时，才把它的区域标成占用
                // 1) 被排除物品自己当前占的格子（必须标，否则会把它自己那格当成空位选出来）
                var cur = exclude?.CurrentAddress as GridItemAddress;
                if (exclude != null && cur != null && ReferenceEquals(cur.Grid, grid))
                {
                    var cse = CellSize(exclude);
                    Mark(occ, w, h, cur.LocationInGrid, cse.X, cse.Y);
                }
                // 2) 额外的禁区：由“即将落到这里的另一件物品”决定（挡路物要给旧弹匣让出它的尺寸）
                var by = avoidBy;
                if (by != null && !ReferenceEquals(by, exclude))
                {
                    var csb = CellSize(by);
                    Mark(occ, w, h, avoid, csb.X, csb.Y);
                }

                var tsz = CellSize(target); int tw = tsz.X, th = tsz.Y;
                for (int o = 0; o < 2; o++)
                {
                    int rw = o == 0 ? tw : th, rh = o == 0 ? th : tw;
                    if (rw > w || rh > h) continue;
                    for (int x = 0; x + rw <= w; x++)
                        for (int y = 0; y + rh <= h; y++)
                        {
                            bool ok = true;
                            for (int dx = 0; dx < rw && ok; dx++)
                                for (int dy = 0; dy < rh && ok; dy++)
                                    if (occ[x + dx, y + dy]) ok = false;
                            if (ok) return new LocationInGrid(x, y, o == 0 ? ItemRotation.Horizontal : ItemRotation.Vertical);
                        }
                }
            }
            catch { }
            return null;
        }

        private static void Mark(bool[,] occ, int w, int h, LocationInGrid loc, int iw, int ih)
        {
            if (loc.r == ItemRotation.Vertical) { var t = iw; iw = ih; ih = t; }
            for (int x = loc.x; x < loc.x + iw && x < w; x++)
                for (int y = loc.y; y < loc.y + ih && y < h; y++)
                    if (x >= 0 && y >= 0) occ[x, y] = true;
        }

        private static LocationInGrid FindSpot(Grid grid, Item exclude, Item target)
        {
            try
            {
                int w = grid.GridWidth, h = grid.GridHeight;
                bool[,] occ = new bool[w, h];
                foreach (var it in grid.Items)
                {
                    if (ReferenceEquals(it, exclude)) continue;
                    var a = it.CurrentAddress as GridItemAddress;
                    if (a == null) continue;
                    var cs = CellSize(it); int iw = cs.X, ih = cs.Y;
                    if (a.LocationInGrid.r == ItemRotation.Vertical) { var t = iw; iw = ih; ih = t; }
                    for (int x = a.LocationInGrid.x; x < a.LocationInGrid.x + iw && x < w; x++)
                        for (int y = a.LocationInGrid.y; y < a.LocationInGrid.y + ih && y < h; y++)
                            if (x >= 0 && y >= 0) occ[x, y] = true;
                }
                var tsz = CellSize(target); int tw = tsz.X, th = tsz.Y;
                for (int o = 0; o < 2; o++)
                {
                    int rw = o == 0 ? tw : th, rh = o == 0 ? th : tw;
                    if (rw > w || rh > h) continue;
                    for (int x = 0; x + rw <= w; x++)
                        for (int y = 0; y + rh <= h; y++)
                        {
                            bool ok = true;
                            for (int dx = 0; dx < rw && ok; dx++)
                                for (int dy = 0; dy < rh && ok; dy++)
                                    if (occ[x + dx, y + dy]) ok = false;
                            if (ok) return new LocationInGrid(x, y, o == 0 ? ItemRotation.Horizontal : ItemRotation.Vertical);
                        }
                }
            }
            catch { }
            return null;
        }

        /// <summary>换弹/收尾期间背包会锁：锁着时发库存事务会被本地 CanExecute 否掉。</summary>
        private static bool IsInventoryLocked(InventoryController ic, Player player)
        {
            try
            {
                if (ic != null && ic.IsInventoryBlocked()) return true;
                if (player != null)
                {
                    var p = player.GetType().GetProperty("ProcessStatus");
                    var v = p?.GetValue(player, null);
                    if (v != null && v.ToString() != "None") return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>物品在网格里实际占的格数（CompoundItem 会重写 CalculateCellSize，不能直接用 Width/Height）。</summary>
        private static IntVec2 CellSize(Item it)
        {
            try { return it.CalculateCellSize(); }
            catch { return new IntVec2(it.Width, it.Height); }
        }

        private static bool SameCell(ItemAddress a, ItemAddress b)
        {
            try
            {
                if (a == null || b == null) return false;
                var ga = a as GridItemAddress;
                var gb = b as GridItemAddress;
                if (ga == null || gb == null) return ReferenceEquals(a, b);
                if (!ReferenceEquals(ga.Grid, gb.Grid)) return false;
                return ga.LocationInGrid.x == gb.LocationInGrid.x
                    && ga.LocationInGrid.y == gb.LocationInGrid.y
                    && ga.LocationInGrid.r == gb.LocationInGrid.r;
            }
            catch { return false; }
        }

        /// <summary>联机落点：先弹挂/口袋，别再让旧弹匣第一时间就进背包。</summary>
        private static bool TryFindFallbackNear(InventoryController ic, Item item, out ItemAddress addr)
        {
            addr = null;
            try
            {
                var eq = ic.Inventory?.Equipment;
                if (eq == null) return false;
                foreach (var g in eq.GetPrioritizedGridsForUnloadedObject(backpackIncluded: false))
                {
                    if (g == null) continue;
                    var loc = g.FindFreeSpace(item);
                    if (loc == null) continue;
                    addr = g.CreateItemAddress(loc);
                    return true;
                }
                foreach (var g in eq.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                {
                    if (g == null) continue;
                    var loc = g.FindFreeSpace(item);
                    if (loc == null) continue;
                    addr = g.CreateItemAddress(loc);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Fika 事务回执是否成功：没有回执（本地路径）视为成功；失败一律 false。</summary>
        internal static string DescribeTask(Task t)
        {
            try
            {
                if (t == null) return "null-task";
                var sb = "status=" + t.Status;
                var r = t.GetType().GetProperty("Result")?.GetValue(t, null);
                sb += " result=" + (r == null ? "null" : r.GetType().Name);
                if (r != null)
                {
                    var p = r.GetType().GetProperty("Succeed");
                    if (p != null) sb += " succeed=" + p.GetValue(r, null);
                }
                return sb;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        private static bool IsOk(Task t)
        {
            try
            {
                if (t == null) return false;
                if (t.Status != TaskStatus.RanToCompletion) return false;
                var r = t.GetType().GetProperty("Result")?.GetValue(t, null);
                if (r == null) return true;
                var p = r.GetType().GetProperty("Succeed");
                if (p == null) return true;
                return (bool)p.GetValue(r, null);
            }
            catch { return false; }
        }

        private static IEnumerator WaitTask(Task t, float seconds)
        {
            float deadline = UnityEngine.Time.time + seconds;
            int frames = 0;
            int frameCap = Math.Max(60, (int)(seconds * 60f));
            while (t != null && !t.IsCompleted && UnityEngine.Time.time < deadline && frames++ < frameCap)
                yield return null;
        }

        private static T GetField<T>(object obj, string name) where T : class
        {
            var t = obj.GetType();
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            while (t != null)
            {
                try
                {
                    var f = t.GetField(name, F);
                    if (f != null) return f.GetValue(obj) as T;
                }
                catch { }
                try
                {
                    var p = t.GetProperty(name, F);
                    if (p != null && p.CanRead) return p.GetValue(obj, null) as T;
                }
                catch { }
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>旧弹匣要落的格被谁挡着（Blocker==null 表示同格有空位，直接放）。</summary>
        private sealed class Displace
        {
            public Item Blocker;
            public Grid Grid;
            public LocationInGrid Spot;
            public ItemAddress From;
        }
    }
}
