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
                    Plugin.UiFixesDelegated = true;
                    Plugin.Log?.LogInfo("[KMR] UIFixes in-place reload is ON -> deferring, fallback only");
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
