using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using BepInEx.Logging;

namespace SmartMagReloadMono.Fika
{
    /// <summary>
    /// 把"交换结果"广播给同局的其他玩家；接收端按物品 id 在自己的镜像里重放同样的交换。
    /// 版本不一致时整局停用同步并提示（避免两端行为不同）。
    /// </summary>
    public static class FikaBridge
    {
        public const string Version = "1.0.0";

        private static ManualLogSource _log;
        private static bool _installed;
        private static bool _incompatible;

        /// <summary>由主程序集通过反射调用；log 传主插件日志。</summary>
        public static void Init(ManualLogSource log)
        {
            _log = log;
            if (_installed) return;
            try
            {
                FikaEventDispatcher.SubscribeEvent(new Action<FikaNetworkManagerCreatedEvent>(OnManagerCreated));
                FikaEventDispatcher.SubscribeEvent(new Action<FikaGameEndedEvent>(OnGameEnded));
                _installed = true;
                SelfTest();
                _log?.LogInfo("[KMR][Sync] Fika sync ready");
            }
            catch (Exception ex)
            {
                _log?.LogWarning("[KMR][Sync] init failed: " + ex.Message);
            }
        }

        /// <summary>单机自检：验证包的写入/读取顺序与字段是否一致（不联网）。</summary>
        private static void SelfTest()
        {
            try
            {
                var src = new MagSwapPacket
                {
                    Kind = MagSwapPacket.KindSwap,
                    Version = Version,
                    ProfileId = "selftest-profile",
                    OldMagId = "selftest-old",
                    NewMagId = "selftest-new",
                };
                var w = new NetDataWriter();
                src.Serialize(w);
                var r = new NetDataReader(w.Data);
                var dst = new MagSwapPacket();
                dst.Deserialize(r);

                var ok = dst.Kind == src.Kind && dst.Version == src.Version
                         && dst.ProfileId == src.ProfileId
                         && dst.OldMagId == src.OldMagId && dst.NewMagId == src.NewMagId;
                if (ok) _log?.LogInfo("[KMR][Sync] packet round-trip self-test passed");
                else _log?.LogError("[KMR][Sync] packet round-trip self-test FAILED: field mismatch");
            }
            catch (Exception ex) { _log?.LogError("[KMR][Sync] packet self-test error: " + ex.Message); }
        }

        private static void OnManagerCreated(FikaNetworkManagerCreatedEvent args)
        {
            try
            {
                args.Manager.RegisterPacket(new Action<MagSwapPacket>(OnPacket));
                _log?.LogInfo("[KMR][Sync] packet registered for this co-op session");
            }
            catch (Exception ex) { _log?.LogWarning("[KMR][Sync] register packet failed: " + ex.Message); }
        }

        private static void OnGameEnded(FikaGameEndedEvent args)
        {
            _incompatible = false;
        }

        /// <summary>本机完成交换后调用（仅当处于联机会话）。</summary>
        public static void Broadcast(string oldMagId, string newMagId)
        {
            try
            {
                if (_incompatible || !_installed) return;
                var mgr = Singleton<IFikaNetworkManager>.Instance;
                if (mgr == null) return;

                var me = Singleton<GameWorld>.Instance?.MainPlayer;
                var profileId = me?.ProfileId;
                if (string.IsNullOrEmpty(profileId)) return;

                var packet = new MagSwapPacket
                {
                    Kind = MagSwapPacket.KindSwap,
                    Version = Version,
                    ProfileId = profileId,
                    OldMagId = oldMagId ?? string.Empty,
                    NewMagId = newMagId ?? string.Empty,
                };
                mgr.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                _log?.LogInfo($"[KMR][Sync] broadcast swap: {oldMagId} ⇄ {newMagId}");
            }
            catch (Exception ex) { _log?.LogWarning("[KMR][Sync] broadcast failed: " + ex.Message); }
        }

        private static void OnPacket(MagSwapPacket packet)
        {
            try
            {
                if (packet.Kind != MagSwapPacket.KindSwap) return;

                if (!string.Equals(packet.Version, Version, StringComparison.Ordinal))
                {
                    if (!_incompatible)
                    {
                        _incompatible = true;
                        _log?.LogError($"[KMR][Sync] version mismatch: local {Version}; peer {packet.Version} -> sync disabled for this raid");
                    }
                    return;
                }

                var world = Singleton<GameWorld>.Instance;
                var me = world?.MainPlayer;
                if (world == null || me == null) return;
                if (string.Equals(packet.ProfileId, me.ProfileId, StringComparison.Ordinal)) return; // 自己的广播

                var player = world.GetAlivePlayerByProfileID(packet.ProfileId);
                if (player == null) return;
                var ic = player.InventoryController;
                if (ic == null) return;

                Magazine oldMag = null, newMag = null;
                foreach (var it in player.Inventory.Equipment.GetAllItems())
                {
                    var m = it as Magazine;
                    if (m == null) continue;
                    if (oldMag == null && m.Id == packet.OldMagId) oldMag = m;
                    if (newMag == null && m.Id == packet.NewMagId) newMag = m;
                    if (oldMag != null && newMag != null) break;
                }
                if (oldMag == null || newMag == null)
                {
                    _log?.LogWarning("[KMR][Sync] magazines not found in peer mirror, skipped");
                    return;
                }

                var oldAddr = oldMag.CurrentAddress;
                var newAddr = newMag.CurrentAddress;
                if (oldAddr == null || newAddr == null) return;

                var res = ItemManipulator.Swap(oldMag, newAddr, newMag, oldAddr, ic, simulate: false);
                if (res.Failed)
                    _log?.LogWarning("[KMR][Sync] mirror swap failed: " + res.Error);
                else
                    _log?.LogInfo($"[KMR][Sync] applied swap on mirror: {oldMag.ShortName} ⇄ {newMag.ShortName}");
            }
            catch (Exception ex) { _log?.LogWarning("[KMR][Sync] packet handling error: " + ex.Message); }
        }
    }
}
