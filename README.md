# Kirisame Mag Reload

**In-place magazine swap and no-drop fix for SPT 4.1.6 (Mono).**
**SPT 4.1.6 换弹优化：弹匣原地对换、不掉地、Fika 友好。**

Created by **Kirisame dish**

---

## What it does / 它做什么

- When a reload would leave you with nowhere to put the old magazine, drop it into a real free cell
  (vest → pockets → backpack) instead of the ground.
  换弹时旧弹匣没地方放 —— 找个真正的空位（弹挂→口袋→背包），而不是直接扔地上。
- If the chosen magazine can be swapped in place, swap positions with the old one (same size, or old smaller).
  选中的弹匣能和旧的原地对换时，直接换位（同尺寸、或旧的更小）。
- If the old magazine is **bigger** than the chosen one's cell, it reorders: new magazine goes into the gun first,
  then the old one is placed into the freed area (with a "dislocate one blocker" step when needed).
  旧弹匣比新弹匣大时走"重排"：先插新弹匣，再把旧弹匣放进腾出的区域（需要时先把占位物挪开）。
- Bottom-right notification when a magazine really has nowhere to go.
  实在没位置时，右下角提示"旧弹匣掉地上了"。
- Everything goes through the game's own inventory operations; no custom item logic.

## Requirements / 前置

- SPT **4.1.6** (Mono / BepInEx 5)
- **Fika**: installed Fika is detected; the plugin stays idle in co-op sessions (see below)
- Recommended: turn **UIFixes → "Reload Magazines In-Place"** OFF if you want this mod to handle swaps.
  (When it is ON, this mod steps back and only does fallback placement.)
  建议关闭 UIFixes 的「Reload Magazines In-Place」；它开着时本插件自动让路，只做落点兜底。

## Install / 安装

1. Drop the contents into your SPT root:
   把压缩包内容放进游戏根目录：
   ```
   BepInEx/plugins/SmartMagReloadMono/SmartMagReloadMono.dll
   ```
2. Start the game. Config appears in `F12` under **General** / **Debug**.
   进游戏后 F12 里能看到 `General` / `Debug` 两组设置。

## Config / 设置

| Key | Default | Meaning |
|---|---|---|
| Enable | on | Master switch; off = vanilla behaviour |
| In-place swap | on | Swap magazines in place when possible |
| Take over reload | off | If UIFixes in-place reload is ON, this mod steps back |
| Verbose log | off | Per-reload scan details (troubleshooting) |
| Language | Auto | Follow the game language (zh/en) |

## Fika / 联机

- **Co-op is not supported yet.** When a Fika session is detected, this plugin stays completely idle
  (vanilla reload behaviour, no inventory changes), so it can not desync other players.
  **暂不支持联机。** 检测到 Fika 会话时本插件完全不动（保持原版行为、不改物品），不会造成不同步。
- Fika synchronisation is planned for a later version; it needs a handshake and a packet that can
  describe the full swap, which is not in 1.0.0.
  联机同步会在后续版本单独做（需要握手 + 能表达完整交换的包），1.0.0 不含。
- Logs are English and prefixed `[KMR]` — attach `BepInEx/LogOutput.log` when reporting.
  日志为英文、前缀 `[KMR]`，反馈问题时请附 `LogOutput.log`。

## Build / 编译

```powershell
dotnet build src/SmartMagReloadMono/SmartMagReloadMono.csproj -c Release -p:SptDir="D:\Your\SPT\EFT"
```

`SptDir` must point to a folder containing `BepInEx/core` and `EscapeFromTarkov_Data/Managed`.
（`SptDir` 指向你的游戏目录。）

## License / 许可

MIT. See [LICENSE](LICENSE).
