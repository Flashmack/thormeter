# ACT and parser options (theory only, 2026-10-05)

## Recommendation

Keep Thor Meter's normalized `CombatEvent` and `CombatEngine` boundary. Do not rework the app around Advanced Combat Tracker (ACT) yet. ACT could replace the statistics UI and encounter reporting, but it cannot itself supply Aion 2 combat events. Removing Npcap requires another verified source of complete live events, independent of which statistics platform displays them.

## Why ACT is not a ready replacement

- [ACT](https://github.com/EQAditu/AdvancedCombatTracker) is a mature, extensible log analysis host with real-time/offline import, exports, history, overlays and a plugin API. Those features could save UI and reporting work.
- The [ACT downloads page](https://advancedcombattracker.com/download.php) lists an **Aion** parsing plugin last modified in 2012; it describes Aion combat logs, not Aion 2. I found no official Aion 2 plugin there. This is a finding from the current listing, not proof that no community plugin exists.
- The [pinned Aion 2 meter reference](https://github.com/SkeeveAN/Aion-DPS-Meter) explicitly uses Npcap to observe Aion 2 traffic and says its Classic Aion `Chat.log` support was removed. This strongly suggests that the old ACT Aion log parser cannot simply be dropped into the current game. Aion 2's usable live log availability still needs verification on the actual client.
- An ACT plugin would still need to obtain, decode and normalize Aion 2 events. If it uses network capture, Npcap remains. It also adds ACT installation, plugin packaging, API and UI integration, and a second application's lifecycle to debug.

## Possible routes without Npcap

1. **Game-authored combat log:** Best route if Aion 2 exposes a complete, timely, permitted local log with source, target, amount and heal/damage type. Validate with a short controlled fight before committing. Then a file-tail input for Thor Meter would be simpler than an ACT rewrite; ACT could be evaluated afterward for its reporting features.
2. **OCR of the visible combat log:** [An Aion 2 OCR meter](https://github.com/ProjackL2/aion2_dps_meter) demonstrates feasibility without packet capture. Expect missed/duplicated lines, language/UI-scale dependence, and limited coverage of other players. This may serve as a diagnostic fallback, not a presumed accuracy upgrade.
3. **ACT plugin:** Useful if the user prefers ACT's mature reporting and a reliable Aion 2 input already exists. Build a small adapter from normalized events after validating that input. Do not replace the core engine to test this idea.

## Decision gate after maintenance

First record what the current app sees: candidate connections, selected connection, packets, frames, damage events, healing events, malformed frames and unknown opcodes during a controlled hit/heal. Separately check whether the game writes a readable combat log with the same events. If a complete local log exists, prototype a log input. If not, changing to ACT will not remove the capture problem; fix and validate the current packet decoder first.
