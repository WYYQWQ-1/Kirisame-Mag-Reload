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

## Fika / 联机 (experimental)

- Co-op in-place swaps are done through Fika's own inventory transaction, not a custom packet.
  联机换弹走的是 Fika 自带的库存事务，不是我们自己的网络包。
- Both sides must run the same mod version; Fika already rejects mismatched mod CRCs.
  两端必须是同一版本；Fika 本身会校验 mod CRC。
- Bots are handled by the game itself — this mod only touches your own reload.
  机器人完全交回游戏处理，本插件只动你自己的换弹。
- Known limitation: if the chamber is empty when you reload in co-op, the in-place swap is skipped
  and the vanilla reload runs instead (so the magazine may land in a free cell or on the ground).
  Reason: the transaction path replaces the vanilla reload, and the vanilla step that chambers a round
  is what makes an empty-chamber reload work; we do not replicate it yet.
  已知限制：联机时如果膛里没弹，我们不做原地交换，改用原版换弹（旧弹匣可能进空位或掉地）。
  原因：事务交换替换了原版流程，而"自动补一发进膛"正是原版在这一步做的，我们暂时没有复刻。
- Known limitation: the weapon's visual / ergonomic refresh after a transaction swap may lag until you switch weapons.
  已知限制：事务交换后枪械视觉/人机刷新可能滞后，切一次枪即恢复。
- **Not yet verified with two real players.** Please attach `BepInEx/LogOutput.log` when reporting.
  **尚未经过双人实测**，反馈时请附 `LogOutput.log`。
- Logs are English and prefixed `[KMR]`.
  日志为英文，前缀 `[KMR]`。

## Build / 编译

```powershell
dotnet build src/SmartMagReloadMono/SmartMagReloadMono.csproj -c Release -p:SptDir="D:\Your\SPT\EFT"
```

`SptDir` must point to a folder containing `BepInEx/core` and `EscapeFromTarkov_Data/Managed`.
（`SptDir` 指向你的游戏目录。）

## License / 许可

MIT. See [LICENSE](LICENSE).
