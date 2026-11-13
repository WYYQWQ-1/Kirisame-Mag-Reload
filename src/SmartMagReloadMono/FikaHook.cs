using System;
using System.IO;
using System.Reflection;

namespace SmartMagReloadMono
{
    /// <summary>
    /// 可选加载 Fika 同步程序集（没装 Fika 的玩家不受影响）。
    /// 主程序集不直接引用 Fika.Core，避免缺 Fika 时插件加载失败。
    /// </summary>
    internal static class FikaHook
    {
        private static MethodInfo _broadcast;
        internal static bool Ready { get; private set; }

        internal static void TryLoad()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "";
                var dll = Path.Combine(dir, "SmartMagReloadMono.Fika.dll");
                if (!File.Exists(dll)) return;

                var asm = Assembly.LoadFrom(dll);
                var t = asm.GetType("SmartMagReloadMono.Fika.FikaBridge");
                if (t == null) return;

                var init = t.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
                init?.Invoke(null, new object[] { Plugin.Log });

                _broadcast = t.GetMethod("Broadcast", BindingFlags.Public | BindingFlags.Static);
                Ready = _broadcast != null;
                if (Ready) Plugin.Log?.LogInfo("[KMR][Sync] Fika sync module loaded");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[KMR][Sync] Load failed (solo play unaffected): " + ex.Message);
            }
        }

        internal static void Broadcast(string oldId, string newId)
        {
            try { _broadcast?.Invoke(null, new object[] { oldId, newId }); } catch { }
        }
    }
}
