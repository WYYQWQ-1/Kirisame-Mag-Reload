using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace SmartMagReloadMono
{
    /// <summary>
    /// 联机弹匣保留（本地 Add 版；架构参考 Tosox 的 MagRetentionReload，独立实现）。
    ///
    /// 为什么不能用“事后网络事务”：
    ///   旧弹匣被原版 Remove 之后 CurrentAddress == null
    ///   · Move 第一句取 item.Parent ⇒ 抛
    ///   · Add 的结果没有可转换的 Operation（引擎缺 AddResult 分支）
    ///   两条都会被 catch 吞成“看起来像网络失败”，实际弹匣变成孤儿 ⇒ 比掉地更糟。
    ///
    /// 所以这套只在**本地**做一次 Add（Add 能认领无主物品），并且：
    ///   · 不搬别的物品（只做落点：原位 → 弹挂/口袋空位）
    ///   · AddResult 挂回换弹结果，RaiseEvents 跟着换弹走 ⇒ 不会出现黑色未搜索格
    ///   · DropMod 拦截：已经放好就不再掉地
    ///   · 两端各自执行同一套逻辑（与 Tosox / 1.1.0 同模型），公告包只做版本握手与通报
    /// </summary>
    internal static class Retention
    {
        internal sealed class State
        {
            internal ItemAddress FreedSlot;
        }

        private static readonly ConditionalWeakTable<Player.FirearmController.ReloadExternalMagResult, AddResult> Adds
            = new ConditionalWeakTable<Player.FirearmController.ReloadExternalMagResult, AddResult>();

        internal static void Install(Harmony harmony)
        {
            try
            {
                var t = typeof(Player.FirearmController.ReloadExternalMagResult);
                var run = AccessTools.Method(t, "Run");
                if (run != null)
                {
                    harmony.Patch(run,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(Retention), nameof(BeforeRun))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(Retention), nameof(AfterRun))));
                    Plugin.Log?.LogInfo("[KMR] retention hook installed: ReloadExternalMagResult.Run");
                }
                var raise = AccessTools.Method(t, "RaiseEvents");
                if (raise != null)
                {
                    harmony.Patch(raise, postfix: new HarmonyMethod(AccessTools.Method(typeof(Retention), nameof(AfterRaiseEvents))));
                    Plugin.Log?.LogInfo("[KMR] retention hook installed: ReloadExternalMagResult.RaiseEvents");
                }
                var op = typeof(Player.FirearmController.ReloadExternalMagOperation);
                var dropMod = AccessTools.Method(op, "DropMod");
                if (dropMod != null)
                {
                    harmony.Patch(dropMod, prefix: new HarmonyMethod(AccessTools.Method(typeof(Retention), nameof(BeforeDropMod))));
                    Plugin.Log?.LogInfo("[KMR] retention hook installed: ReloadExternalMagOperation.DropMod");
                }
                else Plugin.Log?.LogWarning("[KMR] DropMod not found: old magazine may also drop");
                Plugin.Log?.LogInfo("[KMR] retention hooks installed (local add stash)");
            }
            catch (Exception ex) { Plugin.Log?.LogError("[KMR] retention install failed: " + ex.Message); }
        }

        /// <summary>唯一的“这次换弹由 Retention 负责”判定：别人（DirectSwap/ReloadFix/CoopPlacement）都看这一个。</summary>
        internal static bool HandlesThisReload()
        {
            try
            {
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return false;
                if (Plugin.CoopPathActive()) return false;              // CoopZeroGap 那条路自己处理
                if (Plugin.UiFixesDelegated) return false;               // UIFixes 接管时让路
                return Plugin.RetentionOn() && Plugin.CoopSession() && !CoopSync.Incompatible;
            }
            catch { return false; }
        }

        private static bool Active() => HandlesThisReload();

        // ---------------------------------------------------------------- prefix

        private static void BeforeRun(ItemController itemController, Weapon weapon, Magazine nextMagazine,
                                      bool quickReload, ref ItemAddress vestTargetAddress, ref State __state)
        {
            try
            {
                if (quickReload) return;
                if (!Active() || weapon == null || nextMagazine == null) return;
                var player = (itemController as Player.PlayerInventoryController)?.Player;
                if (player == null || !DirectSwap.IsPlayerOwnedController(itemController)) return;
                var oldMag = weapon.GetCurrentMagazine();
                if (oldMag == null || ReferenceEquals(oldMag, nextMagazine)) return;
                var freed = nextMagazine.CurrentAddress;
                if (freed == null) return;

                // ★ 决策闸门（观察端）：只有在对方宣布“保留这个弹匣”时才动手；
                //   拿不到宣布 ⇒ 什么都不改，交原版按包里的落点处理（不会掉地）
                if (!player.IsYourPlayer && !CoopSync.TakeAnnouncement(player.ProfileId, nextMagazine.Id))
                {
                    // ★ 空手走原版会让观察端 Remove→DropMod→ThrowItem，地上多一个副本
                    //   ⇒ 至少给一个落点，让原版把旧弹匣 Move 过去（不会掉地）
                    try { vestTargetAddress = freed; } catch { }
                    Plugin.Log?.LogInfo("[KMR][ret] observer: no announce for " + player.ProfileId + ", using packet landing");
                    return;
                }

                __state = new State { FreedSlot = freed };
                vestTargetAddress = null;              // 原版只 Remove，落点由我们在后缀补
                Plugin.Log?.LogInfo("[KMR][ret] plan: old -> freed slot in " + freed.ContainerName);
                // ★ 决策对齐：本端宣布“这次保留”，观察端据此决定要不要跟着保留（Tosox 的 TakeAnnouncement）
                try { if (player.IsYourPlayer) CoopSync.Announce(player.ProfileId, nextMagazine.Id); }
                catch { }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][ret] prefix: " + ex.Message); }
        }

        // ---------------------------------------------------------------- postfix

        private static void AfterRun(ItemController itemController, State __state,
                                     ref Option<Player.FirearmController.ReloadExternalMagResult> __result)
        {
            try
            {
                if (__state == null || __result.Failed) return;   // 门在前缀已经判过
                var res = __result.Value;
                if (res == null || res.RemoveOldMagResult == null) return;
                var oldMag = res.RemoveOldMagResult.Item as Magazine;
                if (oldMag == null) return;
                Adds.Remove(res);

                Adds.Remove(res);
                AddResult placed = null;
                ItemAddress at = null;

                // ① 新弹匣腾出的那格
                if (__state.FreedSlot != null)
                {
                    var a0 = ItemManipulator.Add(oldMag, __state.FreedSlot, itemController, simulate: false);
                    if (!a0.Failed) { placed = a0.Value; at = __state.FreedSlot; }
                    else Plugin.Log?.LogInfo("[KMR][ret] reject freed slot: " + a0.Error);
                }

                // ② 弹挂的现成空位（还不搬东西）
                if (placed == null)
                {
                    var eqv = (itemController as InventoryController)?.Inventory?.Equipment;
                    placed = TryAddIn(oldMag, itemController, new List<List<Grid>> { VestGrids(eqv) }, out at);
                }

                // ③ 弹挂里腾位置：把挡路件挪走，再放（单机行为就在这里）
                if (placed == null)
                {
                    placed = TryShuffleAndPlace(oldMag, __state.FreedSlot, itemController);
                    if (placed != null) at = oldMag.CurrentAddress;
                }

                // ④ 口袋
                if (placed == null)
                {
                    var eqp = (itemController as InventoryController)?.Inventory?.Equipment;
                    placed = TryAddIn(oldMag, itemController, new List<List<Grid>> { PocketGrids(eqp) }, out at);
                }

                // ⑤ 背包（最后兜底）
                if (placed == null)
                {
                    var eqb = (itemController as InventoryController)?.Inventory?.Equipment;
                    placed = TryAddIn(oldMag, itemController, new List<List<Grid>> { BackpackGrids(eqb) }, out at);
                }

                if (placed != null)
                {
                    Adds.Add(res, placed);
                    Plugin.Log?.LogInfo("[KMR][ret] stowed old magazine into " + (at != null ? at.ContainerName : "?"));
                    return;
                }

                Plugin.Log?.LogWarning("[KMR][ret] no place for the old magazine -> vanilla will drop it");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][ret] postfix: " + ex.Message); }
        }

        /// <summary>在给定的几组格子里找现成空位放旧弹匣（不搬东西）。</summary>
        private static AddResult TryAddIn(Magazine oldMag, ItemController ic, List<List<Grid>> groups, out ItemAddress at)
        {
            at = null;
            try
            {
                var seen = new List<Grid>();
                foreach (var group in groups)
                    foreach (var g in group)
                    {
                        if (g == null || seen.Contains(g)) continue;
                        seen.Add(g);
                        var loc = g.FindLocationForItem(oldMag);
                        if (loc == null) continue;
                        var add = ItemManipulator.Add(oldMag, loc, ic, simulate: false);
                        if (add.Failed) continue;
                        at = loc;
                        return add.Value;
                    }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][ret] add-in: " + ex.Message); }
            return null;
        }

        private static List<Grid> VestGrids(InventoryEquipment eq)
        {
            var list = new List<Grid>();
            try
            {
                var vest = eq.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem as CompoundItem;
                if (vest != null) foreach (var c in vest.Containers) if (c is Grid g) list.Add(g);
            }
            catch { }
            return list;
        }

        private static List<Grid> PocketGrids(InventoryEquipment eq)
        {
            var list = new List<Grid>();
            try
            {
                var p = eq.GetSlot(EquipmentSlot.Pockets)?.ContainedItem as CompoundItem;
                if (p != null) foreach (var c in p.Containers) if (c is Grid g) list.Add(g);
            }
            catch { }
            return list;
        }

        private static List<Grid> BackpackGrids(InventoryEquipment eq)
        {
            var list = new List<Grid>();
            try
            {
                var bp = eq.GetSlot(EquipmentSlot.Backpack)?.ContainedItem as CompoundItem;
                if (bp != null) foreach (var c in bp.Containers) if (c is Grid g) list.Add(g);
            }
            catch { }
            return list;
        }

        /// <summary>单机同款重排（真移动，成功后旧弹匣就能放进去）。</summary>
        private static AddResult? TryShuffleAndPlace(Magazine oldMag, ItemAddress freedSlot, ItemController ic)
        {
            try
            {
                var inv = ic as InventoryController;
                var eq = inv?.Inventory?.Equipment;
                if (eq == null) return null;

                var rig = new List<Grid>();
                foreach (var g in eq.GetPrioritizedGridsForUnloadedObject(backpackIncluded: false))
                    if (g != null && !rig.Contains(g)) rig.Add(g);
                var pockets = new List<Grid>();
                var pocketItem = eq.GetSlot(EquipmentSlot.Pockets)?.ContainedItem as CompoundItem;
                if (pocketItem != null)
                    foreach (var c in pocketItem.Containers)
                        if (c is Grid pg && !pockets.Contains(pg)) pockets.Add(pg);

                // ① 挡路件塞进“腾出的那格”，旧弹匣占它的位置
                var freedGridAddr = freedSlot as GridItemAddress;
                if (freedGridAddr != null)
                {
                    foreach (var g in rig)
                        foreach (var x in CandidateItems(g))
                        {
                            if (!CanRelocate(x)) continue;
                            if (!OccFits(freedGridAddr.Grid, x, freedGridAddr.LocationInGrid)) continue;
                            var xOld = x.CurrentAddress;
                            if (xOld == null) continue;

                            // ★ 同格：x 就来自这一格 ⇒ 算落点时要把它画到腾出格上，否则会盖住它
                            bool sameGrid = ReferenceEquals(g, freedGridAddr.Grid);
                            var slot = sameGrid
                                ? FindSpot(g, x, oldMag, x, freedGridAddr.LocationInGrid)
                                : FindSpot(g, x, oldMag);
                            if (slot == null) continue;

                            var mv = ItemManipulator.Move(x, freedSlot, ic, simulate: false);
                            if (mv.Failed) continue;
                            RaiseMove(mv, ic);                       // ★ 替位件也要发事件，否则界面不刷新

                            var add = ItemManipulator.Add(oldMag, g.CreateItemAddress(slot), ic, simulate: false);
                            if (!add.Failed)
                            {
                                Plugin.Log?.LogInfo("[KMR][ret] shuffle A: " + x.ShortName + " -> " + freedSlot.ContainerName);
                                return add.Value;
                            }

                            var back = ItemManipulator.Move(x, xOld, ic, simulate: false);
                            RaiseMove(back, ic);
                            if (back.Failed)
                            {
                                Plugin.Log?.LogWarning("[KMR][ret] shuffle A: rollback FAILED for " + x.ShortName + " (left at " + (x.CurrentAddress?.ContainerName ?? "null") + ")");
                                return null;
                            }
                        }
                }

                // ② 某件挪到弹挂/口袋别处，旧弹匣占它的位置
                foreach (var g in rig)
                    foreach (var x in CandidateItems(g))
                    {
                        if (!CanRelocate(x)) continue;
                        var xOld = x.CurrentAddress;
                        if (xOld == null) continue;
                        var slot = FindSpot(g, x, oldMag);
                        if (slot == null) continue;

                        foreach (var og in AllGrids(rig, pockets))
                        {
                            if (ReferenceEquals(og, g)) continue;
                            var loc = FindSpot(og, null, x);
                            if (loc == null) continue;
                            var mv = ItemManipulator.Move(x, og.CreateItemAddress(loc), ic, simulate: false);
                            if (mv.Failed) continue;
                            RaiseMove(mv, ic);

                            var add = ItemManipulator.Add(oldMag, g.CreateItemAddress(slot), ic, simulate: false);
                            if (!add.Failed)
                            {
                                Plugin.Log?.LogInfo("[KMR][ret] shuffle B: " + x.ShortName + " -> " + og.ID);
                                return add.Value;
                            }

                            var back = ItemManipulator.Move(x, xOld, ic, simulate: false);
                            RaiseMove(back, ic);
                            if (back.Failed)
                            {
                                Plugin.Log?.LogWarning("[KMR][ret] shuffle B: rollback FAILED for " + x.ShortName);
                                return null;
                            }
                        }
                    }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][ret] shuffle: " + ex.Message); }
            return null;
        }

        /// <summary>候选顺序：小的先、再按 Id —— 两端同序。</summary>
        private static List<Item> CandidateItems(Grid g)
        {
            var list = new List<Item>();
            foreach (var it in g.Items) list.Add(it);
            list.Sort((a, b) =>
            {
                int ca = Area(a), cb = Area(b);
                if (ca != cb) return ca.CompareTo(cb);
                return string.CompareOrdinal(a.Id, b.Id);
            });
            return list;
        }

        /// <summary>替位件的移动结果原本没人接（会被丢弃），必须补发事件，否则界面/搜索状态不刷新。</summary>
        private static void RaiseMove(OperationResult<MoveResult> mv, ItemController ic)
        {
            try
            {
                if (mv.Failed) return;
                var r = mv.Value;
                if (r == null) return;
                r.RaiseEvents(ic, CommandStatus.Begin);
                r.RaiseEvents(ic, CommandStatus.Succeed);
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][ret] raise move: " + ex.Message); }
        }

        private static bool CanRelocate(Item x)
        {
            try
            {
                if (x == null) return false;
                if (x.PinLockState != EItemPinLockState.Free) return false;
                if (x.QuestItem) return false;
                if (!x.CanBePickedUp) return false;
                var a = x.CurrentAddress as GridItemAddress;
                return a != null;
            }
            catch { return false; }
        }

        private static List<Grid> AllGrids(List<Grid> rig, List<Grid> pockets)
        {
            var all = new List<Grid>(rig);
            foreach (var g in pockets) if (!all.Contains(g)) all.Add(g);
            return all;
        }

        private static int Area(Item it)
        {
            try { var cs = CellSize(it); return cs.X * cs.Y; } catch { return 0; }
        }

        private static IntVec2 CellSize(Item it)
        {
            try { return it.CalculateCellSize(); } catch { return new IntVec2(1, 1); }
        }

        private static LocationInGrid FindSpot(Grid grid, Item exclude, Item target)
            => FindSpot(grid, exclude, target, null, default(LocationInGrid));

        /// <summary>
        /// 纯占位图：exclude 当作不存在；placeItem 按 placeAt 额外标记为占用。
        /// 同格重排（x 从本格挪到腾出格）必须把 x 画到新位置上，否则算出来的落点会盖住它。
        /// </summary>
        private static LocationInGrid FindSpot(Grid grid, Item exclude, Item target, Item placeItem, LocationInGrid placeAt)
        {
            try
            {
                int w = grid.GridWidth, h = grid.GridHeight;
                if (w <= 0 || h <= 0) return null;
                var occ = new bool[w, h];
                foreach (var it in grid.Items)
                {
                    if (ReferenceEquals(it, exclude)) continue;
                    var a = it.CurrentAddress as GridItemAddress;
                    if (a == null) continue;
                    Paint(occ, grid, it, a.LocationInGrid);
                }
                if (placeItem != null) PaintAt(occ, grid, placeItem, placeAt);

                var tcs = CellSize(target);
                for (int o = 0; o < 2; o++)
                {
                    int rw = o == 0 ? tcs.X : tcs.Y, rh = o == 0 ? tcs.Y : tcs.X;
                    if (rw > w || rh > h) continue;
                    for (int x = 0; x + rw <= w; x++)
                        for (int y = 0; y + rh <= h; y++)
                            if (Free(occ, x, y, rw, rh))
                                return new LocationInGrid(x, y, o == 0 ? ItemRotation.Horizontal : ItemRotation.Vertical);
                }
            }
            catch { }
            return null;
        }

        private static bool OccFits(Grid grid, Item item, LocationInGrid at)
        {
            try
            {
                int w = grid.GridWidth, h = grid.GridHeight;
                var occ = new bool[w, h];
                foreach (var it in grid.Items)
                {
                    var a = it.CurrentAddress as GridItemAddress;
                    if (a == null) continue;
                    Paint(occ, grid, it, a.LocationInGrid);
                }
                var cs = CellSize(item);
                int rw = at.r == ItemRotation.Vertical ? cs.Y : cs.X;
                int rh = at.r == ItemRotation.Vertical ? cs.X : cs.Y;
                if (at.x + rw > w || at.y + rh > h) return false;
                for (int x = at.x; x < at.x + rw; x++)
                    for (int y = at.y; y < at.y + rh; y++)
                        if (occ[x, y]) return false;
                return true;
            }
            catch { return false; }
        }

        private static void PaintAt(bool[,] occ, Grid grid, Item it, LocationInGrid at)
        {
            try
            {
                var cs = CellSize(it);
                int iw = at.r == ItemRotation.Vertical ? cs.Y : cs.X;
                int ih = at.r == ItemRotation.Vertical ? cs.X : cs.Y;
                for (int x = at.x; x < at.x + iw && x < grid.GridWidth; x++)
                    for (int y = at.y; y < at.y + ih && y < grid.GridHeight; y++)
                        if (x >= 0 && y >= 0) occ[x, y] = true;
            }
            catch { }
        }

        private static void Paint(bool[,] occ, Grid grid, Item it, LocationInGrid at)
        {
            try
            {
                var cs = CellSize(it);
                int iw = at.r == ItemRotation.Vertical ? cs.Y : cs.X;
                int ih = at.r == ItemRotation.Vertical ? cs.X : cs.Y;
                for (int x = at.x; x < at.x + iw && x < grid.GridWidth; x++)
                    for (int y = at.y; y < at.y + ih && y < grid.GridHeight; y++)
                        if (x >= 0 && y >= 0) occ[x, y] = true;
            }
            catch { }
        }

        private static bool Free(bool[,] occ, int ox, int oy, int rw, int rh)
        {
            for (int x = ox; x < ox + rw; x++)
                for (int y = oy; y < oy + rh; y++)
                    if (occ[x, y]) return false;
            return true;
        }

        private static IEnumerable<ItemAddress> Targets(Magazine oldMag, ItemAddress freedSlot, ItemController ic)
        {
            if (freedSlot != null) yield return freedSlot;
            var eq = (ic as InventoryController)?.Inventory?.Equipment;
            if (eq == null) yield break;
            foreach (var grid in eq.GetPrioritizedGridsForUnloadedObject(backpackIncluded: false))
            {
                if (grid == null) continue;
                var a = grid.FindLocationForItem(oldMag);
                if (a != null) yield return a;
            }
            // ★ 兜底：弹挂/口袋都放不下时再考虑背包（单机也是这么做的，别出现背包空着却掉地）
            foreach (var grid in eq.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
            {
                if (grid == null) continue;
                var a = grid.FindLocationForItem(oldMag);
                if (a != null) yield return a;
            }
        }

        private static void AfterRaiseEvents(Player.FirearmController.ReloadExternalMagResult __instance,
                                             ItemController controller, CommandStatus status)
        {
            try
            {
                AddResult add;
                if (!Adds.TryGetValue(__instance, out add) || add == null) return;
                add.RaiseEvents((IItemOwner)controller, status);
                if ((int)status == 1 || (int)status == 2) Adds.Remove(__instance);
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][ret] raise: " + ex.Message); }
        }

        private static bool BeforeDropMod(Player.FirearmController.ReloadExternalMagOperation __instance,
                                          Item droppedMod, EWeaponModType modType)
        {
            try
            {
                if ((int)modType != 16 || droppedMod == null) return true;
                var res = __instance.ReloadExternalMagResult;
                if (res == null || res.RemoveOldMagResult == null) return true;
                if (!ReferenceEquals(res.RemoveOldMagResult.Item, droppedMod)) return true;
                AddResult add;
                if (Adds.TryGetValue(res, out add)) return false;     // ★ 已放好就绝不许掉（先判这个，再谈开关）
                if (!Active()) return true;
                Plugin.Log?.LogWarning("[KMR][ret] old magazine is being dropped (not stowed)");
                try
                {
                    var player = Comfort.Common.Singleton<GameWorld>.Instance == null ? null
                        : Comfort.Common.Singleton<GameWorld>.Instance.MainPlayer;
                    if (player != null && player.IsYourPlayer) DirectSwap.NotifyNoSpace();
                }
                catch { }
                return true;
            }
            catch { return true; }
        }
    }
}
