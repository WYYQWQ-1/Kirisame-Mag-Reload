# Changelog

## 1.1.0

- Co-op (Fika) reloads now swap magazines in place, the same way single-player does — the old magazine no longer detours through the backpack. Both players must run the same version of this mod.
- Co-op diagnostics: each swapped reload of another player is logged as `[KMR][B][coop]`.
- New **Dump diagnostics** option: writes version and recent logs to `KMR-diagnostics.txt` for bug reports.
- **Tested on the host side only.** The client side of a real two-machine session has not been verified yet — if you hit anything, send the `KMR-diagnostics.txt` file.

## 1.0.0 — initial release

- In-place magazine swap on reload: same-size pairs and "old magazine smaller" cases.
- Reorder path for "old magazine bigger" (e.g. drum into a small cell), including moving one blocker aside.
- Fallback placement when the game finds no spot: vest → pockets → backpack, with a move-back after the reload.
- Bottom-right notice when a magazine truly has nowhere to go.
- Bilingual UI (Chinese / English) following the game language.
- UIFixes compatibility: detect only; step back when its in-place reload is ON, optional takeover switch.
- Fika (experimental): co-op swaps use Fika's inventory transaction; bots and other players untouched.
- English diagnostic logs (`[KMR]`).
