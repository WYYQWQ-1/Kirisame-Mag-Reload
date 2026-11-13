using System;
using System.IO;

namespace SmartMagReloadMono
{
    /// <summary>按游戏语言在中文/英文之间二选一。游戏语言读 Game.ini 的 Language 行。</summary>
    internal static class L
    {
        internal enum Lang { Auto, Chinese, English }

        private static bool _chinese;

        internal static void Init(Lang mode)
        {
            if (mode == Lang.Chinese) { _chinese = true; return; }
            if (mode == Lang.English) { _chinese = false; return; }

            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Battlestate Games", "Escape from Tarkov", "Settings", "Game.ini");

                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        if (line.IndexOf("Language", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var idx = line.IndexOf(':');
                        if (idx < 0) continue;
                        var v = line.Substring(idx + 1).Trim().Trim('"').Trim(',').ToLowerInvariant();
                        _chinese = v.StartsWith("ch") || v.StartsWith("zh") || v.StartsWith("cn");
                        return;
                    }
                }
                _chinese = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh");
            }
            catch { _chinese = false; }
        }

        internal static bool Chinese => _chinese;

        internal static string T(string en, string zh) => _chinese ? zh : en;
    }
}
