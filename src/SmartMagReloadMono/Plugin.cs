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
        internal static ConfigEntry<L.Lang> Language;
        internal static bool UiFixesDelegated;
        internal static bool NoSpaceForOldMag;
        internal static bool FikaInstalled;

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
                    { FikaInstalled = true; break; }
                }
            }
            catch { }

            Log?.LogInfo("[KMR] Kirisame Mag Reload " + Info.Metadata.Version + " loaded (SPT 4.1.6, Fika installed: " + FikaInstalled + ", lang: " + (L.Chinese ? "zh" : "en") + ")");
            if (FikaInstalled) Log?.LogInfo("[KMR] Fika detected: plugin is idle during co-op sessions");

            _harmony = new Harmony(Guid);
            ReloadFix.Install(_harmony);
            DirectSwap.Install(_harmony);
            UiFixesSuppress.Install(this);
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private static bool _coop;
        private static float _coopChecked;

        /// <summary>仅在"真的进了联机会话"时返回 true（Fika 装了但单机玩不算）。结果缓存 10 秒。</summary>
        internal static bool InFikaSession()
        {
            if (UnityEngine.Time.time - _coopChecked < 10f) return _coop;
            _coopChecked = UnityEngine.Time.time;
            _coop = DetectFikaSession();
            if (_coop) Log?.LogInfo("[KMR] co-op session detected: plugin stays idle (vanilla reload behaviour)");
            return _coop;
        }

        private static bool DetectFikaSession()
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name.IndexOf("Fika", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var t in a.GetTypes())
                    {
                        if (t.Name != "FikaBackendUtils") continue;
                        var pi = t.GetProperty("ClientType",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        if (pi == null) continue;
                        var v = pi.GetValue(null, null);
                        return v != null && v.ToString() != "None";
                    }
                }
            }
            catch { }
            return false;
        }

        internal static void V(string msg)
        {
            if (Verbose != null && Verbose.Value) Log?.LogInfo("[KMR] " + msg);
        }
    }
}
