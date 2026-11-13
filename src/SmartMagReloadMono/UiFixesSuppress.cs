using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace SmartMagReloadMono
{
    /// <summary>
    /// 只读检测 UIFixes 的「就地装填」是否开着。
    /// 规则：**绝不写别人的配置文件**；它开着就由本插件让路（只做落点兜底）。
    /// </summary>
    internal static class UiFixesSuppress
    {
        private const string Guid = "com.tyfon.uifixes";
        private const string Section = "I. Item Swapping";
        private const string Key = "Reload Magazines In-Place";

        internal static void Install(Plugin host)
        {
            host.StartCoroutine(Detect());
        }

        private static IEnumerator Detect()
        {
            yield return null;
            try
            {
                PluginInfo info;
                if (!Chainloader.PluginInfos.TryGetValue(Guid, out info) || info == null || info.Instance == null)
                {
                    Plugin.Log?.LogInfo("[KMR] UIFixes not detected (or disabled)");
                    yield break;
                }

                var cfg = GetConfigFile(info.Instance);
                if (cfg == null) { Plugin.Log?.LogWarning("[KMR] cannot read UIFixes config object"); yield break; }

                ConfigEntry<bool> entry;
                if (!cfg.TryGetEntry<bool>(new ConfigDefinition(Section, Key), out entry) || entry == null)
                {
                    Plugin.Log?.LogInfo("[KMR] UIFixes has no reload option, treating as OFF");
                    yield break;
                }

                if (entry.Value)
                {
                    if (Plugin.TakeoverReload != null && Plugin.TakeoverReload.Value)
                        TakeOver(info.Instance);
                    else
                    {
                        Plugin.UiFixesDelegated = true;
                        Plugin.Log?.LogInfo("[KMR] UIFixes in-place reload is ON -> deferring, fallback only");
                    }
                }
                else
                {
                    Plugin.Log?.LogInfo("[KMR] UIFixes in-place reload is OFF -> full features");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[KMR] UIFixes detection error: " + ex.Message);
            }
        }

        private static System.Reflection.MethodInfo ResolveTakeoverTarget()
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = a.GetType("UIFixes.ReloadInPlacePatches+SwapIfNoSpacePatch");
                    if (t == null) continue;
                    return t.GetMethod("Prefix",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                }
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] takeover resolve error: " + ex.Message); }
            return null;
        }

        private static HarmonyLib.Harmony _takeoverHarmony;
        private static System.Reflection.MethodInfo _takeoverTarget;
        private static bool _takeoverApplied;

        /// <summary>开关变化时立即生效（不用重启）。</summary>
        internal static void OnTakeoverSettingChanged(object sender, EventArgs e)
        {
            try
            {
                bool on = Plugin.TakeoverReload != null && Plugin.TakeoverReload.Value;
                if (on == _takeoverApplied) return;
                if (on)
                {
                    if (_takeoverTarget == null) _takeoverTarget = ResolveTakeoverTarget();
                    if (_takeoverTarget == null) { Plugin.Log?.LogWarning("[KMR] takeover: UIFixes patch not resolved"); return; }
                    if (_takeoverHarmony == null) _takeoverHarmony = new HarmonyLib.Harmony(Plugin.Guid + ".takeover");
                    _takeoverHarmony.Patch(_takeoverTarget,
                        prefix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(UiFixesSuppress), "SkipIt")));
                    Plugin.UiFixesDelegated = false;
                    Plugin.Log?.LogInfo("[KMR] takeover enabled live: UIFixes in-place swap disabled");
                }
                else
                {
                    _takeoverHarmony.Unpatch(_takeoverTarget, HarmonyLib.AccessTools.Method(typeof(UiFixesSuppress), "SkipIt"));
                    Plugin.UiFixesDelegated = true;
                    Plugin.Log?.LogInfo("[KMR] takeover disabled live: UIFixes handles in-place reload");
                }
                _takeoverApplied = on;
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[KMR] takeover toggle error: " + ex.Message); }
        }

        /// <summary>运行时禁用 UIFixes 的"原地交换"补丁（不改它的配置文件）。</summary>
        private static void TakeOver(object uifixesInstance)
        {
            try
            {
                var m = ResolveTakeoverTarget();
                if (m == null) { Plugin.Log?.LogWarning("[KMR] takeover: UIFixes patch not found; deferring"); Plugin.UiFixesDelegated = true; return; }

                _takeoverHarmony = new HarmonyLib.Harmony(Plugin.Guid + ".takeover");
                _takeoverTarget = m;
                _takeoverHarmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(UiFixesSuppress), "SkipIt")));
                _takeoverApplied = true;
                Plugin.Log?.LogInfo("[KMR] takeover: UIFixes in-place swap disabled at runtime, this mod handles reloads");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[KMR] takeover failed: " + ex.Message + "; deferring");
                Plugin.UiFixesDelegated = true;
            }
        }

        private static bool SkipIt(ref bool __result)
        {
            __result = true;   // 让 UIFixes 的 Prefix 返回 true ⇒ 原版换弹照常，只是它不做交换
            return false;      // 不执行它原本的 Prefix 主体
        }

        private static ConfigFile GetConfigFile(object pluginInstance)
        {
            var t = pluginInstance.GetType();
            while (t != null)
            {
                var p = t.GetProperty("Config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null)
                {
                    var v = p.GetValue(pluginInstance, null) as ConfigFile;
                    if (v != null) return v;
                }
                t = t.BaseType;
            }
            return null;
        }
    }
}
