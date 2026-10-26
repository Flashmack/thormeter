# Thor Meter

A Windows-only, local Aion 2 combat analyzer built with C#/.NET 10 and WPF.

**Version 0.3.0.** EU live compatibility is under active validation. Demo data and automated checks are not evidence that every event is decoded correctly from your client. This application makes no claim of publisher approval or account safety.

## Build and run

Open `ThorMeter.sln` in a Visual Studio version supporting .NET 10, or use:

```powershell
dotnet build ThorMeter.sln
dotnet test tests/ThorMeter.Tests/ThorMeter.Tests.csproj
dotnet run --project src/ThorMeter.App/ThorMeter.App.csproj
```

Live capture requires the separately installed [Npcap driver](https://npcap.com/#download). Enable its WinPcap-compatible API during installation. Thor Meter does not install or bundle the driver. The app has no administrator manifest: if your Npcap installation restricts capture to administrators, its configured permissions must allow capture. Missing-driver or permissions errors are shown in the UI. Demo and recorded-file replay do not require a game session.

## Controls

- **Live / Demo:** select the source before starting. Demo generates synthetic players, late identification, and pet attribution; it is clearly marked.
- **Adapter / Refresh:** select the network adapter carrying the game connection. VPN configurations may require a different or loopback adapter.
- **Start / Stop / Pause:** start observation, stop it, or pause combat totals while capture continues. A new source/session clears totals to avoid combining unrelated entity IDs.
- **Connection / Use connection:** a single protocol-aligned candidate is selected automatically; select the intended observed connection when multiple candidates exist. Identification is based on protocol evidence, not the mere presence of network traffic.
- **Reset totals:** clear damage, healing, combat timing, and old unselected targets while retaining current-connection metadata, ownership, pinned zero-total player rows, player aliases, and a selected or manually labeled target.
- **Record:** opt in before starting Live. New JSONL files are stored in `%LOCALAPPDATA%\\ThorMeter\\Replays`; Debug builds label recordings with a `debug` filename prefix. Only selected connection traffic is recorded. The file can contain sensitive raw payloads; it is not automatically sanitized or uploaded.
- **Open replay:** opens the replay folder as the initial location for the file picker; older recordings elsewhere remain accessible. Replay uses the same reconstruction and decoder pipeline with recorded timestamps. Stop cancels replay. Replay and live capture are mutually exclusive.
- **Replay controls:** new recordings include Pause/Resume and Reset markers, so replay applies those actions in order. Raw traffic remains recorded during Pause, but paused combat is discarded from totals. Older JSONL files remain readable; files created before Reset markers cannot reproduce a live Reset. The reported live pause/replay mismatch still needs a recorded comparison.
- **Always on top:** keep the ordinary desktop window above other windows. Exclusive fullscreen games may cover it.
- **Show provisional HPS:** show or hide observed healing totals and HPS. Healing is decoded in both states; these numbers have not been verified against individual live heals. **Copy heal candidates** copies a bounded summary of decoded events by source, recipient, skill, and opcode. It can include paused events or events excluded from displayed totals; review IDs before sharing it.
- **Damage target / Pin / Rank:** choose one observed target ID or All targets. Filter targets by known Player/NPC/Pet/Unknown category or search by name/ID; select the current highest-damage NPC or unknown target as a **main target candidate**. That is a suggestion, not verified boss recognition. Pin players to the top; pinned rows remain at zero after Reset. Only pinned players get ranks while pins exist. Click a damage or healing metric header to rank by that metric, descending. Healing remains across all recipients when a damage target is selected.
- **Target label/tag:** assign a temporary label or manual Boss/Dummy tag to a selected target ID. These last for the current connection and do not make the ID a verified boss or stable mob code.
- **Selected player alias:** select a row and enter a temporary name when packet identity is missing. The alias is marked as manual and clears on connection change; it never changes the underlying entity ID.

## Damage and DPS

Rows can be filtered to all targets or one observed target, pinned, and ranked by a selected damage/healing metric. Entity IDs remain the stable identity; validated names are display labels, and unresolved sources show their IDs. Metadata arriving later updates the existing entity. Confirmed NPC sources are excluded from player rankings. Pet damage is combined with an owner only when ownership is verified, including damage observed before that mapping arrived. The local-player marker is shown only when verified. Names are accepted only from supported identity/spawn messages after strict UTF-8 and conservative character validation.

Class colors: Templar/Gladiator blue; Assassin/Ranger green; Sorcerer/Spiritmaster purple; Cleric yellow; Chanter white. Class names are also shown.

Each player has a separate active damage clock. Gaps of at least two seconds end that player's interval at the last hit; the next hit begins another interval. DPS is damage divided by that player's active time, avoiding idle time contributed by other players. With a target selected, damage and DPS are scoped to that target. Totals persist until Reset or a connection/session change. Reordered events count without moving the clock backwards. Pausing discards damage/healing while retaining identity updates and current totals; replay markers preserve that behavior.

Healing uses the same active-interval rule with its own clock, so healing cannot change DPS. When provisional HPS is enabled, the grid includes direct healing and known heal-family tick events, even for a healer who dealt no damage. HPS is decoded healing divided by healing-active time; it is not an effective-healing or overheal calculation. Class inference no longer changes a confirmed NPC into a player, but the heal-family classification and amount extraction still require live comparison.

“Nearby” means combat events delivered to this client. The meter cannot guarantee complete coverage of everyone nearby. Missing spawn or identity messages can leave actors unresolved. Damage initially attributed to an unknown actor may disappear when that actor is later confirmed as an NPC; timing already accumulated from it is not reconstructed retrospectively.

## Architecture

- `src/ThorMeter.Core`: normalized events, identity/ownership maps, reusable damage/healing aggregation and separate active timing. No capture or UI dependencies.
- `src/ThorMeter.Infrastructure`: passive adapter capture, connection selection, directional TCP reconstruction, protocol decoding, local recording and replay.
- `src/ThorMeter.App`: WPF/MVVM controls and class-colored statistics, refreshed four times per second.
- `tests/ThorMeter.Tests`: focused engine and protocol/transport regression tests.

Data flows one way: network observation → TCP streams → decoded combat events → combat engine → UI. The engine retains aggregate source-and-target totals and active intervals rather than a full combat event history.

## Privacy and safety boundary

The runtime has no telemetry, uploads, account system, cloud sync, or automatic update requests. Package restore during development contacts NuGet. Capture is passive: no game process-memory access, injection, hooks, game-file modification, packet sending/manipulation, gameplay automation, or anti-cheat interaction. It never attempts to hide itself.

Recording is off by default. Connection-selection markers in recordings preserve session changes during replay. Recordings, IDE outputs, and build outputs are excluded by `.gitignore`. No persistent raw-payload diagnostic logs are enabled by default.

## Protocol and validation

See [protocol notes](docs/PROTOCOL.md) for the pinned technical reference, implemented messages, limits, and remaining live-verification work. Adapted source notices are in [THIRD-PARTY-NOTICES.md](docs/THIRD-PARTY-NOTICES.md).

Before treating EU results as reliable, run a short controlled combat session: start capture before character login, confirm the selected flow, compare visible single-hit damage against meter totals, observe another player, check late names/classes and Reset, and replay the recording to compare totals. Packet loss, unsupported layouts, and missing identity messages must remain visible as limitations. Build/tests alone cannot establish these conditions.

Automatic Party/Force membership filtering and automatic boss/training-dummy classification remain unverified and are not included. A reliable mob catalog requires a verified stable mob code separate from the temporary target entity ID. Skill breakdowns, encounter history, publishing, installers, and elaborate overlays are also outside this milestone.


