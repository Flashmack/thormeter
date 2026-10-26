# Changelog

## 0.3.0 - 2026-10-06

- Keep pinned players, names, aliases, and row selection visible at zero after Reset; clear old unselected targets.
- Add target category/search controls, a highest-damage main-target candidate, and current-connection manual target labels and Boss/Dummy tags.
- Record ordered Reset markers in new opt-in replays while continuing to read older recordings.
- Keep class inference from relabeling confirmed NPCs as players and expose bounded heal-candidate diagnostics.
- Label observed healing and HPS as provisional. Automatic boss and dummy names still require a verified stable mob code and catalog.

## 0.2.0 - 2026-10-06

- Calculate DPS using per-player active damage time and allow filtering to one observed target.
- Add pinned player rankings and selectable damage/healing sort metrics.
- Add current-connection manual aliases for unidentified player IDs.
- Add pause/resume controls that preserve current totals while capture continues.
- Store opt-in replays in the user's local application data folder and improve replay discovery.
- Preserve validated NPC names for observed target labels and expose decoder counters for missing identity names.
- Preserve pause transitions in new replay files while keeping old files readable.
- Keep Party/Force filtering and automatic boss/training-dummy recognition deferred until their packet data is verified.
- Continue treating EU live capture accuracy as under validation.
