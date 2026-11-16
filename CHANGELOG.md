# Changelog

## 1.2.1

- Co-op reloads now behave like single-player: old magazine stays in the cell the new one came from, and blockers are moved aside (into the freed cell, or to another free cell in the rig/pockets) when needed.
- No more unsearched ("black") cells after co-op reloads — the stow result now travels with the reload's own events.
- The "magazine dropped" notice no longer fires when the magazine was actually placed.
- Backpack is only used when rig and pockets are full (single-player rule).
- Version handshake over the network: if two players run different builds, retention is disabled for the raid instead of silently diverging.
- New experimental option **True sync** (default off): rearranges before the reload through network transactions instead of computing the result on each side.

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
