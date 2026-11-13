using System;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace SmartMagReloadMono
{
    /// <summary>
    /// 联机（Fika）下的落点前置：
    /// 在 FikaClientFirearmController.ReloadMag 被调用的瞬间，如果原版没有落点（itemAddress == null），
    /// 就用我们自己的算法算一个真实空位替换掉 —— 这样 Fika 的换弹包里带的就是这个格子，
    /// 所有客户端按同一地址执行，天然一致；不需要我们自己的网络包。
    /// </summary>
    internal static class FikaCoopPlacement
    {
        internal static bool Installed { get; private set; }
        private static float _pendingUntil;
        private static int _pendingSeq;

        internal static void Install(Harmony harmony)
        {
            if (!Plugin.FikaInstalled) return;
            try
            {
                Type ctrl = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name.IndexOf("Fika", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var t in a.GetTypes())
                    {
                        if (t.Name == "FikaClientFirearmController") { ctrl = t; break; }
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

                harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(FikaCoopPlacement), "Before")));
                Installed = true;
                Plugin.Log?.LogInfo("[KMR] co-op hook installed on " + ctrl.FullName + ".ReloadMag");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] co-op placement install failed: " + ex.Message); }
        }

        private static bool Before(object __instance, Magazine magazine, ref ItemAddress itemAddress)
        {
            try
            {
                if (UnityEngine.Time.time < _pendingUntil)
                {
                    Diag.Coop("busy", $"seq={_pendingSeq} waiting for previous swap ack");
                    return false;   // 上一次交换还没落地，忽略这次按 R
                }
                if (itemAddress != null) return true;
                if (magazine == null || __instance == null) return true;
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return true;
                if (!Plugin.InFikaSession()) return true;   // 单机不由这条路径处理
                if (Plugin.UiFixesDelegated) { Diag.Coop("skip", "UIFixes is handling in-place reload"); return true; }
                bool allowSwap = !Plugin.UiFixesDelegated;

                var player = GetField<Player>(__instance, "_player") ?? GetField<Player>(__instance, "player");
                var weapon = GetField<Weapon>(__instance, "Item") ?? GetField<Weapon>(__instance, "_item");
                if (player == null || weapon == null) { Plugin.V("co-op placement: player/weapon not resolved"); return true; }
                if (!player.IsYourPlayer) return true;   // 只管本地玩家，别碰 bot / 观察副本

                var ic = player.InventoryController;
                var current = weapon.GetCurrentMagazine();
                if (current == null || ReferenceEquals(current, magazine)) return true;

                // 跳过的原版流程里会"给空膛补一发"；膛内没弹时不走 swap，交给落点兜底（原版会补弹）
                if (weapon.ChamberAmmoCount <= 0) return true;

                // 1) 先看能不能"原地交换"：旧弹匣要能塞进新弹匣现在占的那格
                var dest = magazine.CurrentAddress as GridItemAddress;
                var gunSlot = weapon.GetMagazineSlot();
                if (magazine.IsMagazineWithBelt) return true;
                if (dest != null && gunSlot != null)
                {
                    var gunAddr = gunSlot.CreateItemAddress();
                    try
                    {
                        if (!allowSwap) { Diag.Coop("no-swap", "deferred to UIFixes"); }
                        else { var probe = ItemManipulator.Swap(current, dest, magazine, gunAddr, ic, simulate: true);
                        if (!probe.Failed)
                        {
                            var seq = Diag.NextSeq();
                            var task = ic.TryRunNetworkTransaction(probe);
                            _pendingSeq = seq;
                            _pendingUntil = UnityEngine.Time.time + 2f;
                            Diag.Coop("swap-submitted", $"seq={seq} old={Diag.Item(current)} new={Diag.Item(magazine)} dest={Diag.Addr(dest)} gun={Diag.Addr(gunAddr)}");
                            if (task != null)
                            {
                                task.ContinueWith(t =>
                                {
                                    try
                                    {
                                        if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                                        {
                                            var r = t.Result;
                                            if (r != null && r.Succeed) Diag.Coop("swap-ok", $"seq={seq}");
                                            else Diag.Coop("swap-failed", $"seq={seq} error={(r == null ? "no-result" : r.Error)}");
                                        }
                                        else Diag.Coop("swap-failed", $"seq={seq} task={t.Status}");
                                    }
                                    catch (Exception ex) { Diag.Coop("swap-failed", $"seq={seq} ex={ex.Message}"); }
                                });
                            }
                            return false;   // 跳过原版换弹（没有拉栓动画，但两端一致）
                        }
                        Diag.Coop("swap-rejected", $"old={Diag.Item(current)} new={Diag.Item(magazine)} dest={Diag.Addr(dest)} error={probe.Error}");
                        }
                    }
                    catch (Exception ex) { Plugin.V("co-op swap probe error: " + ex.Message); }
                }

                // 2) 交换不行 ⇒ 只兜底落点，让所有端落在同一格
                if (ReloadFix.TryFindFallback(ic, current, out var addr) && addr != null)
                {
                    itemAddress = addr;
                    Diag.Coop("placement", $"old={Diag.Item(current)} new={Diag.Item(magazine)} dest={Diag.Addr(addr)}");
                }
                else
                {
                    Diag.Coop("placement-none", $"old={Diag.Item(current)} new={Diag.Item(magazine)} (vanilla will drop it on all clients)");
                }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][coop] error: " + ex.Message); }
            return true;
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
    }
}
