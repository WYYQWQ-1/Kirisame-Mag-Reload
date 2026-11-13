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
        internal static bool FikaInstalled;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

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

            DirectSwapEnabled = Config.Bind("General / 常规", "In-place swap / 直接交换", true,
                "Swap magazines in place when possible: no backpack trip, nothing dropped, vanilla animation kept.\n" +
                "When off: only fallback placement + move back from backpack.\n" +
                "能对换时直接对换：不过背包、不掉地、保留原版动画；关掉则只做落点兜底 + 换完从背包挪回。");

            TakeoverReload = Config.Bind("General / 常规", "Take over reload / 接管换弹", false,
                "Default off: if UIFixes in-place reload is ON, this mod steps back and only does fallback placement.\n" +
                "Turn UIFixes in-place reload off yourself if you want this mod to take over.\n" +
                "默认关：UIFixes 的就地装填开着时本插件让路；要接管请自行关闭 UIFixes 的那一项。");

            Verbose = Config.Bind("Debug / 调试", "Verbose log / 详细日志", false,
                "Print per-reload scan details (for troubleshooting).\n打印每次换弹的扫描细节（排错用）。");

            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name.IndexOf("Fika", StringComparison.OrdinalIgnoreCase) >= 0)
                    { FikaInstalled = true; break; }
                }
            }
            catch { }

            Log?.LogInfo("[KMR] Kirisame Mag Reload 1.0.0 loaded (SPT 4.1.6, Fika installed: " + FikaInstalled + ", lang: " + (L.Chinese ? "zh" : "en") + ")");
            if (FikaInstalled) Log?.LogInfo("[KMR] Fika detected: full features in solo, auto-disabled in co-op sessions");

            _harmony = new Harmony(Guid);
            ReloadFix.Install(_harmony);
            DirectSwap.Install(_harmony);
            UiFixesSuppress.Install(this);
            FikaHook.TryLoad();
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        /// <summary>仅在"真的进了联机会话"时返回 true（Fika 装了但单机玩不算）。</summary>
        internal static bool InFikaSession()
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
