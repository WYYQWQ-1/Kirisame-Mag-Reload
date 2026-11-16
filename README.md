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
- **Fika**: co-op reloads use the same in-place swap; **both players must run the same version**
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

- Old magazine stays in the cell the new one came from. If it does not fit, blockers are moved aside
  (into the freed cell, or to another free cell in the rig/pockets); the backpack is only used when
  rig and pockets are full.
  旧弹匣留在新弹匣腾出的那一格；放不进时会挪开挡路件（挪进腾出的格，或挪到弹挂/口袋的其它空格），
  背包只在弹挂和口袋都放满时才会用到。
- Both players must run the same build. A version mismatch is detected at raid start and turns retention
  off for that raid (with a warning) instead of letting the two sides diverge silently.
  两端必须是同一份构建；版本不一致会在开局被检测到并整局关闭保留，而不是悄悄各算各的。
- Placement is computed on each side from the same reload event. In normal play both players see the
  same result, but it is not a networked inventory instruction, so heavy simultaneous inventory edits
  can differ. Real two-player sessions are not verified yet.
  落点是两端根据同一次换弹各自计算：正常情况一致，但不是网络指令，极端同时操作时可能有差异。
  真双人尚未验证（已测：主机侧、Fika 单人房）。
- Known limitation: quick reload (fast reload) is not touched at all — vanilla drops the magazine there.
  已知限制：快速换弹（quick reload）完全不接管，原版在那里本来就会丢掉旧弹匣。
- Logs are English and prefixed `[KMR]`.
  日志为英文，前缀 `[KMR]`。

## Settings / 设置（F12 → General）

| Key | Default | What it does / 作用 |
|---|---|---|
| `Enabled` | on | Master switch. Off =完全原版 |
| `DirectSwap` | on | In-place swap on reload (single player + co-op). 换弹原地对换 |
| `TakeoverReload` | on | Let this mod handle the reload when UIFixes' in-place reload is on. 接管 UIFixes 的换弹 |
| `Retention` | on | Co-op: keep the old magazine in place, move blockers aside. 联机：旧弹匣留原位 + 挪挡路件 |
| `CoopLocalSwap` | on | Apply the same swap on other players' copies when their reload packet is replayed. 联机：在其它玩家的副本上也执行同样交换 |
| `HostTestLog` | off | Extra co-op diagnostics. 额外联机诊断日志 |
| `DumpDiagnostics` (Debug) | off | Write `KMR-diagnostics.txt` for bug reports. 生成诊断文件 |

## Build / 编译

```powershell
dotnet build src/SmartMagReloadMono/SmartMagReloadMono.csproj -c Release -p:SptDir="D:\Your\SPT\EFT"
```

`SptDir` must point to a folder containing `BepInEx/core` and `EscapeFromTarkov_Data/Managed`.
（`SptDir` 指向你的游戏目录。）

## License / 许可

MIT. See [LICENSE](LICENSE).

## Co-op notes / 联机说明

- Both players must run the **same build** of this mod. A version mismatch is detected at raid start and
  turns retention off for that raid (with a warning) instead of letting the two sides diverge silently.
  两端必须是同一份构建；版本不一致会在开局被检测到并整局关闭保留，而不是悄悄各算各的。
- Co-op placement is computed on each side from the same reload event, so in normal play both players see
  the same result — but it is not a networked inventory instruction, so an edge case with heavy
  simultaneous inventory edits can differ. Try the experimental **True sync** option if you want the
  rearranging to go through transactions before the reload.
  联机落点是两端根据同一次换弹各自计算：正常情况一致，但不是网络指令，极端同时操作时可能有差异。
- Real two-player sessions have not been verified yet; host-side and solo-in-a-Fika-room are tested.
  真双人尚未验证（已测：主机侧、Fika 单人房）。

## Credits / 致谢

Co-op retention architecture follows **Mag Retention Reload** by Tosox
(https://github.com/Tosox/SPT-MagRetentionReload) — independent implementation, no code copied.
See `ACKNOWLEDGEMENTS.md`.
