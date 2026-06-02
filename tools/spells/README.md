# spells.txt rebuild

> **Purpose:** Procedure for rebuilding [`EQLogParser/data/spells.txt`](../../EQLogParser/data/spells.txt) from Dalaya's game-format `spells_us.txt`. Run after each Dalaya patch that updates spells. See also memory `project_spells_rebuild`.

Repeatable conversion of Dalaya's game-format `spells_us.txt` into the parser-format `EQLogParser/data/spells.txt`. Run after each game patch that updates `spells_us.txt`.

## When to run

Whenever the Dalaya patcher overwrites `F:\Dalaya\spells_us.txt`. Spell IDs, names, and message text can change with patches; the parser uses those messages to recognize cast/lands-on/wear-off lines in player logs.

## Procedure

1. **Back up** the current shipped file (so we can diff against it):

   ```powershell
   copy EQLogParser\data\spells.txt tools\spells\spells.previous.txt
   ```

2. **Run the converter**:

   ```powershell
   python tools\spells\convert_spells.py
   ```

   Defaults: source = `F:\Dalaya\spells_us.txt`, dest = `EQLogParser\data\spells.txt`. Pass paths positionally to override.

3. **Verify the diff** is sane:

   ```powershell
   python tools\spells\analyze_diff.py tools\spells\spells.previous.txt EQLogParser\data\spells.txt
   ```

   Expect changes only in columns the converter writes from source data — see `EXPECTED_CHANGE_COLS` in `analyze_diff.py`. Anything else is flagged as drift and indicates a mapping bug or a hand-edit, and the script exits non-zero. When a new source column is mapped, the first rebuild legitimately shows a one-time burst of changes for that column; update `EXPECTED_CHANGE_COLS` in that same change.

4. **Run the test suite**:

   ```powershell
   cd EQLogParserTest
   dotnet test -p:Platform=x64
   ```

5. **Commit** the updated `spells.txt`. Delete the `spells.previous.txt` backup once you're satisfied.

## What the converter does

`spells_us.txt` is the live-EQ-style 239-column format. The parser format is 23 columns (0–22). The converter pulls the fields the parser reads and hard-codes the rest.

Mapping (see [`EQDataStore.ParseCustomSpellData`](../../EQLogParser/src/dao/store/EQDataStore.cs)):

| Parser col | Field | Source col | Notes |
|---|---|---|---|
| 0 | Id | 0 | |
| 1 | Name | 1 | |
| 2 | Level | 104..119 | min non-255 across the 16 class-level cols (255 = no class can cast) |
| 3 | Duration | 17 | in 6-second ticks; `EQDataStore` multiplies by 6 for seconds |
| 4 | Beneficial | 83 | 0=detrimental, non-zero=beneficial |
| 5 | MaxHits | — | hard-coded `0` (not exposed by Dalaya source — see "Still unmapped" below) |
| 6 | Target | 98 | `Pet=14` enables auto pet detection |
| 7 | ClassMask | 104..119 | bitmask, bit(classid-1) set when col(103+classid) != 255 |
| 8 | Damaging | derived | `1` if any of source cols 6/7/8 is non-empty, else `0` |
| 9 | CombatSkill | — | hard-coded `0` |
| 10 | Resist | 85 | 0=unresistable, 1=magic, 2=fire, 3=cold, 4=poison, 5=disease, 6=chrom, 7=prism |
| 11 | SongWindow | 154 | `1` = spell occupies the UI song-window slot (source field is named `ShortDuration` but semantically equivalent) |
| 12 | Adps | cols 156/157/158 + upstream overlay | bitmask (1=Caster, 2=Melee, 4=Tank, 8=Healing) — see "Adps classification" below |
| 13 | Mgb | — | hard-coded `0` (live-EQ Mass-Group-Buff flag — Dalaya replaced MGB with Soulbond) |
| 14 | Rank | — | hard-coded `0` (parsed into `SpellData.Rank` but unused in code) |
| 15 | HasAmbiguityA | — | hard-coded `1` |
| 16 | HasAmbiguityB | — | hard-coded `1` |
| 17 | LandsOnYou | 6 | |
| 18 | LandsOnOther | 7 | |
| 19 | WearOff | 8 | |
| 20 | CastingTimeMs | 13 | integer ms |
| 21 | RecastTimeMs | 15 | integer ms |
| 22 | Category | 156/157/158 | semicolon-joined Dalaya CategoryDescID labels |

Class-level cols 104..119 hold per-class level requirements in EQ classid order: 104=War, 105=Clr, 106=Pal, 107=Rng, 108=Shd, 109=Dru, 110=Mnk, 111=Brd, 112=Rog, 113=Shm, 114=Nec, 115=Wiz, 116=Mag, 117=Enc, 118=Bst, 119=Ber. Verified against wiki: https://wiki.shardsofdalaya.com/wiki/<Class>_spells.

### Adps classification

`Adps` is a bitmask consumed by the Timeline UI (`Timeline.xaml.cs`) to decide which buffs render on the tanking/dps/healing timelines (1=Caster, 2=Melee, 4=Tank, 8=Healing). With every spell at `Adps=0` the Timeline renders nothing, so the converter populates it from **two sources, unioned**:

1. **Source-data category overlay** (primary, broad coverage). Dalaya's `spells_us.txt` carries up to three `CategoryDescID` values per spell at cols 156/157/158. These resolve to human-readable labels in `dbstr_us.txt` (type 5 entries) — e.g. `"HP Buffs"`, `"Damage Shield"`, `"Haste"`, `"Regen"`. The same labels the Shards of Dalaya wiki renders on per-spell pages. `extract_dalaya_categories.py` snapshots them as `dalaya-categories.json`; `convert_spells.py` maps each label to ADPS bits via a hand-curated `CATEGORY_TO_ADPS` table.
2. **Upstream name overlay** (secondary, hand-curated). `extract_upstream_adps.py` pulls `kauffman12/EQLogParser`'s curated `Adps` values for any spell name that also exists in Dalaya, snapshotted as `upstream-adps.json`. Acts as a corroborating second source — auras, recourses, and other source-untagged spells get classified through this path.

The two are **unioned** per spell: if categories say Tank and upstream says Melee, the spell stamps as Melee+Tank. When the same name has multiple upstream entries with different Adps values (rank duplicates), upstream's bitmasks are unioned during extraction.

Refresh either snapshot when its upstream changes:

```powershell
# After a Dalaya patch (refreshes both source-data files):
python tools\spells\extract_dalaya_categories.py
python tools\spells\extract_upstream_adps.py

# Then reconvert:
python tools\spells\convert_spells.py
```

Validate the curated `CATEGORY_TO_ADPS` table against upstream:

```powershell
python tools\spells\validate_adps.py
```

Reports exact / subset (safe) / superset / conflict / missed buckets against upstream's classifications. The union approach means upstream's bits are always preserved in the final stamp — conflicts and misses don't cause regressions, they just signal where the category mapping diverges from upstream's hand curation.

Coverage today: **~1,493 of Dalaya's 5,983 spells (~25%)** stamped, vs. ~5% from upstream overlay alone. Concentrated on player-buff-bar spells — direct-damage nukes, DoTs, snares, etc. correctly stamp as 0.

### Still hard-coded `0`

The SoD `winspellparser` source (https://github.com/ngdeao/SoD-winspellparser) confirms these columns exist in the source format but Dalaya doesn't populate them:

- **MaxHits (col 5)**: source col 176, with `MaxHitsType` at 175. Only 4 spells across all 5,983 have non-zero values — effectively unused. Dalaya's absorb buffs (Rune line, Rune of Absorption) use SPA 55 damage-amount absorbs, not the live-EQ MaxHits hit-counter mechanic.
- **Mgb (col 13)**: source col 185, always `0` in Dalaya. Confirmed via Dalaya forum that Soulbond replaced MGB on Dalaya.
- **Rank (col 14)**: source col 208, always `0`. Parsed into `SpellData.Rank` in the parser but never read anywhere — dead field on both sides.

## Spell-effects sidecar

`convert_spells.py` also emits `EQLogParser/data/spell-effects.json` — a per-spell record of the 12 effect slots from `spells_us.txt`. Loaded by `EQDataStore` at startup to power `ComputeHotTickInfo` (and future per-tick / tooltip features). Schema:

```json
{
  "4989": {
    "name": "Circle of Soothing",
    "durationCalc": 11,
    "durationBase": 6,
    "classMask": 32,
    "slots": [
      {"slot": 2, "spa": 100, "base1": 155, "base2": 0, "max": 0, "calc": 100}
    ]
  }
}
```

Empty slots (SPA 0 or 254 = unused marker) are filtered. Spells with no non-empty slots (placeholder rows like the Healing Increment series) are omitted entirely. Typical size: ~900KB / ~4,500 spells.

## Files

- `convert_spells.py` — the converter (source → parser format + spell-effects.json sidecar)
- `extract_dalaya_categories.py` — regenerates `dalaya-categories.json` from Dalaya's `dbstr_us.txt`
- `dalaya-categories.json` — in-repo `{category_id: label}` snapshot for type-5 (spell category) entries
- `extract_upstream_adps.py` — regenerates `upstream-adps.json` from upstream EQLogParser
- `upstream-adps.json` — in-repo `{spell_name: adps_bitmask}` map (filtered to Dalaya names)
- `validate_adps.py` — validates the curated category→Adps table against upstream's classifications
- `analyze_diff.py` — verification helper, run after conversion
- `spells.previous.txt` — temporary backup, ignore this in git (delete after verifying)
