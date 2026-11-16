using System;
using System.Threading.Tasks;
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
            /// <summary>挡路物品要挪去的位置（null = 挪进新弹匣腾出的格子）。</summary>
            internal ItemAddress DisplaceTarget;
        }

        private sealed class Pair
        {
            internal SwapResult Swap;
            internal MoveResult OldMove;
            internal MoveResult NewMove;
            internal bool RolledBack;
            internal bool Tx;                       // 这一对是通过网络事务落地的
            internal Task TxTask;
            internal readonly List<System.Action> Rollbacks = new List<System.Action>();
        }

        private static readonly Dictionary<MoveResult, Pair> Pairs = new Dictionary<MoveResult, Pair>();
        private static Plan _plan;
        private static bool _swapped;
        private static bool _isObservedCopy;       // 这次 Run 是“别人的弹匣”在重放（联机观察端）
        private static string _skipReason;         // 观察端为什么没交换
        private static int _coopSwapFail;
        private static string _mode = "none";
        /// <summary>这次命中的档位（日志用；联机排查两端是否选了不同档）。</summary>
        private static string _tier;
        /// <summary>已发出、还没落地的 Swap 事务（等 ack 用）。</summary>
        private static Task _txTask;
        private static Item _displace;
        private static Grid _displaceGrid;
        private static LocationInGrid _drumSpot;
        private static ItemAddress _displaceFrom;
        private static ItemAddress _displaceTarget;
        /// <summary>本轮换弹前的旧弹匣（用于事后确认它有没有变成“孤儿”）。</summary>
        private static Magazine _oldMagBeforeReload;

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
                _tier = null;
                _txTask = null;
                // Plugin.NoSpaceForOldMag 由 ReloadFix 置位；这里不再无条件清零（否则掉地提示会丢）
                Pairs.Clear();
                _displace = null; _displaceGrid = null; _drumSpot = null; _displaceFrom = null; _displaceTarget = null;
                var main0 = Comfort.Common.Singleton<GameWorld>.Instance == null ? null : Comfort.Common.Singleton<GameWorld>.Instance.MainPlayer;
                Plugin.V($"[B] entry: qr={quickReload} weapon={(weapon != null)} next={(nextMagazine == null ? "null" : nextMagazine.ShortName)} " +
                    $"ic={(itemController != null)} isMain={(main0 != null && ReferenceEquals(itemController, main0.InventoryController))} " +
                    $"old={(weapon?.GetCurrentMagazine()?.ShortName ?? "null")} vta={(vestTargetAddress != null)}");
                if (Plugin.Enabled == null || !Plugin.Enabled.Value) return;
                if (Plugin.DirectSwapEnabled == null || !Plugin.DirectSwapEnabled.Value) return;
                // ★ 联机 + 保留开关：交给 Retention（Run 前后 Add，绕开 hands 限制）
                if (Plugin.RetentionOwns())
                {
                    _skipReason = "coop retention";
                    Plugin.V("[B] defer: co-op retention takes over");
                    return;
                }
                // ★ 先判 UIFixes（它接管时谁都不许让路，否则两边都撒手 ⇒ 本地交换、不同步）
                if (Plugin.UiFixesDelegated) { Plugin.V("B defer: UIFixes in-place reload is ON"); return; }
                // ★ 联机 + 开启 CoopZeroGap + 事务路径真的装上了：交给 FikaCoopPlacement（每步走网络事务）
                if (!Plugin.SwapTxActive() && Plugin.CoopPathActive() && FikaCoopPlacement.Installed)
                {
                    _skipReason = "coop transaction path";
                    Plugin.Log?.LogInfo("[KMR][B] defer: co-op transaction path (CoopZeroGap) takes over");
                    return;
                }
                if (quickReload) { _skipReason = "quick reload"; Plugin.Log?.LogInfo("[KMR][B] B skip: quick reload"); return; }
                if (weapon == null || nextMagazine == null || itemController == null)
                { Plugin.Log?.LogInfo("[KMR][B] skip: null argument"); return; }
                var main = Comfort.Common.Singleton<GameWorld>.Instance == null ? null : Comfort.Common.Singleton<GameWorld>.Instance.MainPlayer;
                bool isLocalPlayer = main != null && ReferenceEquals(itemController, main.InventoryController);
                _isObservedCopy = false;   // 只有“别的玩家”的副本才记 coop 日志；bot 不刷屏
                _skipReason = null;
                bool isReplayedCoop = !isLocalPlayer
                    && Plugin.CoopLocalSwap != null && Plugin.CoopLocalSwap.Value
                    && Plugin.FikaInstalled && Plugin.InFikaSession()
                    && IsPlayerOwnedController(itemController);
                if (!isLocalPlayer && !isReplayedCoop) { Plugin.V("B skip: not local player"); return; }
                if (isReplayedCoop) _isObservedCopy = true;

                var oldMag = weapon.GetCurrentMagazine();
                _oldMagBeforeReload = oldMag;
                if (oldMag == null || ReferenceEquals(oldMag, nextMagazine))
                { Plugin.Log?.LogInfo("[KMR][B] skip: no old magazine or same magazine"); return; }

                var dest = nextMagazine.CurrentAddress as GridItemAddress;
                if (dest == null) { _skipReason = "new magazine not in a grid"; Plugin.Log?.LogInfo("[KMR][B] B skip: new magazine not in a grid"); return; }
                if (!ReferenceEquals(dest.Grid.GetItemAt(dest.LocationInGrid), nextMagazine)) { _skipReason = "grid cell mismatch"; Plugin.Log?.LogInfo("[KMR][B] B skip: grid cell mismatch"); return; }
                if (!ReferenceEquals(dest.GetOwner(), itemController)) { _skipReason = "different owner"; Plugin.Log?.LogInfo("[KMR][B] B skip: different owner"); return; }

                var gunSlot = weapon.GetMagazineSlot();
                if (gunSlot == null) { _skipReason = "no magazine slot"; Plugin.Log?.LogInfo("[KMR][B] B skip: no magazine slot"); return; }
                var gunAddr = gunSlot.CreateItemAddress() as SlotItemAddress;
                if (gunAddr == null) { _skipReason = "gun slot address type mismatch"; Plugin.Log?.LogInfo("[KMR][B] B skip: gun slot address type mismatch"); return; }
                if (nextMagazine.IsMagazineWithBelt) { _skipReason = "belt-fed magazine"; Plugin.Log?.LogInfo("[KMR][B] B skip: belt-fed magazine"); return; }

                var rot = dest.LocationInGrid.r;
                IntVec2 a, b;
                try { a = oldMag.CalculateRotatedSize(rot); b = nextMagazine.CalculateRotatedSize(rot); }
                catch (Exception ex) { Plugin.Log?.LogInfo("[KMR][B] skip: size calc failed " + ex.Message); return; }
                bool fits = (a.X <= b.X && a.Y <= b.Y);
                try { if (!dest.Grid.CheckCompatibility(oldMag)) { _skipReason = "incompatible grid"; Plugin.Log?.LogInfo("[KMR][B] B skip: incompatible grid"); return; } }
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
                    // ★ 2026-11-16【落点优先级】弹挂 > 背包 > 掉地；弹挂内优先“尺寸刚好匹配”的空格
                    var directSpot = FindSpot(dest.Grid, nextMagazine, oldMag);
                    if (directSpot != null) { _drumSpot = directSpot; _displaceGrid = dest.Grid; }
                    if (directSpot == null)
                    {
                        // 同弹挂（不含背包）里，找尺寸浪费最小的空格子放旧弹匣
                        var fitted = FindBestFreeSlot(itemController, oldMag, backpack: false, exclude: dest.Grid);
                        if (fitted != null)
                        {
                            _drumSpot = fitted.Value.Spot; _displaceGrid = fitted.Value.Grid;
                            Plugin.Log?.LogInfo($"[KMR][B] displace: put {oldMag.TemplateId} into free slot {fitted.Value.Grid.ID} (smallest rig slot that fits, area {fitted.Value.Waste})");
                        }
                    }
                    if (_displaceGrid == null && !FindDisplacePlan(dest, nextMagazine, oldMag, itemController))
                    {
                        // 弹挂确实腾不出位置 ⇒ 明确尝试背包（别把背包空着却掉地上）
                        var bp = FindBackpackSpot(oldMag, itemController);
                        if (bp != null)
                        {
                            _drumSpot = bp.Value.Spot; _displaceGrid = bp.Value.Grid;
                            Plugin.Log?.LogInfo($"[KMR][B] displace: rig has no room ⇒ put {oldMag.TemplateId} into backpack {bp.Value.Grid.ID} (keep it off the floor)");
                        }
                        else
                        {
                            Plugin.Log?.LogInfo($"[KMR][B] skip: old magazine {a.X}x{a.Y} does not fit the area vacated by the new magazine (grid {dest.Grid.ID})");
                            return;   // 交给原版：原版会掉地，AfterRun 里会弹提示
                        }
                    }
                    Plugin.Log?.LogInfo($"[KMR][B] plan reorder: insert {nextMagazine.ShortName} into gun, then place {oldMag.ShortName} into {_displaceGrid?.ID}");
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
                    DisplaceTarget = _displaceTarget,
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

                    bool tx = Plugin.SwapTxActive() && itemController is InventoryController && !_isObservedCopy;
                    var sw = ItemManipulator.Swap(plan.OldMag, plan.Dest, plan.NewMag, plan.GunAddr, itemController, simulate: tx);
                    if (sw.Failed)
                    {
                        Plugin.Log?.LogWarning("[KMR][B] swap failed: " + sw.Error);
                        if (TryFallback(plan, itemController, ref __result)) return false;
                        _plan = null;
                        return true;
                    }
                    var v = sw.Value;

                    if (tx)
                    {
                        var icx = (InventoryController)itemController;
                        // ★ 诊断：SwapResult.CanExecute 的三条检查逐条打出来（发送前）
                        try
                        {
                            bool same = ReferenceEquals(plan.OldMag, plan.NewMag);
                            var a1 = plan.OldMag.CheckAction(plan.Dest);
                            var a2 = plan.NewMag.CheckAction(plan.GunAddr);
                            Plugin.Log?.LogInfo($"[KMR][B][tx] canExecute diag: sameItem={same}"
                                + $" oldCheck={(a1.Failed ? a1.Error?.ToString() : "ok")}"
                                + $" newCheck={(a2.Failed ? a2.Error?.ToString() : "ok")}"
                                + $" destHolder={plan.Dest.ContainerName}"
                                + $" gunHolder={plan.GunAddr.ContainerName}");
                        }
                        catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B][tx] diag threw: " + ex.Message); }

                        Task tt = null;
                        try { tt = icx.TryRunNetworkTransaction(sw); }
                        catch (Exception ex) { Plugin.Log?.LogError("[KMR][B][tx] send threw: " + ex.Message); }

                        if (tt == null)
                        {
                            // 发包失败 ⇒ 退回本地 swap，别让枪上没弹匣
                            Plugin.Log?.LogError("[KMR][B][tx] no task, falling back to local swap");
                            var loc = ItemManipulator.Swap(plan.OldMag, plan.Dest, plan.NewMag, plan.GunAddr, itemController, simulate: false);
                            if (loc.Failed) { _plan = null; return true; }
                            v = loc.Value;
                        }
                        else
                        {
                            var tOld = new MoveResult(v.Item, v._from, v.To, itemController,
                                v._destroyResult1, v._removeResult1, v._addResult1, null, null, null);
                            var tNew = new MoveResult(v.Item2, v._from2, v.To2, itemController,
                                v._destroyResult2, v._removeResult2, v._addResult2, null, null, null);
                            var tpair = new Pair { Swap = v, OldMove = tOld, NewMove = tNew, Tx = true, TxTask = tt };
                            Pairs[tOld] = tpair;
                            Pairs[tNew] = tpair;
                            _txTask = tt; _txOldMag = plan.OldMag; _txDest = plan.Dest; _txGun = plan.GunAddr; _txIc = icx;
                            _swapped = true;
                            __result = new OperationResult<MoveResult>(tOld);
                            Plugin.Log?.LogInfo($"[KMR][B][tx] swap sent as one packet: {plan.OldMag.ShortName}->dest, {plan.NewMag.ShortName}->gun");
                            return false;
                        }
                    }

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

        /// <summary>
        /// 旧弹匣更大、当前格放不下：腾地方给它。三档优先级：
        ///   ① 弹挂内部重排：把弹挂里某件挪进“新弹匣腾出的格子” ⇒ 弹匣仍在弹挂（最好）
        ///   ② 把挡路的弹挂物品挪去别的容器（优先背包），腾出的位置放弹匣 ⇒ 弹匣仍在弹挂
        ///   ③ 老策略：扫其它容器，把那边的一件挪进腾出的格子，旧弹匣放到它原来的位置
        /// 同一格子内多个候选时挑体积最小的，减少乱位。
        /// </summary>
        private static bool FindDisplacePlan(GridItemAddress dest, Magazine nextMag, Magazine oldMag, ItemController ic)
        {
            try
            {
                var equipment = (ic as InventoryController)?.Inventory?.Equipment;
                if (equipment == null) { Plugin.Log?.LogInfo("[KMR][B] displace: no equipment container"); return false; }

                var others = new List<Grid>();
                foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                {
                    if (g == null || ReferenceEquals(g, dest.Grid)) continue;
                    others.Add(g);
                }

                var nr = nextMag.CalculateRotatedSize(dest.LocationInGrid.r);
                int scanned = 0, spots = 0, fitsDest = 0;

                // ── ① 弹挂内部：把某件挪进新弹匣腾出的格子（同容器，天然自洽）
                if (dest.Grid != null && TryMoveIntoVacatedCell(dest.Grid, dest, nr, nextMag, oldMag, ic, ref scanned, ref spots, ref fitsDest)) return true;

                // ── ①.3 其它格子里的挡路件，挪进“新弹匣腾出的那一格”（仅单机：
                //    1.1.0 的联机包络只有 ① 与 ③，多档会两端各挑不同候选）
                if (dest.Grid != null && !Plugin.InFikaSession() && TryOtherSlotIntoVacated(dest, nr, nextMag, oldMag, ic)) { _tier = "①.3"; return true; }

                // ── ①.5 弹挂内部跨格子腾挪（仅单机，同上理由）
                if (dest.Grid != null && !Plugin.InFikaSession() && TryRelocateBlocker(dest, nextMag, oldMag, ic)) { _tier = "①.5"; return true; }

                // ── ② 弹挂里挡路的那件，挪去别的容器（仅单机，同上理由），弹匣放回弹挂
                //    ★ 观察端（Fika 回放）不做跨容器移动，避免两端快照分叉
                // ★ 联机时两端都不做②：换弹包不带物品搬运列表，只有两端算得一样才不会分叉
                if (dest.Grid != null && !Plugin.InFikaSession() && TryEvictFromSameGrid(dest, others, nextMag, oldMag, ic)) { _tier = "②"; return true; }

                // ── ③ 老策略：其它容器里找一件挪进腾出的格子（观察端也用，和原来行为一致）
                foreach (var g in others)
                {
                    try { if (!g.CheckCompatibility(oldMag)) continue; } catch { continue; }   // 该格过滤器不收弹匣就别选
                    if (TryMoveIntoVacatedCell(g, dest, nr, nextMag, oldMag, ic, ref scanned, ref spots, ref fitsDest)) return true;
                }

                Plugin.Log?.LogInfo($"[KMR][B] displace: scanned {scanned} grids, can free space in {spots}, can fit displaced item into vacated cell in {fitsDest}");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] displace lookup error: " + ex.Message); }
            return false;
        }

        /// <summary>候选物品是否可安全挪动（锁住/任务物/不可拾取就不动）。</summary>
        private static bool CanRelocate(Item x, Magazine nextMag, Magazine oldMag)
        {
            try
            {
                if (x == null || ReferenceEquals(x, oldMag) || ReferenceEquals(x, nextMag)) return false;
                if (x.PinLockState == EItemPinLockState.Locked) return false;
                if (x.QuestItem) return false;
                if (!x.CanBePickedUp) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>在 grid 里挑一件能挪进“新弹匣腾出的格子”的物品（挑最小件，同面积按 Id 定序）。</summary>
        private static bool TryMoveIntoVacatedCell(Grid g, GridItemAddress dest, IntVec2 nr,
                                                   Magazine nextMag, Magazine oldMag, ItemController ic,
                                                   ref int scanned, ref int spots, ref int fitsDest)
        {
            scanned++;
            Item best = null;
            LocationInGrid bestSpot = default(LocationInGrid);
            int bestArea = int.MaxValue;
            string bestId = null;
            bool sameGrid = ReferenceEquals(g, dest.Grid);

            foreach (var x in new List<Item>(g.Items))
            {
                if (!CanRelocate(x, nextMag, oldMag)) continue;
                if (sameGrid && ReferenceEquals(x, g.GetItemAt(dest.LocationInGrid))) continue;

                // 同容器：按“x 已经挪进腾出格”的真实占用来算 oldMag 的落点；
                // 别的容器：x 移出该格即可，按原占用算。
                var spot = sameGrid
                    ? FindSpot(g, x, oldMag, nextMag, x, dest.LocationInGrid)
                    : FindSpot(g, x, oldMag, nextMag, null, default(LocationInGrid));
                if (spot == null) continue;
                spots++;

                var xr = x.CalculateRotatedSize(dest.LocationInGrid.r);
                if (xr.X > nr.X || xr.Y > nr.Y) continue;
                // ★ 先查目标网格是否接受它（跨格子时尤其重要），再模拟（先把新弹匣让出来）
                if (!sameGrid)
                {
                    try { if (!dest.Grid.CheckCompatibility(x)) continue; } catch { continue; }
                }
                if (!CanMoveIntoDest(x, dest, nextMag, ic, out var mvWhy))
                {
                    Plugin.Log?.LogInfo($"[KMR][B] vacated reject(move): x={x.TemplateId} grid={g.ID} err={mvWhy}");
                    continue;
                }
                fitsDest++;

                int area = xr.X * xr.Y;
                if (area < bestArea || (area == bestArea && string.CompareOrdinal(x.Id, bestId) < 0))
                {
                    best = x; bestSpot = spot; bestArea = area; bestId = x.Id;
                }
            }
            if (best == null) return false;

            _displace = best; _displaceGrid = g; _drumSpot = bestSpot; _displaceFrom = best.CurrentAddress;
            _displaceTarget = dest;
            Plugin.Log?.LogInfo($"[KMR][B] displace: move {best.TemplateId}({bestArea}) into the vacated cell"
                                + $"{(sameGrid ? " [same grid]" : " [other container]")}, then place {oldMag.TemplateId} into {g.ID}");
            return true;
        }

        /// <summary>把弹挂里挡路的那件挪去别的容器（优先背包），腾出的空位给旧弹匣。</summary>
        private static bool TryEvictFromSameGrid(GridItemAddress dest, List<Grid> others,
                                                 Magazine nextMag, Magazine oldMag, ItemController ic)
        {
            foreach (var x in new List<Item>(dest.Grid.Items))
            {
                if (!CanRelocate(x, nextMag, oldMag)) continue;
                var spot = FindSpot(dest.Grid, x, oldMag, nextMag, null, default(LocationInGrid));
                if (spot == null) continue;
                foreach (var og in others)
                {
                    LocationInGrid loc = null;
                    try { loc = og.FindFreeSpace(x); } catch { loc = null; }
                    if (loc == null) continue;
                    try
                    {
                        var ogAddr = og.CreateItemAddress(loc);
                        if (ItemManipulator.Move(x, ogAddr, ic, simulate: true).Failed) continue;
                    }
                    catch { continue; }
                    _displace = x; _displaceGrid = dest.Grid; _drumSpot = spot; _displaceFrom = x.CurrentAddress;
                    try { _displaceTarget = og.CreateItemAddress(loc); } catch { continue; }
                    Plugin.Log?.LogInfo($"[KMR][B] displace: evict {x.TemplateId} from the rig to {og.ID}, keep the magazine in the rig");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 其它弹挂格子里的物品，挪进「新弹匣腾出的那一格」；旧弹匣随后放进它空出来的位置。
        /// 这是最理想的一档：腾出来的格子本来就要空着，不占额外空间。
        /// </summary>
        private static bool TryOtherSlotIntoVacated(GridItemAddress dest, IntVec2 nr,
                                                    Magazine nextMag, Magazine oldMag, ItemController ic)
        {
            try
            {
                var equipment = (ic as InventoryController)?.Inventory?.Equipment;
                if (equipment == null) return false;
                var rig = RigGrids(equipment);

                foreach (var g in rig)
                {
                    if (g == null || ReferenceEquals(g, dest.Grid)) continue;
                    var cands = new List<Item>();
                    foreach (var it in g.Items)
                        if (CanRelocate(it, nextMag, oldMag)) cands.Add(it);
                    cands.Sort((a, b) =>
                    {
                        var ca = CellSize(a); var cb = CellSize(b);
                        return (ca.X * ca.Y).CompareTo(cb.X * cb.Y);
                    });

                    foreach (var x in cands)
                    {
                        var xr = x.CalculateRotatedSize(dest.LocationInGrid.r);
                        if (xr.X > nr.X || xr.Y > nr.Y) continue;          // 腾出的格子放不下它
                        if (!CanMoveIntoDest(x, dest, nextMag, ic, out var mvWhy))
                        {
                            Plugin.Log?.LogInfo($"[KMR][B] vacated reject(move): x={x.TemplateId} grid={g.ID} err={mvWhy}");
                            continue;
                        }

                        var spot = FindSpot(g, x, oldMag, nextMag, null, default(LocationInGrid));
                        if (spot == null) continue;

                        bool addOk = false;
                        string addErr = null;
                        var a = x.CurrentAddress as GridItemAddress;
                        try
                        {
                            if (a != null)
                            {
                                g.RemoveItem(x, a.LocationInGrid);
                                try
                                {
                                    var ar = ItemManipulator.Add(oldMag, g.CreateItemAddress(spot), ic, simulate: true);
                                    addOk = !ar.Failed;
                                    if (ar.Failed) addErr = ar.Error.GetType().Name + ": " + ar.Error;
                                }
                                finally { if (x.CurrentAddress == null) g.PlaceItem(x, a.LocationInGrid); }
                            }
                        }
                        catch (Exception ex) { addOk = false; addErr = "throw " + ex.GetType().Name + ": " + ex.Message; }
                        if (!addOk) { Plugin.Log?.LogInfo($"[KMR][B] vacated-slot reject: x={x.TemplateId} grid={g.ID} err={addErr}"); continue; }

                        _displace = x; _displaceGrid = g; _drumSpot = spot;
                        _displaceFrom = x.CurrentAddress; _displaceTarget = dest;
                        Plugin.Log?.LogInfo($"[KMR][B] displace: move {x.TemplateId} {g.ID} → vacated cell, then place {oldMag.TemplateId} into {g.ID}");
                        return true;
                    }
                }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] other-slot error: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// 把挡路件挪走给旧弹匣腾位置，目标容器按优先级：
        ///   ① 弹挂里的其它空格  ② 口袋  ③ 背包
        /// 旧弹匣随后放进挡路件腾出来的位置（仍在弹挂里）。
        /// </summary>
        private static bool TryRelocateBlocker(GridItemAddress dest, Magazine nextMag, Magazine oldMag, ItemController ic)
        {
            try
            {
                var equipment = (ic as InventoryController)?.Inventory?.Equipment;
                if (equipment == null) return false;
                var rig = RigGrids(equipment);

                int grids = 0, drumSpots = 0, okTargets = 0;
                foreach (var g in rig)
                {
                    if (g == null || ReferenceEquals(g, dest.Grid)) continue;   // 弹匣自己那格交给 ①
                    grids++;
                    var cands = new List<Item>();
                    foreach (var it in g.Items)
                        if (CanRelocate(it, nextMag, oldMag)) cands.Add(it);
                    cands.Sort((a, b) =>
                    {
                        var ca = CellSize(a); var cb = CellSize(b);
                        return (ca.X * ca.Y).CompareTo(cb.X * cb.Y);          // 先挑小的，少扰动
                    });

                    foreach (var x in cands)
                    {
                        var spot = FindSpot(g, x, oldMag, nextMag, null, default(LocationInGrid));
                        if (spot == null) continue;
                        drumSpots++;

                        // ★ 必须先把 x 从格子里实际摘掉，否则它的占位永远挡着 oldMag，
                        //   这次校验必失败（等于把整条路径废掉）。校验完立刻放回。
                        bool addOk = false;
                        var a = x.CurrentAddress as GridItemAddress;
                        string addErr = null;
                        try
                        {
                            if (a != null)
                            {
                                g.RemoveItem(x, a.LocationInGrid);
                                try
                                {
                                    var ar = ItemManipulator.Add(oldMag, g.CreateItemAddress(spot), ic, simulate: true);
                                    addOk = !ar.Failed;
                                    if (ar.Failed) addErr = ar.Error.GetType().Name + ": " + ar.Error;
                                }
                                finally { if (x.CurrentAddress == null) g.PlaceItem(x, a.LocationInGrid); }
                            }
                        }
                        catch (Exception ex) { addOk = false; addErr = "throw " + ex.GetType().Name + ": " + ex.Message; }
                        if (!addOk) Plugin.Log?.LogInfo($"[KMR][B] relocate reject: x={x.TemplateId} grid={g.ID} ({g.GridWidth}x{g.GridHeight}) spot={spot.x},{spot.y} err={addErr}");
                        if (!addOk) continue;

                        foreach (var (og, tier) in TargetOrder(equipment, rig, g))
                        {
                            LocationInGrid loc = null;
                            try { loc = og.FindFreeSpace(x); } catch { loc = null; }
                            if (loc == null) continue;

                            try
                            {
                                var ogAddr = og.CreateItemAddress(loc);
                                if (ItemManipulator.Move(x, ogAddr, ic, simulate: true).Failed) continue;
                                okTargets++;
                                _displace = x; _displaceGrid = g; _drumSpot = spot;
                                _displaceFrom = x.CurrentAddress; _displaceTarget = ogAddr;
                                Plugin.Log?.LogInfo($"[KMR][B] displace: move {x.TemplateId} {g.ID} → {og.ID} ({tier})"
                                                    + $", then place {oldMag.TemplateId} into {g.ID}");
                                return true;
                            }
                            catch { continue; }
                        }
                    }
                }
                Plugin.Log?.LogInfo($"[KMR][B] relocate: rig grids {grids}, drum spots {drumSpots}, x→target ok {okTargets}");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] relocate error: " + ex.Message); }
            return false;
        }

        /// <summary>挡路件的目标格子顺序：弹挂(其它格) → 口袋 → 背包。</summary>
        private static List<(Grid Grid, string Tier)> TargetOrder(InventoryEquipment equipment, List<Grid> rig, Grid same)
        {
            var list = new List<(Grid, string)>();
            var seen = new HashSet<Grid>();
            foreach (var g in rig)
            {
                if (g == null || ReferenceEquals(g, same) || !seen.Add(g)) continue;
                list.Add((g, "rig"));
            }
            try
            {
                var eq = equipment;
                // 口袋
                var pockets = eq.GetSlot(EquipmentSlot.Pockets)?.ContainedItem as CompoundItem;
                if (pockets != null)
                    foreach (var g in pockets.Grids)
                        if (g != null && !ReferenceEquals(g, same) && seen.Add(g)) list.Add((g, "pockets"));
            }
            catch { }
            try
            {
                // 背包（及其它可放处）
                foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                {
                    if (g == null || ReferenceEquals(g, same) || !seen.Add(g)) continue;
                    list.Add((g, "backpack"));
                }
            }
            catch { }
            return list;
        }

        /// <summary>这个控制器属于一个玩家（不是 bot）吗？观察到的玩家重放换弹时要用同一套交换。</summary>
        internal static bool IsPlayerOwnedController(ItemController ic)
        {
            try
            {
                // PlayerInventoryController.Player 是 public 字段（不是属性），必须直读
                var poic = ic as Player.PlayerInventoryController;
                var owner = poic?.Player as Player;
                if (owner == null) return false;

                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var t = owner.GetType();
                if (t.Name.IndexOf("Bot", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                // 观察到的 AI 用同一个 ObservedInventoryController，必须排掉（bot 的弹匣不能让客户端改）
                var aiField = t.GetField("IsObservedAI", F);
                if (aiField != null && aiField.GetValue(owner) is bool isAiField && isAiField) return false;
                var aiProp = t.GetProperty("IsAI", F);
                if (aiProp != null && aiProp.GetValue(owner) is bool isAiProp && isAiProp) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>弹挂（战术胸挂）自己的格子；拿不到时退回“弹挂+口袋”。</summary>
        private static List<Grid> RigGrids(InventoryEquipment equipment)
        {
            var list = new List<Grid>();
            try
            {
                var vest = equipment.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem as CompoundItem;
                if (vest != null)
                    foreach (var g in vest.Grids) if (g != null) list.Add(g);
            }
            catch { }
            if (list.Count == 0)
            {
                try
                {
                    foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: false))
                        if (g != null) list.Add(g);
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// 在所有格子里找一个能放 oldMag 的空位，按“浪费空间最少”排序（尺寸刚好匹配的优先）。
        /// backpack=false 只看弹挂/口袋，true 时连背包一起看。
        /// </summary>
        private static (Grid Grid, LocationInGrid Spot, int Waste)? FindBestFreeSlot(
            ItemController ic, Magazine mag, bool backpack, Grid exclude)
        {
            try
            {
                var equipment = (ic as InventoryController)?.Inventory?.Equipment;
                if (equipment == null) return null;
                var mcs = CellSize(mag);
                int mw = mcs.X, mh = mcs.Y;
                (Grid Grid, LocationInGrid Spot, int Waste)? best = null;
                foreach (var g in backpack
                             ? equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true)
                             : RigGrids(equipment))
                {
                    if (g == null) continue;
                    if (exclude != null && ReferenceEquals(g, exclude)) continue;
                    LocationInGrid loc = null;
                    try { loc = g.FindFreeSpace(mag); } catch { loc = null; }
                    if (loc == null) continue;
                    // 浪费 = 格子面积 - 物品面积（旋转两种取较小浪费，粗略即可）
                    int waste = Math.Max(0, g.GridWidth * g.GridHeight - mw * mh);
                    if (best == null || waste < best.Value.Waste) best = (g, loc, waste);
                }
                return best;
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] free slot search error: " + ex.Message); }
            return null;
        }

        /// <summary>在背包/口袋等其它容器里给 oldMag 找一个空位（跳过弹挂自己）。</summary>
        private static (LocationInGrid Spot, Grid Grid)? FindBackpackSpot(Magazine oldMag, ItemController ic)
        {
            try
            {
                var equipment = (ic as InventoryController)?.Inventory?.Equipment;
                if (equipment == null) return null;
                foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                {
                    if (g == null) continue;
                    LocationInGrid loc = null;
                    try { loc = g.FindFreeSpace(oldMag); } catch { loc = null; }
                    if (loc != null) return (loc, g);
                }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] backpack spot error: " + ex.Message); }
            return null;
        }

        /// <summary>
        /// 校验“把 x 挪进 dest（新弹匣腾出的那格）”。
        /// ★ 关键：新弹匣此刻还占着 dest，必须先临时摘出来，否则占用检查必然失败，
        ///   整条路径会变成死代码。校验完立刻放回。
        /// </summary>
        private static bool CanMoveIntoDest(Item x, GridItemAddress dest, Magazine nextMag, ItemController ic, out string why)
        {
            why = null;
            var na = nextMag?.CurrentAddress as GridItemAddress;
            var xa = x?.CurrentAddress as GridItemAddress;
            bool removed = false;
            try
            {
                // ★ 联机不做“临时摘出再放回”：Grid.RemoveItem/PlaceItem 不发库存事件，
                //   UI（尤其背包里未搜索的格子）会残留幽灵状态。联机沿用 1.1.0 的包络（① 关），
                //   这里直接返回 false 让 ① 在联机下自然跳过。
                if (na != null && dest.Grid != null && ReferenceEquals(na.Grid, dest.Grid) && Plugin.InFikaSession())
                {
                    why = "coop: skip temp-remove (UI event safety)";
                    return false;
                }
                if (na != null && dest.Grid != null && ReferenceEquals(na.Grid, dest.Grid))
                {
                    removed = true;                       // ★ M1：必须在 RemoveItem 之前置位（它中途可能抛）
                    dest.Grid.RemoveItem(nextMag, na.LocationInGrid);
                }
                var r = ItemManipulator.Move(x, dest, ic, simulate: true);
                if (r.Failed) { why = r.Error.GetType().Name + ": " + r.Error; return false; }
                return true;
            }
            catch (Exception ex) { why = "throw " + ex.GetType().Name + ": " + ex.Message; return false; }
            finally
            {
                // ★ M2①：Move 无 try/finally，异常时 x 可能留在 dest 上；先把它复位
                try
                {
                    if (x != null && xa != null)
                    {
                        var cur = x.CurrentAddress as GridItemAddress;
                        bool misplaced = cur == null || !ReferenceEquals(cur.Grid, xa.Grid) || cur.LocationInGrid != xa.LocationInGrid;
                        if (misplaced)
                        {
                            try { if (cur != null) cur.Grid.RemoveItem(x, cur.LocationInGrid); } catch { }
                            try { xa.Grid.PlaceItem(x, xa.LocationInGrid); } catch { }
                        }
                    }
                }
                catch { }

                // ★ M2②：放回 nextMag 前先看格子是否真空了
                if (removed && nextMag != null && nextMag.CurrentAddress == null)
                {
                    try
                    {
                        if (dest.Grid.HasFreeSpace(nextMag, na.LocationInGrid)) dest.Grid.PlaceItem(nextMag, na.LocationInGrid);
                        else Plugin.Log?.LogError("[KMR][B] canMove: dest not free when restoring new magazine");
                    }
                    catch (Exception ex) { Plugin.Log?.LogError("[KMR][B] canMove restore error: " + ex.Message); }
                }
            }
        }

        /// <summary>物品在网格里实际占的格数（CompoundItem 会重写 CalculateCellSize，不能直接用 Width/Height）。</summary>
        private static IntVec2 CellSize(Item it)
        {
            try { return it.CalculateCellSize(); }
            catch { return new IntVec2(it.Width, it.Height); }
        }

        private static LocationInGrid FindSpot(Grid grid, Item exclude, Item target)
            => FindSpot(grid, exclude, target, null, null, default(LocationInGrid));

        /// <summary>
        /// 找一块能放下 target 的空位。
        /// alsoExclude：额外当作不存在（例如即将被移走的 newMag）。
        /// placeItem/placeAt：额外把 placeItem 按 placeAt 的位置和旋转标记为占用（模拟“先把它放过去”）。
        /// </summary>
        private static LocationInGrid FindSpot(Grid grid, Item exclude, Item target,
                                               Item alsoExclude, Item placeItem, LocationInGrid placeAt)
        {
            try
            {
                int w = grid.GridWidth, h = grid.GridHeight;
                bool[,] occ = new bool[w, h];

                void Paint(Item it, int ox, int oy, ItemRotation r)
                {
                    var cs = CellSize(it);
                    int iw = cs.X, ih = cs.Y;
                    if (r == ItemRotation.Vertical) { var t = iw; iw = ih; ih = t; }
                    for (int x = ox; x < ox + iw && x < w; x++)
                        for (int y = oy; y < oy + ih && y < h; y++)
                            if (x >= 0 && y >= 0) occ[x, y] = true;
                }

                foreach (var it in grid.Items)
                {
                    if (ReferenceEquals(it, exclude) || ReferenceEquals(it, alsoExclude)) continue;
                    var a = it.CurrentAddress as GridItemAddress;
                    if (a == null) continue;
                    Paint(it, a.LocationInGrid.x, a.LocationInGrid.y, a.LocationInGrid.r);
                }
                if (placeItem != null) Paint(placeItem, placeAt.x, placeAt.y, placeAt.r);

                var tcs = CellSize(target);
                for (int o = 0; o < 2; o++)
                {
                    int rw = o == 0 ? tcs.X : tcs.Y, rh = o == 0 ? tcs.Y : tcs.X;
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
                    var cs = CellSize(it);
                    int iw = cs.X, ih = cs.Y;
                    if (a.LocationInGrid.r == ItemRotation.Vertical) { var t = iw; iw = ih; ih = t; }
                    for (int x = a.LocationInGrid.x; x < a.LocationInGrid.x + iw && x < w; x++)
                        for (int y = a.LocationInGrid.y; y < a.LocationInGrid.y + ih && y < h; y++)
                            if (x >= 0 && y >= 0) occ[x, y] = true;
                }
                var tcs = CellSize(target);
                int tw = tcs.X, th = tcs.Y;
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
                        // ★ 含背包：别再出现“背包空着却掉地上”
                        foreach (var g in equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                        {
                            var loc = g.FindFreeSpace(plan.OldMag);
                            if (loc == null) continue;
                            target = g.CreateItemAddress(loc);
                            break;
                        }
                }
                if (target == null) return false;

                // ★ 旧弹匣此刻可能已无 parent（Remove 之后），Move 会抛 ⇒ 改用 Add
                if (plan.OldMag.CurrentAddress == null)
                {
                    var ar = ItemManipulator.Add(plan.OldMag, target, ic, simulate: false);
                    if (ar.Failed) return false;
                    var mr = new MoveResult(plan.OldMag, plan.GunAddr, target, ic,
                        new IgnoreDiscardLimitsResult(), null, ar.Value, null, null, null);
                    __result = new OperationResult<MoveResult>(mr);
                }
                else
                {
                    var mv = ItemManipulator.Move(plan.OldMag, target, ic, simulate: false);
                    if (mv.Failed) return false;
                    __result = mv;
                }
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
                    var displaceTo = plan.DisplaceTarget ?? plan.Dest;
                    var r3 = ItemManipulator.Move(plan.Displace, displaceTo, ic, simulate: false);
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
                // ★ 2026-11-16【回滚】原来两个 λ 互相等对方先动，会死锁（枪里是新弹匣却报“已回滚”）。
                //   改成严格逆序的一串受限操作：摘掉枪里的 → 挪位物品回原位 → 新弹匣回格子 → 旧弹匣回枪。
                pair.Rollbacks.Add(() =>
                {
                    // ★ 优先用 Swap 一步换回（旧弹匣回枪、新弹匣回格），避免多步之间留半成品
                    try
                    {
                        var oldAddr = plan.DrumSpot != null && plan.DisplaceGrid != null
                            ? plan.DisplaceGrid.CreateItemAddress(plan.DrumSpot)
                            : plan.Dest;
                        // ★ 旧弹匣已经没有 parent 时，Swap 第一句就会抛 ⇒ 直接走下面的 Add 路径
                        if (plan.OldMag.CurrentAddress == null) throw new InvalidOperationException("old magazine is orphaned");
                        var sw = ItemManipulator.Swap(plan.OldMag, plan.GunAddr, plan.NewMag, oldAddr, ic, simulate: false);
                        if (!sw.Failed) { Plugin.Log?.LogInfo("[KMR][B] rollback via swap"); return; }
                        Plugin.Log?.LogWarning("[KMR][B] rollback swap failed, falling back to sequence: " + sw.Error);
                    }
                    catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B] rollback swap error: " + ex.Message); }

                    if (plan.OldMag.CurrentAddress != null)
                    {
                        try { var rr = ItemManipulator.Remove(plan.OldMag, ic); if (rr.Failed) Plugin.Log?.LogWarning("[KMR][B] rollback: remove old failed " + rr.Error); } catch (Exception e) { Plugin.Log?.LogWarning("[KMR][B] rollback: remove old threw " + e.Message); }
                    }
                    if (plan.Displace != null && plan.DisplaceFrom != null)
                    {
                        try { var rm = ItemManipulator.Move(plan.Displace, plan.DisplaceFrom, ic, simulate: false); if (rm.Failed) Plugin.Log?.LogWarning("[KMR][B] rollback: restore displaced failed " + rm.Error); } catch (Exception e) { Plugin.Log?.LogWarning("[KMR][B] rollback: restore displaced threw " + e.Message); }
                    }
                    try { var rn = ItemManipulator.Move(plan.NewMag, plan.Dest, ic, simulate: false); if (rn.Failed) Plugin.Log?.LogWarning("[KMR][B] rollback: new mag back failed " + rn.Error); } catch (Exception e) { Plugin.Log?.LogWarning("[KMR][B] rollback: new mag back threw " + e.Message); }
                    try { var ra = ItemManipulator.Add(plan.OldMag, plan.GunAddr, ic, simulate: false); if (ra.Failed) Plugin.Log?.LogWarning("[KMR][B] rollback: old mag to gun failed " + ra.Error); } catch (Exception e) { Plugin.Log?.LogWarning("[KMR][B] rollback: old mag to gun threw " + e.Message); }

                    // ★ 校验：无 parent 的物品等于没存档（会丢），补一次兜底
                    try
                    {
                        if (plan.OldMag.CurrentAddress == null)
                        {
                            var eq = (ic as InventoryController)?.Inventory?.Equipment;
                            bool saved = false;
                            if (eq != null)
                                foreach (var g in eq.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true))
                                {
                                    var loc = g.FindFreeSpace(plan.OldMag);
                                    if (loc == null) continue;
                                    var ar = ItemManipulator.Add(plan.OldMag, g.CreateItemAddress(loc), ic, simulate: false);
                                    if (!ar.Failed) { saved = true; break; }
                                }
                            if (!saved) Plugin.Log?.LogError("[KMR][B] rollback: old magazine has no owner (may be lost)");
                        }
                    }
                    catch { }
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

        /// <summary>等 Swap 事务的 ack，确认两端落地；失败只记日志（本地已无库存改动可回滚）。</summary>
        private static Magazine _txOldMag;
        private static ItemAddress _txDest;
        private static ItemAddress _txGun;
        private static InventoryController _txIc;

        private static System.Collections.IEnumerator WatchSwapTx(Task t, Weapon weapon, Magazine nextMag, Magazine oldMag, ItemAddress dest, ItemAddress gun, InventoryController ic)
        {
            float deadline = UnityEngine.Time.time + 8f;
            while (!t.IsCompleted && UnityEngine.Time.time < deadline) yield return null;
            if (!t.IsCompleted)
            {
                Plugin.Log?.LogError("[KMR][B][tx] swap ack timed out after 8s");
                yield break;
            }
            string detail = "status=" + t.Status;
            try
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    var r = t.GetType().GetProperty("Result")?.GetValue(t, null);
                    if (r != null)
                    {
                        var okp = r.GetType().GetProperty("Succeed");
                        if (okp != null && !(bool)okp.GetValue(r, null))
                        {
                            var ep = r.GetType().GetProperty("Error");
                            detail += " error=" + (ep?.GetValue(r, null)?.ToString() ?? "?");
                        }
                    }
                }
            }
            catch (Exception ex) { detail += " threw=" + ex.Message; }
            bool acked = t.Status == TaskStatus.RanToCompletion && !detail.Contains("error=");
            if (acked) Plugin.Log?.LogInfo("[KMR][B][tx] swap acked " + detail);
            else
            {
                Plugin.Log?.LogError("[KMR][B][tx] swap failed " + detail);
                // ★ 事务没落地：本地必须真做一次 swap，否则原版会对“没移动的物品”发整套事件
                try
                {
                    if (oldMag != null && nextMag != null && dest != null && gun != null && ic != null)
                    {
                        var loc = ItemManipulator.Swap(oldMag, dest, nextMag, gun, ic, simulate: false);
                        Plugin.Log?.LogInfo("[KMR][B][tx] local fallback swap: " + (loc.Failed ? loc.Error.ToString() : "ok"));
                    }
                    else Plugin.Log?.LogWarning("[KMR][B][tx] local fallback skipped (missing context)");
                }
                catch (Exception ex) { Plugin.Log?.LogError("[KMR][B][tx] local fallback threw: " + ex.Message); }
            }

            // ack 后取证：枪上是谁、旧弹匣在哪
            try
            {
                var cur = weapon != null ? weapon.GetCurrentMagazine() : null;
                var oldParent = oldMag != null && oldMag.CurrentAddress != null ? oldMag.CurrentAddress.ContainerName : "none";
                Plugin.Log?.LogInfo($"[KMR][B][tx] postreload old={oldMag?.Id} parent={oldParent}"
                                    + $" gun={(cur == null ? "none" : cur.Id)} expectGun={nextMag?.Id} dest={dest?.ContainerName}");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][B][tx] postreload probe: " + ex.Message); }
        }

        private static void SafeRollback(ref OperationResult<RemoveResult> r, ref bool done)
        {
            if (!done) return;
            try { r.Value.RollBack(); } catch { }
            done = false;
        }



        internal static void NotifyNoSpace()
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
                        if (pair.Tx)
                        {
                            // ★ 事务式：本地没有可回滚的已执行结果（sw 是 simulate 产物，子结果已被 Swap 的 finally 回滚）
                            //   重跑只会抛 "Attempt to RollBack a result twice"。标记即可，等 ack 时再决定反向事务。
                            Plugin.Log?.LogWarning("[KMR][B][tx] vanilla asked to roll back a tx swap; local state untouched");
                            return false;
                        }
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
                if (_txTask != null)
                {
                    // ★ 交换是通过网络事务发的：此刻本地可能还没落地（等 ack），不能断言枪上是新弹匣
                    var t = _txTask; _txTask = null;
                    Plugin.Log?.LogInfo($"[KMR][B][tx] reload running, awaiting swap ack (task={t.Status})");
                    if (Plugin.Instance != null)
                        Plugin.Instance.StartCoroutine(WatchSwapTx(t, weapon, nextMagazine, _txOldMag, _txDest, _txGun, _txIc));
                    return;
                }
                var cur = weapon.GetCurrentMagazine();
                if (_isObservedCopy)
                {
                    var curMag = weapon.GetCurrentMagazine();
                    Plugin.Log?.LogInfo($"[KMR][B][coop] old={(curMag == null ? "none" : curMag.Id)} new={nextMagazine.Id}"
                        + $" {(plan != null && _swapped ? "ok" : "skip=" + (_skipReason ?? "vanilla"))}");
                }
                if (plan == null || !_swapped)
                {
                    Plugin.V("no swap this time (vanilla or fallback path)");
                    if (Plugin.NoSpaceForOldMag && !Plugin.RetentionOwns())
                    {
                        Diag.Reload(Diag.NextSeq(), null, null, nextMagazine, "solo-drop", "no-free-cell");
                        NotifyNoSpace();
                    }
                    else if (Plugin.InFikaSession() && !Plugin.RetentionOwns()
                             && _oldMagBeforeReload != null && _oldMagBeforeReload.CurrentAddress == null)
                    {
                        // ★ 联机：ReloadFix 在联机里不置位，这里补上掉地提示（否则静默掉弹匣）
                        Plugin.Log?.LogWarning("[KMR][B][coop] old magazine has no owner after reload -> notify");
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
            finally
            {
                // ★ 取证：旧弹匣最后有没有归属（只记录，不处置）
                try
                {
                    var om = _oldMagBeforeReload;
                    _oldMagBeforeReload = null;
                    if (om != null)
                        Plugin.Log?.LogInfo($"[KMR][B] postreload old={om.Id} parent={(om.CurrentAddress == null ? "none" : om.CurrentAddress.ContainerName)}");
                }
                catch { }
                _plan = null;
            }
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
            _displace = null; _displaceGrid = null; _drumSpot = null; _displaceFrom = null; _displaceTarget = null;
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
