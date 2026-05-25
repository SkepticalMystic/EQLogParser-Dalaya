# Multi-log collation (Raid Damage window)

> **Purpose:** Feature deep-dive for the Raid Damage multi-log merge — 3-layer alignment, dedup, ModifiersMask handling, and surrounding architecture. Read when touching `FightMerger`, `RaidDamageView`, `ParseContext`, or related parsers/merge logic.

Shipped in 1.1.0 (2026-05-13). Foundation + alignment + drift compensation + order-pair dedup + cross-TZ ModifiersMask collapse merged to master. Verified cross-TZ end-to-end with a 4-source / 4-fight Dalaya raid sample — Iskkath's Experiment, Kara'Kadar, Old Overgrowth, and Skoal the Malignant all land within ~1% of dev-stated boss HP. Healing tab still blocked on `RecordsStore` DI refactor.

## Problem

Each player's log is incomplete due to proximity constraints. DS damage is only visible to the tank. Melee fighters near the boss see more melee hits but fewer ranged hits. No single log gives an accurate picture of raid damage.

## Cross-source alignment (3-layer correction)

EQ logs use each player's local clock, with three sources of misalignment that the merge has to peel off before deduping. They're correctable in this order:

**Layer 1 — Constant timezone offset.** [FightOffsetDetector.cs](../src/control/managers/FightOffsetDetector.cs) bins per-pair fight-start time deltas into 15-minute buckets, picks the modal bucket as the offset. Snaps to whole-hour-or-fraction-of-hour real-world timezone differences (1h, 30m for India/SA, 45m for Nepal). Anchor for offset = source with the most fights (most complete log). Stored as `FightSource.TimeOffsetSeconds` and `RaidDamageSource.TimeOffsetSeconds`. UI: auto-detected on each `Add Source`, manually overridable via right-click → "Set time offset…", or re-runnable via the "Detect" toolbar button. Displays as `+1h`, `-30m`, `+1h 30m` next to source name.

**Layer 2 — Self-reference resolution.** Each player's log writes their own spell hits as `"X has taken N damage from your Spell"` (Dalaya self format), while observers see `"X has taken N damage from PlayerName by Spell"`. Without per-context resolution, both would parse to `Attacker = ConfigUtil.PlayerName` (the live user), so a non-active source's self-cast damage gets mis-attributed and won't dedup against the third-person observation. `RaidDamageView.AddSourceClick` sets `source.Context.PlayerRegistry.PlayerName = sourcePlayer` after constructing each isolated context. The 4 damage/cast/misc/healing parsers read `_playerRegistry.PlayerName` (and the new instance `_playerRegistry.ReplacePlayer(name, alt)` method) instead of `ConfigUtil.PlayerName` everywhere `"you/your"` is resolved. Without this, every source's `"X has taken N from your Spell"` would resolve to whoever was running the app, double-attributing damage and breaking cross-source dedup.

**Layer 3 — Linear drift compensation.** Real-world Dalaya two-player logs show clock drift growing within a single fight (observed up to 40+ seconds across a 3-minute boss). [FightDriftDetector.cs](../src/control/managers/FightDriftDetector.cs) runs per-cluster: pairs `DamageRecord` observations between sources by record-identity + sequence (≥5 pairs required; |slope| > 0.5 rejected as implausible) and fits ordinary least squares `drift(t) = a + b·(t − t0)` in target-time domain. [DriftFunction.cs](../src/control/managers/DriftFunction.cs) wraps the fit; `Correct(t) = t − Predict(t)` pulls the source's timestamps into the merged frame. **Drift anchor selection differs from offset anchor** — drift anchor is the source with the *smallest mean event time* (the fastest clock, closest to physical real time); using the offset anchor (largest source) pushes correction the wrong direction when the larger source is slower. Drift correction applies only to non-anchor sources; anchor events pass through unchanged. Without drift correction, lone-source events (e.g. tank-only-visible pet melee) inflated the merged fight duration by however many seconds the source had drifted by then.

## Cross-source dedup

After the three correction layers, [FightMerger.MergeBlocks](../src/control/managers/FightMerger.cs) does **order-pair dedup**: for each unique `DamageRecord`, the N-th observation across each source's sorted timestamp list pairs into a single emit. Each iteration consumes one entry from every source that still has observations and emits at `min(consumed times)`. Total emits per record = max observations from any one source.

This replaced an earlier time-bucket clustering approach. Time bounds (3s, then 10s) couldn't keep up with the observed drift growth, leaving late-fight ticks un-deduped → 2× damage. Order pairing is invariant to drift size, at the cost of under-counting genuinely-distinct same-record events that two sources independently miss disjointly (rare — variable damage rolls usually distinguish ticks across recasts).

**ModifiersMask wildcard collapse** (shipped in 1.1.0): the dedup key includes `ModifiersMask`, which would split the same physical hit into two buckets when one source captured a crit/lucky/twincast announcement and another didn't. The pairing of `"X scores a critical hit! (N)"` to a damage line uses a 1-second window in `DamageLineParser`, and EQ's 1-second log granularity means intra-second line ordering varies per client. Before emit, `CollapseModifierMaskVariants` groups perRecord entries by everything *except* `ModifiersMask`, OR's all concrete (`>= 0`) masks together, and treats `-1` (unparsed sentinel) as a wildcard. Closed >90% of the cross-TZ over-count.

DS filter (damage records only): kept from the holder's own source log; the parser stores `Attacker = holder, Defender = NPC`.

## Merge diagnostics

`FightMerger.MergeDiagnosticsEnabled` (static field) gates per-cluster `[MergeDiag]` log lines that detect dedup-key splits — records sharing `(Attacker, Defender, Total)` but living in different perRecord buckets due to metadata desync. Off by default. Toggleable from **Help → "Log Merge Diagnostics (Raid Damage)"** in the main window; persisted under `ConfigUtil` key `LogMergeDiagnostics`. Useful when investigating future cross-source over-counts — the diagnostic identifies the desync field (ModifiersMask, AttackerOwner, Type, etc.) and quantifies the excess events + damage per cluster.

## UI

[RaidDamageView.xaml](../src/ui/raiddamage/RaidDamageView.xaml) + [RaidDamageView.xaml.cs](../src/ui/raiddamage/RaidDamageView.xaml.cs) is a three-pane window: source list (with offset badge per row, Add/Remove/Detect toolbar) / merged fights list / tabbed DPS + Tanking summaries. The summary panes embed real `DamageSummary` and `TankingSummary` controls bound to the isolated `ParseContext`'s managers — full feature parity with the main tabs (column chooser, pet rollups, right-click menu, DPS Breakdown drill-in). [RaidOffsetDialog.xaml](../src/ui/raiddamage/RaidOffsetDialog.xaml) is the manual offset-override input.

`LogProcessor.ProcessSync` is the synchronous entry point — reuses `DoPreProcess` so chat-skipping, double-line splitting, and the parser chain match the live tailing flow.

## DI / ParseContext

Most parsing infrastructure went through a DI refactor to enable isolated parse contexts (used by this feature). The pattern:

- **`ParseContext`** (`src/control/util/ParseContext.cs`) bundles one `EQDataStore` + `PlayerRegistry` + parsers + managers (`DamageLineParser`, `HealingLineParser`, `CastLineParser`, `MiscLineParser`, `PreLineParser`, `LineModifiersParser`, `FightManager`, `DamageStatsBuilder`, `TankingStatsBuilder`, `HealingStatsBuilder`).
- **`Live(fightManager)`** wraps the app singletons. Used by the live tailing flow.
- **`CreateIsolated()`** constructs fully independent instances. Used by `RaidDamageView` to parse exported logs in the background without touching live session state.
- Each parser/manager has a parameterless ctor that delegates to live singletons (`this(EQDataStore.Instance, PlayerRegistry.Instance)`) plus a DI ctor for explicit injection. `Instance { get; }` accessor on each manager preserves the singleton entry point for callers that haven't been refactored.
- **`PlayerRegistry.SeedFrom(other)`** copies state between instances — used by raid damage to seed the isolated `PlayerRegistry` from the live one (so verified-player/pet knowledge carries into the isolated parse).
- **Not yet refactored**: `RecordsStore` (stores heals/deaths/loot/etc) is still a global singleton. This blocks adding a Healing tab — see memory note `project_healing_raid_tab` for the plan.

### UI host shims

`DamageSummary`, `TankingSummary`, and `HealingSummary` each have an `IDamageSummaryHost` / `ITankingSummaryHost` / `IHealingSummaryHost` shim that abstracts the globals each control reaches into (`FightManager.EventsClearedActiveData`, `MainActions.EventsChartOpened`, `MainActions.Events*SummaryOptionsChanged`, copy/selection actions). Default `MainActionsHost` impls forward to the live statics. Embedded raid-damage variants (`RaidDamageHost`, `RaidDamageTankingHost`, `RaidDamageHealingHost`) take a `FightManager` ctor argument and route the clear-active-data subscription onto the isolated `FightManager` so loading a new main log doesn't blow away the raid-damage view, and no-op the chart/copy/selection paths that only make sense for the "current parse".

Each summary control also takes an optional `columnPersistenceKey` so the embedded raid-damage instance persists column visibility under a separate `ConfigUtil` key (e.g. `RaidDamageSummaryColumns`) without overwriting the main tab's preferences.

## Data flow

`LogReader` → `BlockingCollection<LogReaderItem>` → `LogProcessor` (uses parsers from its `ParseContext`) → `DamageRecord`/`HealRecord` objects → grouped into `ActionGroup` (one per second) → stored in `Fight.DamageBlocks` / `Fight.TankingBlocks`. Heal records skip the Fight graph and go to `RecordsStore.Instance` directly.

## Tests

Multi-log feature tests live in `EQLogParser.Test/src/control/`: `FightMergerTest` (offset, drift, order-pair dedup, DS filter, asymmetric counts, ModifiersMask wildcard collapse — 36 tests), `FightOffsetDetectorTest` (15-min bucket detection, anchor selection), `FightDriftDetectorTest` (regression accuracy, anchor flip on negative slope, slope clamp), `ParseContextTest`. `DalayaDamageLineParserTest` covers `_playerRegistry.PlayerName` self-reference resolution.

## What's NOT done

- Healing tab in Raid Damage window — blocked on `RecordsStore` DI refactor. See memory note `project_healing_raid_tab` for the scope. Foundation for the embedded `HealingSummary` is in place (DI ctor, host shim, column key) — only the heal-record sourcing is missing.
