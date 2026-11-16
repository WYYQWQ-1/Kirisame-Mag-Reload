using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using HarmonyLib;

namespace SmartMagReloadMono
{
    /// <summary>联机保留的通报包（架构参考 Tosox，独立实现）：版本握手 + 谁保留了哪个弹匣。</summary>
    public struct MagRetentionPacket : INetSerializable
    {
        public const byte KindRetention = 0;
        public const byte KindHello = 1;

        public byte Kind;
        public string Version;
        public string ProfileId;
        public string MagazineId;

        public static MagRetentionPacket Create(string version, string profileId, string magazineId)
            => new MagRetentionPacket { Kind = KindRetention, Version = version, ProfileId = profileId, MagazineId = magazineId };

        public static MagRetentionPacket Hello(string version)
            => new MagRetentionPacket { Kind = KindHello, Version = version };

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Kind);
            writer.Put(Version ?? string.Empty, 0);
            writer.Put(ProfileId ?? string.Empty, 0);
            writer.Put(MagazineId ?? string.Empty, 0);
        }

        public void Deserialize(NetDataReader reader)
        {
            Kind = reader.GetByte();
            Version = reader.GetString();
            ProfileId = reader.GetString();
            MagazineId = reader.GetString();
        }
    }

    /// <summary>Fika 侧：注册通报包、收发、版本握手；版本不一致时整局关掉保留。</summary>
    internal static class CoopSync
    {
        private static readonly Dictionary<string, string> Announcements = new Dictionary<string, string>();
        private static bool _warned;
        private static bool _incompatible;
        private static string _version = "?";

        internal static bool Incompatible => _incompatible;

        internal static void Init(string version)
        {
            _version = version;
            try
            {
                FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
                FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
                Plugin.Log?.LogInfo("[KMR][net] retention packet registered hooks (version " + _version + ")");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[KMR][net] init failed (co-op notice disabled): " + ex.Message);
            }
        }

        private static void OnManagerCreated(FikaNetworkManagerCreatedEvent args)
        {
            try
            {
                args.Manager.RegisterPacket<MagRetentionPacket>(OnPacket);
                Plugin.Log?.LogInfo("[KMR][net] MagRetentionPacket registered");
                // 每次网络管理器创建（新一局）就重置，避免上一局的“版本不一致”状态带过来
                _incompatible = false; _warned = false; Announcements.Clear();
                Broadcast(MagRetentionPacket.Hello(_version));      // 开局 hello：立刻暴露版本差异
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][net] register failed: " + ex.Message); }
        }

        private static void OnGameEnded(FikaGameEndedEvent args)
        {
            try
            {
                if ((int)args.ExitStatus != 5)   // 非“撤离成功”都清
                {
                    Announcements.Clear();
                    _incompatible = false;
                    _warned = false;
                }
            }
            catch { }
        }

        private static void Broadcast(MagRetentionPacket p)
        {
            try
            {
                var mgr = Singleton<IFikaNetworkManager>.Instance;
                if (mgr == null) return;
                mgr.SendData<MagRetentionPacket>(ref p, (DeliveryMethod)2, true);
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][net] send failed: " + ex.Message); }
        }

        internal static void Announce(string profileId, string magazineId)
            => Broadcast(MagRetentionPacket.Create(_version, profileId, magazineId));

        /// <summary>观察端用：这一位玩家刚才是否宣布“要保留这个弹匣”。</summary>
        internal static bool TakeAnnouncement(string profileId, string magazineId)
        {
            try
            {
                if (string.IsNullOrEmpty(profileId)) return false;
                string v;
                if (!Announcements.TryGetValue(profileId, out v)) return false;
                Announcements.Remove(profileId);
                return !string.IsNullOrEmpty(magazineId) && v == magazineId;
            }
            catch { return false; }
        }

        private static void OnPacket(MagRetentionPacket p)
        {
            try
            {
                if (p.Version != _version) { OnMismatch(p.Version); return; }
                if (p.Kind == MagRetentionPacket.KindRetention && !string.IsNullOrEmpty(p.ProfileId))
                    Announcements[p.ProfileId] = p.MagazineId;
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR][net] packet: " + ex.Message); }
        }

        private static void OnMismatch(string other)
        {
            _incompatible = true;
            if (_warned) return;
            _warned = true;
            Plugin.Log?.LogError("[KMR][net] version mismatch: mine=" + _version + " other=" + other + " → retention off this raid");
            try
            {
                NotificationManager.DisplayWarningNotification(
                    "Kirisame Mag Reload version mismatch (you: " + _version + ", other: " + other
                    + "). Magazine retention is off for this raid.", (ENotificationDurationType)1);
            }
            catch { }
            Broadcast(MagRetentionPacket.Hello(_version));
        }
    }
}
