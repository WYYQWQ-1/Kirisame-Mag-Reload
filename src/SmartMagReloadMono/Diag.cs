using System;
using EFT.InventoryLogic;

namespace SmartMagReloadMono
{
    /// <summary>结构化诊断日志：让别人回传 LogOutput.log 就能定位联机问题。</summary>
    internal static class Diag
    {
        private static int _seq;

        internal static int NextSeq() => ++_seq;

        internal static string Item(Item it)
        {
            if (it == null) return "null";
            try { return $"{it.Id}/{it.TemplateId}"; } catch { return "?"; }
        }

        internal static string Addr(ItemAddress addr)
        {
            if (addr == null) return "null";
            try
            {
                var g = addr as GridItemAddress;
                if (g != null)
                    return $"{addr.ContainerName}[{g.LocationInGrid.x},{g.LocationInGrid.y},{g.LocationInGrid.r}]";
                return addr.ContainerName;
            }
            catch { return "?"; }
        }

        internal static void Reload(int seq, string profileId, Item oldMag, Item newMag,
            string mode, string result, string detail = "")
        {
            Plugin.Log?.LogInfo($"[KMR] reload seq={seq} profile={(string.IsNullOrEmpty(profileId) ? "?" : profileId)} " +
                $"old={Item(oldMag)} new={Item(newMag)} mode={mode} result={result}" +
                (string.IsNullOrEmpty(detail) ? "" : " detail=" + detail));
        }

        internal static void Coop(string what, string detail = "")
        {
            Plugin.Log?.LogInfo($"[KMR][coop] {what}" + (string.IsNullOrEmpty(detail) ? "" : " " + detail));
        }
    }
}
