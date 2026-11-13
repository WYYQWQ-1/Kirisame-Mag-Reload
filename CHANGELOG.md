# Changelog

## 1.0.0

- In-place magazine swap: same size and "old smaller" cases.
- Reorder path for "old bigger" (e.g. 2x2 drum into a 1x2 magazine cell), including a blocker-displacement step.
- Fallback placement (vest → pockets → backpack) so the old magazine is no longer dropped by vanilla.
- Move-back after the reload finishes when the fallback used the backpack, with position verification.
- Bottom-right notification when there is really nowhere to place the magazine.
- Bilingual UI (Chinese / English) following the game language; English logs for bug reports.
- Fika: detected and handled safely — the plugin stays idle in co-op sessions (no inventory changes), so it can not desync other players. Network sync is planned for a later version.
- UIFixes compatibility: detection only, this mod steps back when UIFixes in-place reload is ON.
