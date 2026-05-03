# spells.txt rebuild

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

   Expect changes only in columns `1` (Name — game renames), `8` (Damaging — derived), and `17/18/19` (lands-on/wear-off text). Anything else is flagged as drift and indicates a mapping bug or a hand-edit. The script exits non-zero in that case.

4. **Run the test suite**:

   ```powershell
   cd EQLogParserTest
   dotnet test -p:Platform=x64
   ```

5. **Commit** the updated `spells.txt`. Delete the `spells.previous.txt` backup once you're satisfied.

## What the converter does

`spells_us.txt` is the live-EQ-style 239-column format. The parser only reads 5 fields and ignores the rest. The converter copies those 5 fields, hard-codes the rest to defaults, and writes the 20-column parser format.

Mapping (see [`DataManager.ParseCustomSpellData`](../../EQLogParser/src/dao/store/DataManager.cs)):

| Parser col | Field | Source col | Notes |
|---|---|---|---|
| 0 | Id | 0 | |
| 1 | Name | 1 | |
| 2 | Level | — | hard-coded `0` |
| 3 | Duration | — | hard-coded `0` |
| 4 | Beneficial | — | hard-coded `0` |
| 5 | MaxHits | — | hard-coded `0` |
| 6 | Target | — | hard-coded `5` |
| 7 | ClassMask | — | hard-coded `0` |
| 8 | Damaging | derived | `1` if any of source cols 6/7/8 is non-empty, else `0` |
| 9 | CombatSkill | — | hard-coded `0` |
| 10 | Resist | — | hard-coded `0` |
| 11 | SongWindow | — | hard-coded `0` |
| 12 | Adps | — | hard-coded `0` |
| 13 | Mgb | — | hard-coded `0` |
| 14 | Rank | — | hard-coded `0` |
| 15 | HasAmbiguityA | — | hard-coded `1` |
| 16 | HasAmbiguityB | — | hard-coded `1` |
| 17 | LandsOnYou | 6 | |
| 18 | LandsOnOther | 7 | |
| 19 | WearOff | 8 | |

The zeroed metadata columns (level/duration/classmask/etc.) are intentional. Dalaya only uses `spells.txt` to match cast and effect text in log lines — class, level, and ADPS data isn't surfaced anywhere in the Dalaya UI today. If we want automatic pet detection (the live-EQ feature that uses `Target == 14` or `38`), it would need to come from this conversion — see the "future work" note in `EQLogParser/CLAUDE.md`.

## Files

- `convert_spells.py` — the converter
- `analyze_diff.py` — verification helper, run after conversion
- `spells.previous.txt` — temporary backup, ignore this in git (delete after verifying)
