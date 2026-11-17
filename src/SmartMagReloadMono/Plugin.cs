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
    [BepInPlugin(Guid, "Kirisame Mag Reload — in-place magazine swap & no-drop fix", "1.2.2")]
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
        internal static ConfigEntry<bool> CoopZeroGap;
        /// <summary>真同步开关：开=换弹前用网络事务预挪（两端同一份指令），关=Tosox 同款两端各算。</summary>
        internal static ConfigEntry<bool> TrueSync;
        internal static ConfigEntry<bool> HostTestLog;
        internal static ConfigEntry<bool> CoopLocalSwap;
        /// <summary>联机：把一次 Swap 作为一条网络事务发出去（真同步，默认关）。</summary>
        internal static ConfigEntry<bool> SwapNetworkTx;
        /// <summary>★仅测试：编译期常量，Release 包内不存在（-p:ForceCoop=true 才编进去）。</summary>
#if KMR_FORCE_COOP
        internal static readonly bool ForceCoopForTest = true;
#else
        internal static readonly bool ForceCoopForTest = false;
#endif
        internal static ConfigEntry<bool> DumpDiagnostics;
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

            CoopZeroGap = Config.Bind("General", "CoopZeroGap", false,
                new ConfigDescription(
                    L.T("Co-op only: pre-move what is in the way, then re-issue the reload so the old magazine lands in its final cell with no interim step. Adds one host round-trip before the animation.",
                        "仅联机：先挪开挡路的东西，再重新发起换弹，让旧弹匣一步到最终位置（没有中间态；代价是每次换弹动画晚一个往返）。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Co-op: zero-gap swap", "联机：零中间态") }));


            DumpDiagnostics = Config.Bind("Debug", "DumpDiagnostics", false,
                new ConfigDescription(
                    L.T("Tick this to write KMR-diagnostics.txt next to the BepInEx folder (mod/fika versions, session, last KMR log lines). Attach that file when reporting a co-op issue; it turns itself back off.",
                        "勾一下会把 KMR-diagnostics.txt 写到 BepInEx 同级目录（插件/Fika 版本、会话、最近 KMR 日志行）。报联机问题时附上这个文件；勾完会自动关回去。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Dump diagnostics", "导出诊断日志") }));
            DumpDiagnostics.SettingChanged += (a, b) => { if (DumpDiagnostics.Value) DumpDiag(); };

            CoopLocalSwap = Config.Bind("General", "CoopLocalSwap", true,
                new ConfigDescription(
                    L.T("Co-op: also apply the same in-place swap on other players' copies when their reload packet is replayed. Both sides must run this mod.",
                        "联机：其他玩家的换弹包重放时，也在他那份副本上执行同样的交换。双方都要装本插件。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Co-op in-place swap (both sides)", "联机就地交换（双方）") }));

            RetentionEnabled = Config.Bind("General", "Retention", true,
                new ConfigDescription(
                    L.T("Co-op only: keep the old magazine in place (stash it back into the cell the new magazine came from; no backpack access unless the rig/pockets are full).",
                        "仅联机：换下来的旧弹匣留在原地（放回新弹匣腾出的那格；弹挂/口袋满时才用背包）。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Co-op: keep old magazine in place", "联机：旧弹匣留原位") }));





            HostTestLog = Config.Bind("Debug", "HostTestLog", true,
                new ConfigDescription(
                    L.T("Co-op host only: print a step-by-step line for every reload so the host side can be checked from the log.",
                        "仅联机主机：每次换弹按步骤打印一行，方便从日志核对主机侧结果。"),
                    null,
                    new ConfigurationManagerAttributes { DispName = L.T("Host test log", "主机测试日志") }));

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
            Log?.LogInfo("[KMR] co-op retention architecture credit: Tosox (SPT-MagRetentionReload), independent implementation");
            if (FikaInstalled) Log?.LogInfo("[KMR] Fika detected (version " + FikaVersion + "): solo full features, co-op uses transaction swap / placement");
            if (FikaInstalled) InitCoopSyncSafely(Info.Metadata.Version.ToString());
#if KMR_FORCE_COOP
            Log?.LogWarning("[KMR] TEST BUILD: forced co-op path (KMR_FORCE_COOP) — do not ship");
#endif

            _harmony = new Harmony(Guid);
            ReloadFix.Install(_harmony);
            DirectSwap.Install(_harmony);
            UiFixesSuppress.Install(this);
            TakeoverReload.SettingChanged += (a, b) => UiFixesSuppress.OnTakeoverSettingChanged(null, EventArgs.Empty);
            FikaCoopPlacement.Install(_harmony);
            Retention.Install(_harmony);
        }

        /// <summary>
        /// 单独方法 + NoInlining：没装 Fika 时这段永远不会被 JIT，
        /// 避免因为缺 Fika.Core 引用导致整个插件加载失败。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void InitCoopSyncSafely(string version)
        {
            try { CoopSync.Init(version); }
            catch (Exception ex) { Log?.LogWarning("[KMR] co-op notice disabled: " + ex.Message); }
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private static bool _forceCoopLogged;
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

        /// <summary>Fika 会话（不看 peer 数）：客户端或主机都算。用于避免 solo hook 双接管。</summary>
        /// <summary>是否走“联机事务路径”（真实联机，或测试编译时强制）。只给让路判断用，别扩散。</summary>
        /// <summary>是不是 Fika 会话（含主机 0 人）。纯单人不算。</summary>
        internal static bool FikaSessionAny()
        {
            try { return GetFikaBool("IsClient") || GetFikaBool("IsServer"); } catch { return false; }
        }

        internal static bool SwapTxActive()
        {
            return (InFikaSession() || (ForceCoopForTest && FikaSessionAny()))
                && SwapNetworkTx != null && SwapNetworkTx.Value;
        }

        /// <summary>联机弹匣保留开关（架构参考 Tosox 的 MagRetentionReload，独立实现）。</summary>
        internal static ConfigEntry<bool> RetentionEnabled;

        /// <summary>联机生效判定：真联机，或测试构建在 Fika 房里强制。</summary>
        /// <summary>这次换弹是否由 Retention 全权负责（别人不要再判空间、不要再弹提示）。</summary>
        internal static bool RetentionOwns()
        {
            try { return Retention.HandlesThisReload(); }   // 一处判定，四处引用，避免漂移
            catch { return false; }
        }

        internal static bool CoopSession()
        {
            try
            {
                if (InFikaSession()) return true;
                // 测试构建：只要在对局里就走联机路径（不需要真的开 Fika 房），
                // 发布构建里 ForceCoopForTest 是编译期 false，这段不存在。
                if (ForceCoopForTest && InRaidForTest()) return true;
                return false;
            }
            catch { return false; }
        }

        private static bool InRaidForTest()
        {
            try
            {
                var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
                return gw != null && gw.MainPlayer != null;
            }
            catch { return false; }
        }

        internal static bool RetentionOn()
        {
            return RetentionEnabled != null && RetentionEnabled.Value;
        }

        internal static bool CoopPathActive()
        {
            // 真联机（有别人）才走事务路径；Fika 房但 peers=0 等于自己玩 ⇒ 走单机逻辑（更快、更成熟）
            var tested = ForceCoopForTest && FikaSessionAny();   // 仅测试构建：Fika 房内强制走事务
            bool zeroGap = CoopZeroGap != null && CoopZeroGap.Value;
            bool trueSync = TrueSync != null && TrueSync.Value;
            return (InFikaSession() || tested) && (zeroGap || trueSync);
        }

        internal static bool IsHostCoop()
        {
            try { return GetFikaBool("IsServer") && GetFikaPeerCount() > 0; }
            catch { return false; }
        }

        internal static int PeerCount()
        {
            try { return GetFikaPeerCount(); } catch { return 0; }
        }

        /// <summary>把版本、会话状态和最近 KMR 日志行写成一个文件，方便别人交日志。</summary>
        internal static void DumpDiag()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Kirisame Mag Reload diagnostics");
                sb.AppendLine("time      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("plugin    : " + (Instance != null ? Instance.Info.Metadata.Version.ToString() : "?"));
                sb.AppendLine("fika      : " + (FikaInstalled ? FikaVersion : "not installed"));
                sb.AppendLine("session   : IsClient=" + GetFikaBool("IsClient") + " IsServer=" + GetFikaBool("IsServer") + " peers=" + GetFikaPeerCount());
                sb.AppendLine("flags     : enable=" + (Enabled != null && Enabled.Value)
                    + " inPlaceSwap=" + (DirectSwapEnabled != null && DirectSwapEnabled.Value)
                    + " zeroGap=" + (CoopZeroGap != null && CoopZeroGap.Value)
                    + " takeover=" + (TakeoverReload != null && TakeoverReload.Value)
);
                sb.AppendLine("uiFixes   : " + UiFixesDelegated);
                sb.AppendLine();
                sb.AppendLine("--- last KMR lines ---");
                var logPath = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                if (System.IO.File.Exists(logPath))
                {
                    var lines = System.IO.File.ReadAllLines(logPath);
                    int n = 0;
                    for (int i = lines.Length - 1; i >= 0 && n < 400; i--)
                    {
                        if (lines[i].Contains("[KMR]")) { n++; }
                        if (lines[i].Contains("[KMR]")) sb.AppendLine(lines[i]);
                    }
                }
                var outPath = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "KMR-diagnostics.txt");
                System.IO.File.WriteAllText(outPath, sb.ToString());
                Log?.LogInfo("[KMR] diagnostics written to " + outPath);
            }
            catch (Exception ex) { Log?.LogWarning("[KMR] diagnostics failed: " + ex.Message); }
            finally
            {
                try { DumpDiagnostics.Value = false; } catch { }
            }
        }

        internal static void V(string msg)
        {
            if (Verbose != null && Verbose.Value) Log?.LogInfo("[KMR] " + msg);
        }
    }
}
