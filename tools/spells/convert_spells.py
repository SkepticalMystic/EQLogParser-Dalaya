#!/usr/bin/env python3
"""Convert Dalaya's spells_us.txt (game format) to EQLogParser's spells.txt (parser format).

Run after each Dalaya patch that updates spells_us.txt.

Usage:
    python convert_spells.py [SOURCE] [DEST]

Defaults:
    SOURCE = F:/Dalaya/spells_us.txt
    DEST   = ../../EQLogParser/data/spells.txt  (relative to this script)

Mapping (see CLAUDE.md and EQDataStore.ParseCustomSpellData):
    parser col  source col   meaning
    0           0            spell id
    1           1            spell name
    2           104..119     level    = min(non-255) across the 16 class-level cols
    3           17           duration (6-second ticks; EQDataStore multiplies by 6 for seconds)
    4           83           beneficial flag (0=detrimental, 1=beneficial, 2/3=beneficial-variants)
    6           98           target type     (Pet=14 enables auto pet detection)
    7           104..119     class mask = bitmask, bit(classid-1) set when col(103+classid) != 255
    8           -            damaging flag   (1 if any of src[6..8] non-empty)
    10          85           resist type (0=unr 1=mag 2=fire 3=cold 4=poison 5=disease 6=chrom 7=prism)
    11          154          song window flag (1 = occupies song-window slot in UI)
    12          overlay      Adps bitmask from tools/spells/upstream-adps.json (name match)
                             1=Caster 2=Melee 4=Tank 8=Healing — drives the Timeline UI
                             (Timeline.xaml.cs ClassFilter); 0 when name has no upstream
                             classification. See extract_upstream_adps.py.
    15, 16      -            ambiguity flags (hard-coded "1", "1")
    17          6            lands-on-you message
    18          7            lands-on-other message
    19          8            wear-off message
    all others  -            hard-coded "0"

Source cols 104..119 hold per-class level requirements in EQ classid order:
  104 War, 105 Clr, 106 Pal, 107 Rng, 108 Shd, 109 Dru, 110 Mnk, 111 Brd,
  112 Rog, 113 Shm, 114 Nec, 115 Wiz, 116 Mag, 117 Enc, 118 Bst, 119 Ber.
A value of 255 means the class cannot learn the spell. Verified against
Minor/Light/Greater Healing (Cleric+Pal+Ranger+Druid+Shaman+Beastlord rows)
plus class-unique spells (Wolf Form, Camouflage, Frost Bolt, Cardiac Arrest,
Disintegrate, Chorus of Althuna, Inner Fire). Wiki cross-reference:
https://wiki.shardsofdalaya.com/wiki/<Class>_spells.

Still hard-coded "0":
  MaxHits (col 5), CombatSkill (9), Mgb (13), Rank (14).
MaxHits is not exposed by Dalaya's source data (Dalaya's absorb buffs use
SPA 55 damage-amount absorbs, not the live-EQ MaxHits hit-counter mechanic).
Mgb is the Mass-Group-Buff-eligible flag from PoP-era live EQ — confirmed via
Dalaya forums that Soulbond replaced MGB on Dalaya, so the flag is unused.
Rank is parsed into SpellData.Rank but never read anywhere in the codebase.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

DEFAULT_SOURCE = Path(r"F:/Dalaya/spells_us.txt")
DEFAULT_DEST = Path(__file__).resolve().parent.parent.parent / "EQLogParser" / "data" / "spells.txt"
DEFAULT_ADPS_OVERLAY = Path(__file__).resolve().parent / "upstream-adps.json"
DEFAULT_CATEGORIES = Path(__file__).resolve().parent / "dalaya-categories.json"


CASTER_ADPS = 1
MELEE_ADPS = 2
TANK_ADPS = 4
HEALING_ADPS = 8

# Hand-curated mapping from Dalaya's spell-category labels (dbstr_us.txt type 5)
# to the ADPS bitmask consumed by Timeline.xaml.cs. Categories not listed here
# contribute 0 (no bit set) — when a spell has multiple categories, bitmasks
# are unioned, so unclassified categories don't drown out specific ones.
#
# Principle: only classify specific sub-categories with high confidence. Broad
# parent categories like "Statistic Buffs" stay at 0 so they don't pollute
# pure-Caster INT/WIS buffs with a stray Melee bit. The 3-slot union lets the
# specific sub-category (e.g. "Wisdom/Intelligence") set the right bit.
CATEGORY_TO_ADPS: dict[str, int] = {
    # --- Tank: damage mitigation, HP, AC, defensives ---
    "HP Buffs": TANK_ADPS,
    "HP type one": TANK_ADPS,
    "HP type two": TANK_ADPS,
    "Health": TANK_ADPS,
    "Symbol": TANK_ADPS,
    "Aegolism": TANK_ADPS,
    "Shielding": TANK_ADPS,
    "Damage Shield": TANK_ADPS,
    "Rune": TANK_ADPS,
    "Spellshield": TANK_ADPS,
    "Invulnerability": TANK_ADPS,
    "Armor Class": TANK_ADPS,
    "Stamina": TANK_ADPS,
    "Agility": TANK_ADPS,
    "Resist Buff": TANK_ADPS,
    "Spell Guard": TANK_ADPS,
    "Melee Guard": TANK_ADPS,
    "Reflection": TANK_ADPS,
    "Block": TANK_ADPS,
    "Defensive": TANK_ADPS,
    "Reactions": TANK_ADPS,
    "Fumble": TANK_ADPS,

    # Health/Mana = combo HP + mana regen. Bit set for both buckets.
    "Health/Mana": TANK_ADPS | CASTER_ADPS,

    # --- Melee: ATK, haste, physical stats, melee disciplines ---
    "Haste": MELEE_ADPS,
    "Strength": MELEE_ADPS,
    "Dexterity": MELEE_ADPS,
    "Attack": MELEE_ADPS,
    "Ranged Physical": MELEE_ADPS,
    "Melee Physical": MELEE_ADPS,
    "Combat Innates": MELEE_ADPS,
    "Combat Abilities": MELEE_ADPS,
    "Disciplines": MELEE_ADPS,
    "Techniques": MELEE_ADPS,
    "Offensive": MELEE_ADPS,

    # --- Caster: mana, spell-haste/crit, INT/WIS, pet DPS buffs ---
    "Wisdom/Intelligence": CASTER_ADPS,
    "Mana": CASTER_ADPS,         # both 39137 (stat sub) and 39145 alias
    "Spell Focus": CASTER_ADPS,
    "Conversions": CASTER_ADPS,  # mana conversions
    "Fizzle Rate": CASTER_ADPS,
    "Mana Flow": CASTER_ADPS,
    "Pet Haste": CASTER_ADPS,        # pet DPS uplift attributed to caster
    "Pet Misc Buffs": CASTER_ADPS,   # pet utility (familiar regen, etc.) attributed to caster

    # --- Healing: direct heals, HoTs, regen, cures ---
    "Heals": HEALING_ADPS,         # both 39002 (broad) and 39063 alias
    "Duration Heals": HEALING_ADPS,
    "Quick Heal": HEALING_ADPS,
    "Cure": HEALING_ADPS,
    "Regen": HEALING_ADPS,

    # Everything not listed contributes 0 — see the module-level comment.
}


CLASS_LEVEL_FIRST_COL = 104  # source col for classid=1 (Warrior); +15 reaches Berserker.


def _level_and_class_mask(src_fields: list[str]) -> tuple[str, str]:
    """Return (level, class_mask) derived from the 16 class-level columns.

    Level = lowest non-255 class level (or "255" if no class can cast).
    ClassMask = bitmask where bit(classid-1) is set when col(103+classid) != 255.
    """
    min_level = 256
    mask = 0
    for classid in range(1, 17):
        col = CLASS_LEVEL_FIRST_COL + (classid - 1)
        if col >= len(src_fields):
            continue
        raw = src_fields[col].strip()
        if not raw:
            continue
        try:
            lvl = int(raw)
        except ValueError:
            continue
        if lvl == 255:
            continue
        mask |= 1 << (classid - 1)
        if lvl < min_level:
            min_level = lvl
    if min_level == 256:
        return "255", "0"
    # Parser parses Level as byte (0-255). Source values up to 254 are valid;
    # 255 already excluded above.
    return str(min(min_level, 255)), str(mask)


def _adps_from_categories(src_fields: list[str], categories: dict[str, str]) -> int:
    """Union ADPS bitmasks from source cols 156/157/158 (CategoryDescID[0..2]).

    Each slot holds a numeric id into dbstr_us.txt type-5 entries; categories
    maps id → text label; CATEGORY_TO_ADPS maps label → bitmask.
    """
    mask = 0
    for col in (156, 157, 158):
        if col >= len(src_fields):
            break
        cat_id = src_fields[col].strip()
        if not cat_id or cat_id == "0":
            continue
        label = categories.get(cat_id)
        if label is None:
            continue
        mask |= CATEGORY_TO_ADPS.get(label, 0)
    return mask


def convert_line(src_fields: list[str], adps_overlay: dict[str, int], categories: dict[str, str]) -> str:
    spell_id = src_fields[0]
    name = src_fields[1]
    lands_on_you = src_fields[6]
    lands_on_other = src_fields[7]
    wear_off = src_fields[8]
    duration = src_fields[17] if len(src_fields) > 17 else "0"
    beneficial = src_fields[83] if len(src_fields) > 83 else "0"
    resist = src_fields[85] if len(src_fields) > 85 else "0"
    target = src_fields[98] if len(src_fields) > 98 else "5"
    song_window = src_fields[154] if len(src_fields) > 154 else "0"
    level, class_mask = _level_and_class_mask(src_fields)
    damaging = "1" if (lands_on_you or lands_on_other or wear_off) else "0"
    # ADPS = union of category-derived bitmask and the upstream-overlay bitmask.
    # Upstream is hand-curated by humans, categories are sourced from Dalaya
    # itself; combining them maximizes coverage without losing precision.
    adps_value = _adps_from_categories(src_fields, categories) | adps_overlay.get(name, 0)
    adps = str(adps_value)

    return "^".join([
        spell_id,         # 0  Id
        name,             # 1  Name
        level,            # 2  Level (min class level; 255 = no class can cast)
        duration,         # 3  Duration (ticks)
        beneficial,       # 4  Beneficial (0=det, non-zero=beneficial)
        "0",              # 5  MaxHits
        target,           # 6  Target
        class_mask,       # 7  ClassMask (bitmask, classid-1 per bit)
        damaging,         # 8  Damaging
        "0",              # 9  CombatSkill
        resist,           # 10 Resist (0=unresistable, 1=magic, 2=fire, 3=cold, ...)
        song_window,      # 11 SongWindow (1 = occupies UI song-window slot)
        adps,             # 12 Adps bitmask (1=Caster 2=Melee 4=Tank 8=Healing)
        "0",              # 13 Mgb
        "0",              # 14 Rank
        "1",              # 15 HasAmbiguity (a)
        "1",              # 16 HasAmbiguity (b)
        lands_on_you,     # 17 LandsOnYou
        lands_on_other,   # 18 LandsOnOther
        wear_off,         # 19 WearOff
    ])


def load_adps_overlay(path: Path) -> dict[str, int]:
    if not path.is_file():
        return {}
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def load_categories(path: Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def convert(source: Path, dest: Path, adps_overlay: dict[str, int], categories: dict[str, str]) -> tuple[int, int, int]:
    written = 0
    skipped = 0
    stamped = 0
    out_lines: list[str] = []
    with source.open("r", encoding="utf-8", newline="") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            if not line:
                skipped += 1
                continue
            fields = line.split("^")
            if len(fields) < 9:
                skipped += 1
                continue
            converted = convert_line(fields, adps_overlay, categories)
            # Count rows with non-zero Adps (col 12). Cheap field grab since
            # we just built the line.
            if converted.split("^")[12] != "0":
                stamped += 1
            out_lines.append(converted)
            written += 1

    dest.parent.mkdir(parents=True, exist_ok=True)
    # Match the existing file's encoding/line endings: ASCII, LF, trailing newline.
    with dest.open("w", encoding="utf-8", newline="\n") as f:
        for line in out_lines:
            f.write(line + "\n")

    return written, skipped, stamped


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source", nargs="?", default=str(DEFAULT_SOURCE), help=f"path to spells_us.txt (default: {DEFAULT_SOURCE})")
    parser.add_argument("dest", nargs="?", default=str(DEFAULT_DEST), help=f"path to spells.txt output (default: {DEFAULT_DEST})")
    parser.add_argument("--adps-overlay", default=str(DEFAULT_ADPS_OVERLAY), help=f"path to upstream-adps.json (default: {DEFAULT_ADPS_OVERLAY})")
    parser.add_argument("--categories", default=str(DEFAULT_CATEGORIES), help=f"path to dalaya-categories.json (default: {DEFAULT_CATEGORIES})")
    args = parser.parse_args()

    source = Path(args.source)
    dest = Path(args.dest)
    overlay_path = Path(args.adps_overlay)
    categories_path = Path(args.categories)

    if not source.is_file():
        print(f"error: source not found: {source}", file=sys.stderr)
        return 1

    adps_overlay = load_adps_overlay(overlay_path)
    if adps_overlay:
        print(f"loaded {len(adps_overlay)} upstream Adps classifications from {overlay_path}")
    else:
        print(f"warning: no upstream Adps overlay at {overlay_path}", file=sys.stderr)

    categories = load_categories(categories_path)
    if categories:
        print(f"loaded {len(categories)} category labels from {categories_path}")
    else:
        print(f"warning: no category map at {categories_path} — Adps will rely on upstream overlay only", file=sys.stderr)

    written, skipped, stamped = convert(source, dest, adps_overlay, categories)
    print(f"wrote {written} spells to {dest} ({stamped} with non-zero Adps)")
    if skipped:
        print(f"skipped {skipped} blank/short lines")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
