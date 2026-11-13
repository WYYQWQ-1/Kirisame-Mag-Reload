using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SmartMagReloadMono
{
    /// <summary>
    /// SPT 4.1.6 / Mono 换弹修复（自研实现）。
    ///
    /// 三件事：
    ///   ① 落点兜底：原版没给旧弹匣找到位置时，给它一个此刻空闲的位置（弹挂→口袋→背包）
    ///   ② 换完回挪：落点在背包时，换弹结束后再挪回弹挂空位
    ///   ③ 直接交换：能对换时直接对换/调整顺序，不过背包
    /// </summary>
    [BepInPlugin(Guid, "Kirisame Mag Reload — in-place magazine swap & no-drop fix", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "yu.smartmagreload.mono";

        internal static Plugin Instance;
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<bool> DirectSwapEnabled;
        internal static ConfigEntry<bool> TakeoverReload;
        internal static ConfigEntry<L.Lang> Language;
        internal static bool UiFixesDelegated;
        internal static bool NoSpaceForOldMag;
        internal static bool FikaInstalled;
        internal static string FikaVersion = "?";

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // 先按 ini/系统决定语言，再绑定任何显示名，避免语言项自己永远是英文
            L.Init(L.Lang.Auto);

            Language = Config.Bind("General", "Language", L.Lang.Auto,
                new ConfigDescription(
                    L.T("Auto: follow the game language.", "自动：跟随游戏语言。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Language", "语言") }));
            L.Init(Language.Value);

            Enabled = Config.Bind("General", "Enable", true,
                new ConfigDescription(
                    L.T("Master switch. When off, vanilla reload behaviour is used.", "总开关。关掉后完全用原版换弹。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Enable", "启用") }));

            DirectSwapEnabled = Config.Bind("General", "InPlaceSwap", true,
                new ConfigDescription(
                    L.T("Swap magazines in place when possible: no backpack trip, nothing dropped, vanilla animation kept.",
                        "能对换时直接对换：不过背包、不掉地、保留原版动画。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("In-place swap", "直接交换") }));



            TakeoverReload = Config.Bind("General", "TakeoverReload", false,
                new ConfigDescription(
                    L.T("When UIFixes in-place reload is ON: enable this to disable its swap at runtime (its config file is not modified) and let this mod handle reloads.",
                        "当 UIFixes 的就地装填开着时：打开此项会在运行时禁用它的交换（不改它的配置文件），由本插件接管换弹。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Take over reload", "接管换弹") }));

            Verbose = Config.Bind("Debug", "VerboseLog", false,
                new ConfigDescription(
                    L.T("Print per-reload scan details (for troubleshooting).", "打印每次换弹的扫描细节（排错用）。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Verbose log", "详细日志") }));

            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name.IndexOf("Fika", StringComparison.OrdinalIgnoreCase) >= 0)
                    { FikaInstalled = true; FikaVersion = a.GetName().Version == null ? "?" : a.GetName().Version.ToString(); break; }
                }
            }
            catch { }

            Log?.LogInfo("[KMR] Kirisame Mag Reload " + Info.Metadata.Version + " loaded (SPT 4.1.6, Fika installed: " + FikaInstalled + ", lang: " + (L.Chinese ? "zh" : "en") + ")");
            if (FikaInstalled) Log?.LogInfo("[KMR] Fika detected (version " + FikaVersion + "): solo full features, co-op uses transaction swap / placement");

            _harmony = new Harmony(Guid);
            ReloadFix.Install(_harmony);
            DirectSwap.Install(_harmony);
            UiFixesSuppress.Install(this);
            TakeoverReload.SettingChanged += (a, b) => UiFixesSuppress.OnTakeoverSettingChanged(null, EventArgs.Empty);
            FikaCoopPlacement.Install(_harmony);
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private static bool _coop;
        private static string _coopDetail = "";

        /// <summary>
        /// 只有在"这一局真的还有别人"时才算联机。
        /// Fika 的单机也是自己开房（Host、0 人），所以不能只看 ClientType / 控制器类型。
        /// </summary>
        internal static bool InFikaSession()
        {
            try
            {
                var isClient = GetFikaBool("IsClient");
                var isServer = GetFikaBool("IsServer");
                var peers = GetFikaPeerCount();
                var coop = isClient || (isServer && peers > 0);

                var detail = $"role={(isClient ? "Client" : isServer ? "Host" : "None")} peers={peers}";
                if (coop != _coop || detail != _coopDetail)
                {
                    _coop = coop; _coopDetail = detail;
                    Log?.LogInfo($"[KMR] session {detail} -> {(coop ? "co-op: plugin idle for local swaps (uses Fika transaction path)" : "single-player: full features")}");
                }
                return coop;
            }
            catch (Exception ex)
            {
                Log?.LogWarning("[KMR] session detect error: " + ex.Message);
                return false;
            }
        }

        private static System.Reflection.PropertyInfo _pIsClient, _pIsServer, _pNetServer, _pPeers;
        private static Type _tBackend, _tServer, _tNet;

        private static void ResolveFikaTypes()
        {
            if (_tBackend != null) return;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name.IndexOf("Fika", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var t in a.GetTypes())
                {
                    if (t.Name == "FikaBackendUtils") _tBackend = t;
                    else if (t.Name == "FikaServer") _tServer = t;
                    else if (t.Name == "FikaNetServer") _tNet = t;
                }
                if (_tBackend != null && _tServer != null) break;
            }
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            _pIsClient = _tBackend?.GetProperty("IsClient", F);
            _pIsServer = _tBackend?.GetProperty("IsServer", F);

            if (_tServer != null)
            {
                var inst = typeof(Comfort.Common.Singleton<>).MakeGenericType(_tServer)
                    .GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (inst != null)
                {
                    _pNetServer = _tServer.GetProperty("NetServer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var net = _pNetServer?.GetValue(inst);
                    if (net != null)
                        _pPeers = net.GetType().GetProperty("ConnectedPeersCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                }
            }
        }

        private static bool GetFikaBool(string name)
        {
            ResolveFikaTypes();
            var p = name == "IsClient" ? _pIsClient : _pIsServer;
            return p != null && (bool)(p.GetValue(null) ?? false);
        }

        private static int GetFikaPeerCount()
        {
            ResolveFikaTypes();
            try
            {
                if (_pNetServer == null) return 0;
                var inst = typeof(Comfort.Common.Singleton<>).MakeGenericType(_tServer)
                    .GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (inst == null) return 0;
                var net = _pNetServer.GetValue(inst);
                if (net == null || _pPeers == null) return 0;
                return (int)(_pPeers.GetValue(net) ?? 0);
            }
            catch { return 0; }
        }

        internal static void V(string msg)
        {
            if (Verbose != null && Verbose.Value) Log?.LogInfo("[KMR] " + msg);
        }
    }
}
