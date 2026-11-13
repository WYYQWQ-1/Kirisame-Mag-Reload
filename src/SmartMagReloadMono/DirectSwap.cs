using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using Diz.LanguageExtensions;
using HarmonyLib;

namespace SmartMagReloadMono
{
    /// <summary>
    /// B 方案：真·就地换位（自研）。
    ///
    /// 为什么 Swap 不能放在 Run 之前：Run 第一行就取走了 currentMagazine（Player.cs:8808），
    /// 提前交换会让后面那次"搬旧弹匣"搬成新弹匣。正确位置是拦 ItemManipulator.Move
    ///（Player.cs:8827 那一次）：在那里真做 Swap，并把两半包装成 MoveResult 交回给 Run。
    /// 8844 插入新弹匣那次同样吃掉，返回第二半结果 ⇒ Run 产出与原版结构等价的结果，
    /// 动画、膛内弹、事件、技能结算全部走原版。
    /// </summary>
    internal static class DirectSwap
    {
        private sealed class Plan
        {
            internal Magazine OldMag;
            internal Magazine NewMag;
            internal ItemAddress Dest;
            internal SlotItemAddress GunAddr;
            internal Slot GunSlot;
            internal ItemAddress VanillaTarget;
            internal ItemController Controller;
            internal bool Reorder;
            internal Item Displace;
            internal Grid DisplaceGrid;
            internal LocationInGrid DrumSpot;
            internal ItemAddress DisplaceFrom;
        }

        private sealed class Pair
        {
            internal SwapResult Swap;
            internal MoveResult OldMove;
            internal MoveResult NewMove;
            internal bool RolledBack;
            internal readonly List<System.Action> Rollbacks = new List<System.Action>();
        }

        private static readonly Dictionary<MoveResult, Pair> Pairs = new Dictionary<MoveResult, Pair>();
        private static Plan _plan;
        private static bool _swapped;
        private static string _mode = "none";
        private static Item _displace;
        private static Grid _displaceGrid;
        private static LocationInGrid _drumSpot;
        private static ItemAddress _displaceFrom;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var t = typeof(Player.FirearmController).GetNestedType("ReloadExternalMagResult",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var run = t == null ? null : AccessTools.Method(t, "Run");
                if (run != null)
                {
                    harmony.Patch(run,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(DirectSwap), "BeforeRun")),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(DirectSwap), "AfterRun")),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(DirectSwap), "OnRunFinal")));
                    Plugin.Log?.LogInfo("[KMR] B: hook Run (probe/finish)");
                }

                var move = AccessTools.Method(typeof(ItemManipulator), "Move",
                    new[] { typeof(Item), typeof(ItemAddress), typeof(ItemController), typeof(bool) });
                if (move != null)
                {
                    harmony.Patch(move, prefix: new HarmonyMethod(AccessTools.Method(typeof(DirectSwap), "BeforeMove")));
                    Plugin.Log?.LogInfo("[KMR] B: hook Move (bridge swap)");
                }

                var rollback = AccessTools.Method(typeof(MoveResult), "RollBack", Type.EmptyTypes);
                if (rollback != null)
                {
                    harmony.Patch(rollback, prefix: new HarmonyMethod(AccessTools.Method(typeof(DirectSwap), "BeforeRollback")));
                    Plugin.Log?.LogInfo("[KMR] B: hook MoveResult.RollBack (idempotent)");
                }
            }
            catch (Exception ex) { Plugin.Log?.LogError("[KMR] B patch install failed: " + ex); }
        }

        // ───── Run 前缀只做探针，不搬任何东西 ─────
        private static void BeforeRun(ItemController itemController, Weapon weapon, Magazine nextMagazine,
                                      bool quickReload, ref ItemAddress vestTargetAddress)
        {
            try
            {
                _plan = null;
                _swapped = false;
                _mode = "none";
                Plugin.NoSpaceForOldMag = false;
                Pairs.Clear();
                _displace = null; _displaceGrid = null; _drumSpot = null; _displaceFrom = null;
                var main0 = Comfort.Common.Singleton<GameWorld>.Instance == null ? null : Comfort.Common.Singleton<GameWorld>.Instance.MainPlayer;
                Plugin.V($"[B] entry: qr={quickReload} weapon={(weapon != null)} next={(nextMagazine == null ? "null" : nextMagazine.ShortName)} " +
                    $"ic={(itemController != null)} isMain={(main0 != null && ReferenceEquals(itemController, main0.InventoryController))} " +
                    $"old={(weapon?.GetCurrentMagazine()?.ShortName ?? "null")} vta={(vestTargetAddress != null)}");
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
                if (Plugin.FikaInstalled && Plugin.InFikaSession()) { Plugin.V("B skip: co-op session"); return; }
                if (Plugin.DirectSwapEnabled == null || !Plugin.DirectSwapEnabled.Value) return;
                if (Plugin.UiFixesDelegated) { Plugin.V("B defer: UIFixes in-place reload is ON"); return; }
                if (quickReload) { Plugin.Log?.LogInfo("[KMR][B] B skip: quick reload"); return; }
                if (weapon == null || nextMagazine == null || itemController == null)
                { Plugin.Log?.LogInfo("[KMR][B] skip: null argument"); return; }
                var main = Comfort.Common.Singleton<GameWorld>.Instance == null ? null : Comfort.Common.Singleton<GameWorld>.Instance.MainPlayer;
                if (main == null || !ReferenceEquals(itemController, main.InventoryController)) { Plugin.Log?.LogInfo("[KMR][B] B skip: not local player"); return; }

                var oldMag = weapon.GetCurrentMagazine();
                if (oldMag == null || ReferenceEquals(oldMag, nextMagazine))
                { Plugin.Log?.LogInfo("[KMR][B] skip: no old magazine or same magazine"); return; }

                var dest = nextMagazine.CurrentAddress as GridItemAddress;
                if (dest == null) { Plugin.Log?.LogInfo("[KMR][B] B skip: new magazine not in a grid"); return; }
                if (!ReferenceEquals(dest.Grid.GetItemAt(dest.LocationInGrid), nextMagazine)) { Plugin.Log?.LogInfo("[KMR][B] B skip: grid cell mismatch"); return; }
                if (!ReferenceEquals(dest.GetOwner(), itemController)) { Plugin.Log?.LogInfo("[KMR][B] B skip: different owner"); return; }

                var gunSlot = weapon.GetMagazineSlot();
                if (gunSlot == null) { Plugin.Log?.LogInfo("[KMR][B] B skip: no magazine slot"); return; }
                var gunAddr = gunSlot.CreateItemAddress() as SlotItemAddress;
                if (gunAddr == null) { Plugin.Log?.LogInfo("[KMR][B] B skip: gun slot address type mismatch"); return; }
                if (nextMagazine.IsMagazineWithBelt) { Plugin.Log?.LogInfo("[KMR][B] B skip: belt-fed magazine"); return; }

                var rot = dest.LocationInGrid.r;
                IntVec2 a, b;
                try { a = oldMag.CalculateRotatedSize(rot); b = nextMagazine.CalculateRotatedSize(rot); }
                catch (Exception ex) { Plugin.Log?.LogInfo("[KMR][B] skip: size calc failed " + ex.Message); return; }
                bool fits = (a.X <= b.X && a.Y <= b.Y);
                try { if (!dest.Grid.CheckCompatibility(oldMag)) { Plugin.Log?.LogInfo("[KMR][B] B skip: incompatible grid"); return; } }
                catch { }

                _mode = fits ? "solo-swap" : "solo-reorder";
                if (fits)
                {
                    var probe = ItemManipulator.Swap(oldMag, dest, nextMagazine, gunAddr, itemController, simulate: true);
                    if (probe.Failed) { Plugin.Log?.LogInfo("[KMR][B] skip: swap probe failed " + probe.Error); return; }
                    Plugin.Log?.LogInfo($"[KMR][B] plan swap: {oldMag.ShortName} ⇄ {nextMagazine.ShortName} (grid {dest.Grid.ID})");
                }
                else
                {
                    var directSpot = FindSpot(dest.Grid, nextMagazine, oldMag);
                    if (directSpot != null) { _drumSpot = directSpot; _displaceGrid = dest.Grid; }
                    if (directSpot == null && !FindDisplacePlan(dest, nextMagazine, oldMag, itemController))
                    {
                        Plugin.Log?.LogInfo($"[KMR][B] skip: old magazine {a.X}x{a.Y} does not fit the area vacated by the new magazine (grid {dest.Grid.ID})");
                        return;
                    }
                    Plugin.Log?.LogInfo($"[KMR][B] plan reorder: insert {nextMagazine.ShortName} into gun, then place {oldMag.ShortName} into {dest.Grid.ID}");
                }

                _plan = new Plan
                {
                    OldMag = oldMag,
                    NewMag = nextMagazine,
                    Dest = dest,
                    GunAddr = gunAddr,
                    GunSlot = gunSlot,
                    VanillaTarget = vestTargetAddress,
                    Controller = itemController,
                    Reorder = !fits,
                    Displace = _displace,
                    DisplaceGrid = _displaceGrid,
                    DrumSpot = _drumSpot,
                    DisplaceFrom = _displaceFrom,
                };
                vestTargetAddress = dest;
                ReloadFix.Claim(oldMag.Id);
            }
            catch (Exception ex) { _plan = null; Plugin.Log?.LogWarning("[KMR][B] prefix error: " + ex.Message); }
        }

        // ───── 拦 Move：8827 时真做 Swap ─────
        private static bool BeforeMove(Item item, ItemAddress to, ItemController itemController, bool simulate,
                                       ref OperationResult<MoveResult> __result)
        {
            try
            {
                var plan = _plan;
                if (plan == null || simulate || item == null || itemController == null) return true;

                if (ReferenceEquals(item, plan.OldMag) && SameCell(to, plan.Dest))
                {
                    if (plan.Reorder) return ReorderOldMag(plan, itemController, ref __result);

                    var sw = ItemManipulator.Swap(plan.OldMag, plan.Dest, plan.NewMag, plan.GunAddr, itemController, simulate: false);
                    if (sw.Failed)
                    {
                        Plugin.Log?.LogWarning("[KMR][B] swap failed: " + sw.Error);
                        if (TryFallback(plan, itemController, ref __result)) return false;
                        _plan = null;
                        return true;
                    }
                    var v = sw.Value;
                    var oldMove = new MoveResult(v.Item, v._from, v.To, itemController,
                        v._destroyResult1, v._removeResult1, v._addResult1, null, null, null);
                    var newMove = new MoveResult(v.Item2, v._from2, v.To2, itemController,
                        v._destroyResult2, v._removeResult2, v._addResult2, null, null, null);
                    var pair = new Pair { Swap = v, OldMove = oldMove, NewMove = newMove };
                    Pairs[oldMove] = pair;
                    Pairs[newMove] = pair;
                    __result = new OperationResult<MoveResult>(oldMove);
                    _swapped = true;
                    Plugin.Log?.LogInfo("[KMR][B] swapped (old magazine into new magazine cell, new into gun)");
                    return false;
                }

                if (ReferenceEquals(item, plan.NewMag))
                {
                    var slotAddr = to as SlotItemAddress;
                    if (slotAddr != null && ReferenceEquals(slotAddr.Slot, plan.GunSlot))
                    {
                        foreach (var kv in Pairs)
                        {
                            if (ReferenceEquals(kv.Value.OldMove.Item, plan.OldMag) && !kv.Value.RolledBack)
                            {
                                __result = new OperationResult<MoveResult>(kv.Value.NewMove);
                                Plugin.V("B bridge: new magazine insertion already done by swap");
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] bridge error: " + ex.Message); }
            return true;
        }

        /// <summary>旧弹匣更大且当前格放不下：找一件占着目标空地的物品，把它挪到新弹匣腾出的格，再放大弹匣。</summary>
        private static bool FindDisplacePlan(GridItemAddress dest, Magazine nextMag, Magazine oldMag, ItemController ic)
        {
            try
            {
                var equipment = (ic as InventoryController)?.Inventory?.Equipment;
                if (equipment == null) { Plugin.Log?.LogInfo("[KMR][B] displace: no equipment container"); return false; }
                int scanned = 0, spots = 0, fitsDest = 0;
                foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                {
                    if (g == null || ReferenceEquals(g, dest.Grid)) continue;
                    scanned++;
                    foreach (var x in new List<Item>(g.Items))
                    {
                        var spot = FindSpot(g, x, oldMag);
                        if (spot == null) continue;
                        spots++;
                        var xr = x.CalculateRotatedSize(dest.LocationInGrid.r);
                        var nr = nextMag.CalculateRotatedSize(dest.LocationInGrid.r);
                        if (xr.X > nr.X || xr.Y > nr.Y)
                        {
                            Plugin.Log?.LogInfo($"[KMR][B] displace: {x.ShortName}({xr.X}x{xr.Y}) larger than target cell ({nr.X}x{nr.Y})");
                            continue;
                        }
                        fitsDest++;
                        _displace = x; _displaceGrid = g; _drumSpot = spot; _displaceFrom = x.CurrentAddress;
                        Plugin.Log?.LogInfo($"[KMR][B] plan: move {x.ShortName} into the vacated cell, then place {oldMag.ShortName} into {g.ID}");
                        return true;
                    }
                }
                Plugin.Log?.LogInfo($"[KMR][B] displace: scanned {scanned} grids, can free space in {spots}, can fit displaced item into vacated cell in {fitsDest}");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] displace lookup error: " + ex.Message); }
            return false;
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
                    int iw = it.Width, ih = it.Height;
                    if (a.LocationInGrid.r == ItemRotation.Vertical) { var t = iw; iw = ih; ih = t; }
                    for (int x = a.LocationInGrid.x; x < a.LocationInGrid.x + iw && x < w; x++)
                        for (int y = a.LocationInGrid.y; y < a.LocationInGrid.y + ih && y < h; y++)
                            if (x >= 0 && y >= 0) occ[x, y] = true;
                }
                int tw = target.Width, th = target.Height;
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

        private static bool RegionFits(Grid grid, Item exclude, Item target)
        {
            try
            {
                int w = grid.GridWidth, h = grid.GridHeight;
                if (w <= 0 || h <= 0) return false;
                bool[,] occ = new bool[w, h];
                foreach (var it in grid.Items)
                {
                    if (ReferenceEquals(it, exclude)) continue;
                    var a = it.CurrentAddress as GridItemAddress;
                    if (a == null) continue;
                    int iw = it.Width, ih = it.Height;
                    if (a.LocationInGrid.r == ItemRotation.Vertical) { var t = iw; iw = ih; ih = t; }
                    for (int x = a.LocationInGrid.x; x < a.LocationInGrid.x + iw && x < w; x++)
                        for (int y = a.LocationInGrid.y; y < a.LocationInGrid.y + ih && y < h; y++)
                            if (x >= 0 && y >= 0) occ[x, y] = true;
                }
                int tw = target.Width, th = target.Height;
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
                            if (ok) return true;
                        }
                }
            }
            catch { }
            return false;
        }

        /// <summary>兜底：把旧弹匣放回原版落点或弹挂/口袋空位，并把它当作 8827 的结果交回。</summary>
        private static bool TryFallback(Plan plan, ItemController ic, ref OperationResult<MoveResult> __result)
        {
            try
            {
                ItemAddress target = plan.VanillaTarget;
                if (target == null)
                {
                    var equipment = (ic as InventoryController)?.Inventory?.Equipment;
                    if (equipment != null)
                        foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject())
                        {
                            var loc = g.FindFreeSpace(plan.OldMag);
                            if (loc == null) continue;
                            target = g.CreateItemAddress(loc);
                            break;
                        }
                }
                if (target == null) return false;
                var mv = ItemManipulator.Move(plan.OldMag, target, ic, simulate: false);
                if (mv.Failed) return false;
                __result = mv;
                _plan = null;
                Plugin.Log?.LogInfo("[KMR][B] fallback to vanilla target: " + target.ContainerName);
                return true;
            }
            catch { return false; }
        }

        private static bool ReorderOldMag(Plan plan, ItemController ic, ref OperationResult<MoveResult> __result)
        {
            OperationResult<RemoveResult> r1 = default(OperationResult<RemoveResult>);
            bool ok1 = false, ok2 = false, ok3 = false;
            MoveResult m2 = null, m3 = null;
            try
            {
                r1 = ItemManipulator.Remove(plan.OldMag, ic);          // 摘下旧弹匣（无归属）
                if (r1.Failed) { Plugin.Log?.LogWarning("[KMR][B] reorder failed (remove old): " + r1.Error); return true; }
                ok1 = true;

                var r2 = ItemManipulator.Move(plan.NewMag, plan.GunAddr, ic, simulate: false);
                if (r2.Failed) { Plugin.Log?.LogWarning("[KMR][B] reorder failed (insert new into gun): " + r2.Error); SafeRollback(ref r1, ref ok1); return TryFallback(plan, ic, ref __result) ? false : true; }
                ok2 = true; m2 = r2.Value;

                if (plan.Displace != null)
                {
                    var r3 = ItemManipulator.Move(plan.Displace, plan.Dest, ic, simulate: false);
                    if (r3.Failed)
                    {
                        Plugin.Log?.LogWarning("[KMR][B] reorder failed (move displaced item): " + r3.Error);
                        try { m2.RollBack(); } catch { }
                        SafeRollback(ref r1, ref ok1);
                        return TryFallback(plan, ic, ref __result) ? false : true;
                    }
                    ok3 = true; m3 = r3.Value;
                }

                ItemAddress spotAddr;
                if (plan.DrumSpot != null)
                {
                    var g = plan.DisplaceGrid != null ? plan.DisplaceGrid : ((GridItemAddress)plan.Dest).Grid;
                    spotAddr = g.CreateItemAddress(plan.DrumSpot);
                }
                else spotAddr = plan.Dest;

                // 旧弹匣此刻无归属，只能用 Add（Move 会因 Item.Parent 抛异常）
                var r4 = ItemManipulator.Add(plan.OldMag, spotAddr, ic, simulate: false);
                if (r4.Failed)
                {
                    Plugin.Log?.LogWarning("[KMR][B] reorder failed (place old): " + r4.Error);
                    if (m3 != null) { try { m3.RollBack(); } catch { } }
                    try { m2.RollBack(); } catch { }
                    SafeRollback(ref r1, ref ok1);
                    return TryFallback(plan, ic, ref __result) ? false : true;
                }

                // 替位物（X）的移动结果没有交给 Run，必须由我们补发事件，否则界面不刷新
                if (m3 != null)
                {
                    try
                    {
                        m3.RaiseEvents(ic, CommandStatus.Begin);
                        m3.RaiseEvents(ic, CommandStatus.Succeed);
                    }
                    catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] raise displaced events failed: " + ex.Message); }
                }

                var oldMove = new MoveResult(plan.OldMag, plan.GunAddr, spotAddr, ic,
                    new IgnoreDiscardLimitsResult(), r1.Value, r4.Value, null, null, null);
                var pair = new Pair { OldMove = oldMove, NewMove = m2 };
                pair.Rollbacks.Add(() =>
                {
                    try { if (plan.Displace != null && plan.DisplaceFrom != null) ItemManipulator.Move(plan.Displace, plan.DisplaceFrom, ic, simulate: false); } catch { }
                });
                pair.Rollbacks.Add(() =>
                {
                    try { ItemManipulator.Move(plan.NewMag, plan.Dest, ic, simulate: false); } catch { }
                    try { ItemManipulator.Move(plan.OldMag, plan.GunAddr, ic, simulate: false); } catch { }
                });
                Pairs[oldMove] = pair; Pairs[m2] = pair;
                __result = new OperationResult<MoveResult>(oldMove);
                _swapped = true;
                Plugin.Log?.LogInfo("[KMR][B] reorder done (new in gun, old in freed cell)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[KMR][B] reorder error: " + ex.Message);
                if (m3 != null) { try { m3.RollBack(); } catch { } }
                if (ok2 && m2 != null) { try { m2.RollBack(); } catch { } }
                SafeRollback(ref r1, ref ok1);
                return true;
            }
        }

        private static void SafeRollback(ref OperationResult<RemoveResult> r, ref bool done)
        {
            if (!done) return;
            try { r.Value.RollBack(); } catch { }
            done = false;
        }



        private static void NotifyNoSpace()
        {
            try
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification(
                    L.T("No room for the old magazine — dropped on the ground",
                        "旧弹匣没地方放，掉在地上了"),
                    EFT.Communications.ENotificationDurationType.Default,
                    EFT.Communications.ENotificationIconType.Alert);
            }
            catch { }
        }

        private static bool BeforeRollback(MoveResult __instance)
        {
            try
            {
                Pair pair;
                if (__instance != null && Pairs.TryGetValue(__instance, out pair))
                {
                    if (!pair.RolledBack)
                    {
                        pair.RolledBack = true;   // 先标记，避免嵌套递归
                        try
                        {
                            if (pair.Swap != null) pair.Swap.RollBack();
                            foreach (var rb in pair.Rollbacks) { try { rb(); } catch { } }
                        }
                        catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] rollback error: " + ex.Message); }
                        Plugin.Log?.LogWarning("[KMR][B] rolled back (reload failed)");
                    }
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static void AfterRun(Weapon weapon, Magazine nextMagazine)
        {
            var plan = _plan;
            try
            {
                if (weapon == null || nextMagazine == null) return;
                var cur = weapon.GetCurrentMagazine();
                if (plan == null || !_swapped)
                {
                    Plugin.V("no swap this time (vanilla or fallback path)");
                    if (Plugin.NoSpaceForOldMag)
                    {
                        Diag.Reload(Diag.NextSeq(), null, null, nextMagazine, "solo-drop", "no-free-cell");
                        NotifyNoSpace();
                    }
                    return;
                }
                if (ReferenceEquals(cur, nextMagazine))
                {
                    Plugin.Log?.LogInfo("[KMR][B] done: gun has " + nextMagazine.ShortName);
                    if (Plugin.Verbose != null && Plugin.Verbose.Value) ProbeContainers(plan);
                }
                else Plugin.Log?.LogWarning("[KMR][B] unexpected result: gun has " + (cur == null ? "none" : cur.ShortName));
            }
            catch { }
            finally { _plan = null; }
        }

        private static Exception OnRunFinal(Exception __exception)
        {
            if (__exception != null)
            {
                try { RollBackAll(); } catch { }
                Plugin.Log?.LogWarning("[KMR][B] Run threw, rolled back: " + __exception.Message);
            }
            _plan = null;
            return __exception;
        }

        private static void RollBackAll()
        {
            foreach (var kv in Pairs)
            {
                var p = kv.Value;
                if (p.RolledBack) continue;
                p.RolledBack = true;          // 先标记，避免递归
                try
                {
                    if (p.Swap != null) p.Swap.RollBack();
                    foreach (var rb in p.Rollbacks) { try { rb(); } catch { } }
                }
                catch { }
            }
        }

        internal static void Clear()
        {
            _plan = null;
            _swapped = false;
            Pairs.Clear();
            _displace = null; _displaceGrid = null; _drumSpot = null; _displaceFrom = null;
        }

        /// <summary>诊断：换弹后 0s / 2s 打印涉及的容器是否"已搜索"，看是谁把它翻成未搜索。</summary>
        private static void ProbeContainers(Plan plan)
        {
            try
            {
                var si = plan.Dest?.Container?.ParentItem as SearchableItem;
                if (si == null) return;
                LogSearchState("now", si);
                if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(ProbeLater(si, 2f));
            }
            catch (Exception ex) { Plugin.V("probe error: " + ex.Message); }
        }

        private static System.Collections.IEnumerator ProbeLater(SearchableItem si, float delay)
        {
            yield return new UnityEngine.WaitForSeconds(delay);
            try { LogSearchState("after 2s", si); } catch { }
        }

        private static void LogSearchState(string when, SearchableItem si)
        {
            try
            {
                var f = typeof(SearchableItem).GetField("_itemInfo",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var info = f?.GetValue(si);
                Plugin.Log?.LogInfo($"[KMR][Search] {when} container={si.ShortName} IsSearched={si.IsSearched} _itemInfo={(info != null)}");
            }
            catch (Exception ex) { Plugin.V("search-state read failed: " + ex.Message); }
        }

        private static bool SameCell(ItemAddress a, ItemAddress b)
        {
            try
            {
                if (a == null || b == null) return false;
                if (!ReferenceEquals(a.Container, b.Container)) return false;
                var ga = a as GridItemAddress;
                var gb = b as GridItemAddress;
                if (ga != null && gb != null)
                    return ga.LocationInGrid.x == gb.LocationInGrid.x && ga.LocationInGrid.y == gb.LocationInGrid.y;
                return true;
            }
            catch { return false; }
        }
    }
}
