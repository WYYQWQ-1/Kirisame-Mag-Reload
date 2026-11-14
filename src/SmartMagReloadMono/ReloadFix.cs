using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT.InventoryLogic;
using HarmonyLib;

namespace SmartMagReloadMono
{
    /// <summary>
    /// 换弹落点修复（自研，全部走游戏事务）。
    ///
    /// 依据 Player.cs:8804 Run：旧弹匣先落地（8827 Move / 8836 Remove），
    /// 之后 8844 才把新弹匣插进枪。所以"旧弹匣落进新弹匣腾出的格"在 Run 内不可达。
    ///
    /// ① BeforeRun：原版 vestTargetAddress 为 null 时，给它一个此刻空闲的位置
    ///    （弹挂→口袋→背包），让游戏自己搬。
    /// ② 若落点在背包：等 ReloadExternalMagOperation.SwitchToIdlingState（换弹真正结束），
    ///    再把旧弹匣挪回弹挂空位；事务回执后校验真实位置，失败一律 Warning。
    /// </summary>
    internal static class ReloadFix
    {
        private sealed class Pending
        {
            internal InventoryController Controller;
            internal Magazine OldMag;
            internal ItemAddress Target;
            internal float At;
        }

        private static readonly Dictionary<string, Pending> Pendings = new Dictionary<string, Pending>();

        private static Type ResultType => typeof(Player.FirearmController)
            .GetNestedType("ReloadExternalMagResult", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        private static Type OpType => typeof(Player.FirearmController)
            .GetNestedType("ReloadExternalMagOperation", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        internal static void Install(Harmony harmony)
        {
            try
            {
                var t = ResultType;
                if (t == null) { Plugin.Log?.LogError("[KMR] Cannot find ReloadExternalMagResult"); return; }
                var run = AccessTools.Method(t, "Run");
                if (run == null) { Plugin.Log?.LogError("[KMR] Cannot find Run"); return; }
                harmony.Patch(run, prefix: new HarmonyMethod(AccessTools.Method(typeof(ReloadFix), "BeforeRun")));
                Plugin.Log?.LogInfo("[KMR] hook installed: ReloadExternalMagResult.Run");

                var op = OpType;
                var idle = op == null ? null : AccessTools.Method(op, "SwitchToIdlingState");
                if (idle != null)
                {
                    harmony.Patch(idle, postfix: new HarmonyMethod(AccessTools.Method(typeof(ReloadFix), "AfterReloadFinished")));
                    Plugin.Log?.LogInfo("[KMR] hook installed: SwitchToIdlingState (reload finished)");
                }
                else Plugin.Log?.LogWarning("[KMR] SwitchToIdlingState not found: move-back disabled");
            }
            catch (Exception ex) { Plugin.Log?.LogError("[KMR] patch install failed: " + ex); }
        }

        /// <summary>联机用：算一个此刻真实空闲的落点（弹挂→口袋→背包），不改任何物品。</summary>
        internal static bool TryFindFallback(InventoryController ic, Magazine mag, out ItemAddress addr)
        {
            addr = null;
            try
            {
                var equipment = ic?.Inventory?.Equipment;
                if (equipment == null || mag == null) return false;
                foreach (var grid in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                {
                    if (grid == null) continue;
                    var loc = grid.FindFreeSpace(mag);
                    if (loc == null) continue;
                    var candidate = grid.CreateItemAddress(loc);
                    var mv = ItemManipulator.Move(mag, candidate, ic, simulate: true);
                    if (mv.Failed) continue;
                    addr = candidate;
                    return true;
                }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] fallback scan error: " + ex.Message); }
            return false;
        }

        // ───────── Run 前缀：给游戏一个此刻空闲的落点 ─────────
        private static void BeforeRun(ItemController itemController, Weapon weapon, Magazine nextMagazine,
                                      ref ItemAddress vestTargetAddress)
        {
            try
            {
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
                if (Plugin.InFikaSession()) { Plugin.V("A skip: co-op session (plugin idle online)"); return; }

                var main = Comfort.Common.Singleton<GameWorld>.Instance == null
                    ? null : Comfort.Common.Singleton<GameWorld>.Instance.MainPlayer;
                if (main == null || !ReferenceEquals(itemController, main.InventoryController))
                { Plugin.V("A skip: not local player (bot or observed)"); return; }
                if (weapon == null || nextMagazine == null) return;

                // B 方案由 DirectSwap 自己的 Run 前缀负责；这里只在它没接手时做 A 方案兜底
                if (vestTargetAddress != null) return;

                var oldMag = weapon.GetCurrentMagazine();
                if (oldMag == null || ReferenceEquals(oldMag, nextMagazine))
                { Plugin.V("A skip: no old magazine or same magazine"); return; }

                var now = UnityEngine.Time.time;
                foreach (var k in new List<string>(Pendings.Keys))
                    if (now - Pendings[k].At > 60f) Pendings.Remove(k);

                var ic = itemController as InventoryController;
                var equipment = ic?.Inventory?.Equipment;
                if (equipment == null) return;

                var backpackItem = equipment.GetSlot(EquipmentSlot.Backpack)?.ContainedItem;

                foreach (var grid in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                {
                    if (grid == null) continue;
                    LocationInGrid loc = null;
                    try { loc = grid.FindFreeSpace(oldMag); }
                    catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] FindFreeSpace error: " + ex.Message); continue; }
                    if (loc == null) continue;
                    ItemAddress addr = null;
                    try { addr = grid.CreateItemAddress(loc); }
                    catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] CreateItemAddress error: " + ex.Message); continue; }
                    if (addr == null) continue;

                    try
                    {
                        var mv = ItemManipulator.Move(oldMag, addr, itemController, simulate: true);
                        if (mv.Failed) { Plugin.V("candidate " + grid.ID + " simulate failed: " + mv.Error); continue; }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning("[KMR] simulate move error (" + grid.ID + "): " + ex.Message);
                        continue;
                    }

                    vestTargetAddress = addr;
                    bool inBackpack = IsUnder(backpackItem, grid);
                    Plugin.V("vanilla found no target -> using " + OwnerName(grid) + " / " + grid.ID
                        + (inBackpack ? " (backpack)" : ""));
                    if (inBackpack) Remember(ic, oldMag, addr);
                    return;
                }

                Plugin.Log?.LogWarning("[KMR] no free space in vest/pockets/backpack -> vanilla will drop it");
                Plugin.NoSpaceForOldMag = true;   // 由 B 的方案决定是否真的掉地
            }
            catch (Exception ex) { Plugin.Log?.LogError("[KMR] prefix error: " + ex.Message); }
        }

        // ───────── 换弹真正结束：把放进背包的旧弹匣挪回弹挂 ─────────
        /// <summary>换弹真正结束（SwitchToIdlingState）时自增，供联机侧等待。</summary>
        internal static int ReloadFinishedCount;

        private static void AfterReloadFinished(object __instance)
        {
            try { ReloadFinishedCount++; } catch { }
            try
            {
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) { Pendings.Clear(); return; }
                if (Plugin.InFikaSession()) { Pendings.Clear(); return; }   // 联机由 FikaCoopPlacement 负责
                var rf = __instance.GetType().GetField("ReloadExternalMagResult",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var result = rf?.GetValue(__instance);
                var oldMag = result?.GetType().GetField("OldMagazine")?.GetValue(result) as Magazine;
                if (oldMag == null) return;

                Pending p;
                if (!Pendings.TryGetValue(oldMag.Id, out p)) return;
                Pendings.Remove(oldMag.Id);

                DirectSwap.Clear();
                if (p.Target != null && (oldMag.CurrentAddress == null
                    || !ReferenceEquals(oldMag.CurrentAddress.Container, p.Target.Container)))
                {
                    Plugin.Log?.LogInfo("[KMR] move-back skipped: magazine was moved elsewhere");
                    return;
                }
                TryMoveBack(p.Controller, oldMag);
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] finish callback error: " + ex.Message); }
        }

        private static void TryMoveBack(InventoryController ic, Magazine oldMag)
        {
            try
            {
                if (oldMag.CurrentAddress == null)
                {
                    Plugin.Log?.LogWarning("[KMR] move-back skipped: magazine has no owner address");
                    return;
                }
                var equipment = ic?.Inventory?.Equipment;
                if (equipment == null) return;

                foreach (var grid in equipment.GetPrioritizedGridsForUnloadedObject())
                {
                    if (grid == null) continue;
                    var loc = grid.FindFreeSpace(oldMag);
                    if (loc == null) continue;
                    var addr = grid.CreateItemAddress(loc);
                    var mv = ItemManipulator.Move(oldMag, addr, ic, simulate: true);
                    if (mv.Failed) { Plugin.V("move-back simulate failed (" + grid.ID + "): " + mv.Error); continue; }

                    var task = ic.TryRunNetworkTransaction(mv);
                    if (task == null) { Plugin.Log?.LogWarning("[KMR] move-back returned no task"); continue; }

                    var target = addr;
                    task.ContinueWith(t => Verify(oldMag, target,
                        t.Status == TaskStatus.RanToCompletion ? t.Result : null));
                    return;
                }
                Plugin.Log?.LogWarning("[KMR] move-back failed: no free cell in vest/pockets");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] move-back error: " + ex.Message); }
        }

        private static void Verify(Magazine mag, ItemAddress target, IResult result)
        {
            try
            {
                if (result != null && !result.Succeed)
                {
                    Plugin.Log?.LogWarning("[KMR] move-back rejected: " + result.Error);
                    return;
                }
                bool ok = mag.CurrentAddress is GridItemAddress cur
                          && target is GridItemAddress tgt
                          && ReferenceEquals(cur.Container, tgt.Container)
                          && cur.LocationInGrid.x == tgt.LocationInGrid.x
                          && cur.LocationInGrid.y == tgt.LocationInGrid.y;
                if (ok)
                    Plugin.Log?.LogInfo("[KMR] move-back done and verified in " + OwnerName(target) + " / " + target.ContainerName);
                else
                    Plugin.Log?.LogWarning("[KMR] move-back position mismatch: now at " + Describe(mag.CurrentAddress) + "; expected " + target.ContainerName);
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] verify error: " + ex.Message); }
        }

        // ───────── 工具 ─────────
        internal static void Claim(string magId)
        {
            try { Pendings.Remove(magId); } catch { }
        }

        private static void Remember(InventoryController ic, Magazine oldMag, ItemAddress target = null)
        {
            Pendings[oldMag.Id] = new Pending { Controller = ic, OldMag = oldMag, Target = target, At = UnityEngine.Time.time };
            if (Pendings.Count > 8) Pendings.Clear();
        }

        private static bool IsUnder(Item root, Grid grid)
        {
            try
            {
                if (root == null || grid == null) return false;
                Item it = grid?.ParentItem;
                for (int i = 0; i < 8 && it != null; i++)
                {
                    if (ReferenceEquals(it, root)) return true;
                    it = it.CurrentAddress?.Container?.ParentItem;
                }
            }
            catch { }
            return false;
        }

        private static string OwnerName(Grid grid)
        {
            try { return grid?.ParentItem?.ShortName ?? "?"; } catch { return "?"; }
        }

        private static string OwnerName(ItemAddress addr)
        {
            try { return addr?.Container?.ParentItem?.ShortName ?? "?"; } catch { return "?"; }
        }

        private static string Describe(ItemAddress addr)
        {
            try { return addr == null ? "no owner" : OwnerName(addr) + " / " + addr.ContainerName; }
            catch { return "?"; }
        }

        private static void NotifyDropped()
        {
            try
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification(
                    L.T("No room for the old magazine — dropped on the ground", "旧弹匣没地方放，掉在地上了"),
                    EFT.Communications.ENotificationDurationType.Default,
                    EFT.Communications.ENotificationIconType.Alert);
            }
            catch { }
        }
    }
}
