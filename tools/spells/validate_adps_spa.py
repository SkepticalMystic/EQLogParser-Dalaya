#!/usr/bin/env python3
"""Cross-check ADPS classifications against actual SPA effect IDs (spells_us.txt cols 86-97).

Companion to validate_adps.py. Where that script validates the category-derived
ADPS against the upstream-curated overlay (label-vs-label), this one validates
the *final* ADPS bitmask each spell receives (category ∪ upstream, exactly as
convert_spells emits it) against the spell's real SPA effect slots. The premise:
a spell tagged Melee should actually carry a melee SPA (ATK/Haste/STR/DEX); one
tagged Tank should carry a defensive SPA (AC/HP/resist/rune); etc. Mismatches in
either direction are tuning candidates.

Two directions, both restricted to BENEFICIAL spells (buffs) for high signal:

  A. tagged-but-uncorroborated — the ADPS bit is set, but none of the spell's
     captured SPAs corroborate it. Candidate false positive (wrong tag, or the
     justification is a mechanism we don't capture — see the healing caveat).

  B. corroborated-but-untagged — the spell carries a strong category SPA but the
     matching ADPS bit is NOT set. Candidate false negative (missed classification).

Healing caveat: direct heals use SPA 0 (Hitpoints), which convert_spells treats
as an empty-slot marker (EMPTY_SLOT_SPAS) and drops. So a direct heal has no
captured slots at all, and Healing cannot be corroborated by SPA the way the
other three buckets can. This tool therefore:
  - excludes the Healing bit from direction A entirely (would flag every heal), and
  - in direction B only detects HoTs (SPA 100), the one healing effect that is
    captured. Verified: Minor/Light/Greater/Superior Healing, Word of Health,
    Regeneration, and Chloroplast all extract zero slots; Celestial Healing
    (a HoT) extracts SPA 100.

Scope notes / future work:
  - Detrimental ADPS (slows = Tank, resist debuffs = Caster, DoTs) is a separate
    model and is intentionally out of scope here; only beneficial buffs are checked.
  - SPA corroboration sets below are deliberately conservative and high-confidence,
    grounded by sampling Dalaya's own spells_us.txt (see the inline evidence on
    each entry), not by a generic live-EQ SPA table.

Usage:
    python validate_adps_spa.py [--limit N]
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SPELLS_US = Path(r"F:/Dalaya/spells_us.txt")
CATEGORIES_JSON = ROOT / "dalaya-categories.json"
UPSTREAM_JSON = ROOT / "upstream-adps.json"

# Reuse the production converter so we validate exactly the bitmask it emits, and
# the same slot layout / empty-slot handling.
sys.path.insert(0, str(ROOT))
from convert_spells import (  # noqa: E402
    CASTER_ADPS, MELEE_ADPS, TANK_ADPS, HEALING_ADPS,
    SLOT_SPA_COL, SLOT_BASE1_COL, SLOT_MAX_COL, EMPTY_SLOT_SPAS,
    _adps_from_categories, _safe_field_int,
)

BIT_NAME = {CASTER_ADPS: "Caster", MELEE_ADPS: "Melee", TANK_ADPS: "Tank", HEALING_ADPS: "Healing"}

# SPA ids that corroborate each ADPS bit on a BENEFICIAL spell. Each id was
# confirmed by sampling spells_us.txt rather than assumed from a generic SPA list.
CORROBORATING_SPAS: dict[int, set[int]] = {
    # ATK(2): Firefist/Grim Aura. STR(4): Strengthen. DEX(5): Dexterity. Haste(11):
    # Quickness/Yaulp. MeleeProc(85): Quivers/Vampiric Embrace/Poisoned Arrows.
    # Martial(119): Battlecry/Warsong of the Tribes, Frenzy of Spirit. Accuracy(184)
    # and SkillDmgMod(185): the Quiver line + Yaulp V/VI.
    MELEE_ADPS: {2, 4, 5, 11, 85, 119, 184, 185},
    # AC(1): Inner Fire/Armor of Faith. AGI(6): Agility. STA(7): Talisman of the
    # Brute. MaxHP(69): Shielding line. DmgShield(59): Shield of Thistles. Rune(55):
    # Steelskin/Leatherskin. Resists(46-50): Resist Fire/Cold/Magic/Poison/Disease.
    # Invuln/absorb(40): Divine Aura/Harmshield. SpellShield(78): Words of Protection.
    TANK_ADPS: {1, 6, 7, 40, 46, 47, 48, 49, 50, 55, 59, 69, 78},
    # INT(8)/WIS(9): Brilliance/Insight/Potion of the Mind. Mana(15): Mana Sieve
    # (beneficial = mana buff). SpellHaste(127): Casting Speed Increment. (Spell-damage
    # focus SPAs are too ambiguous on Dalaya to include.)
    CASTER_ADPS: {8, 9, 15, 127},
    # HoT(100): Celestial Healing. Direct heals are SPA 0 and not captured — see
    # the module docstring's healing caveat. Used for direction B only.
    HEALING_ADPS: {100},
}

# SPA 10 appears on hundreds of spells purely as a base=0/max=0 structural
# placeholder (Armor of Faith, Burnout, Spirit of Cheetah...). Treat it as noise
# when deciding whether a spell has any "real" captured effect.
PLACEHOLDER_SPA = 10


def bits(mask: int) -> str:
    return "+".join(name for bit, name in
                    ((CASTER_ADPS, "Caster"), (MELEE_ADPS, "Melee"),
                     (TANK_ADPS, "Tank"), (HEALING_ADPS, "Healing"))
                    if mask & bit) or "--"


def meaningful_spas(fields: list[str]) -> set[int]:
    """Captured SPA ids for a spell, excluding empty-slot markers and the SPA-10
    base/max=0 placeholder."""
    out: set[int] = set()
    for slot in range(12):
        spa = _safe_field_int(fields, SLOT_SPA_COL + slot)
        if spa in EMPTY_SLOT_SPAS:
            continue
        if spa == PLACEHOLDER_SPA:
            base1 = _safe_field_int(fields, SLOT_BASE1_COL + slot)
            mx = _safe_field_int(fields, SLOT_MAX_COL + slot)
            if base1 == 0 and mx == 0:
                continue
        out.add(spa)
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--limit", type=int, default=20, help="max sample rows per bucket (default 20)")
    args = ap.parse_args()

    if not SPELLS_US.is_file():
        print(f"error: source not found: {SPELLS_US}", file=sys.stderr)
        return 1

    categories = json.loads(CATEGORIES_JSON.read_text(encoding="utf-8"))
    overlay = {k: int(v) for k, v in json.loads(UPSTREAM_JSON.read_text(encoding="utf-8")).items()}

    total = 0
    beneficial = 0
    tagged_per_bit: dict[int, int] = defaultdict(int)
    flagged_a: dict[int, list] = defaultdict(list)  # tagged but uncorroborated
    flagged_b: dict[int, list] = defaultdict(list)  # corroborated but untagged

    with SPELLS_US.open("r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line:
                continue
            fields = line.split("^")
            if len(fields) < 200:
                continue
            total += 1

            # Beneficial flag (col 83): 0 = detrimental. Only buffs are in scope.
            if _safe_field_int(fields, 83) == 0:
                continue
            beneficial += 1

            name = fields[1]
            adps = _adps_from_categories(fields, categories) | overlay.get(name, 0)
            if adps == 0:
                # No classification at all -> direction B can still suggest one.
                pass

            spas = meaningful_spas(fields)

            for bit, corro in CORROBORATING_SPAS.items():
                has_corro = bool(spas & corro)
                tagged = bool(adps & bit)
                if tagged:
                    tagged_per_bit[bit] += 1

                # Direction A: tagged but uncorroborated. Skip Healing (SPA-0 heals
                # legitimately have no captured slot). Also skip spells with no
                # captured slots at all — their whole effect is the invisible SPA-0,
                # so absence of a corroborating SPA proves nothing (regens, direct
                # heals, pure HP buffs). We can only refute a tag when we can SEE the
                # spell's effects and none of them match.
                if bit != HEALING_ADPS and tagged and spas and not has_corro:
                    flagged_a[bit].append((name, adps, sorted(spas)))

                # Direction B: corroborated but untagged.
                if has_corro and not tagged:
                    flagged_b[bit].append((name, adps, sorted(spas & corro)))

    print(f"Scanned {total} spells; {beneficial} beneficial (buffs) in scope.\n")
    print("Tagged (beneficial only) per ADPS bit:")
    for bit in (MELEE_ADPS, TANK_ADPS, CASTER_ADPS, HEALING_ADPS):
        print(f"  {BIT_NAME[bit]:>8}: {tagged_per_bit[bit]}")

    def show(title: str, buckets: dict[int, list], spa_label: str):
        print(f"\n=== {title} ===")
        for bit in (MELEE_ADPS, TANK_ADPS, CASTER_ADPS, HEALING_ADPS):
            rows = buckets.get(bit)
            if not rows:
                continue
            print(f"\n  [{BIT_NAME[bit]}] {len(rows)} spell(s):")
            for name, adps, shown in rows[:args.limit]:
                print(f"    {name!r:46} adps={bits(adps):>22} {spa_label}={shown}")
            if len(rows) > args.limit:
                print(f"    ... and {len(rows) - args.limit} more")

    show("Direction A -- tagged but NO corroborating SPA (possible false positives)",
         flagged_a, "spas")
    show("Direction B -- corroborating SPA present but bit NOT set (possible misses)",
         flagged_b, "match")

    print("\nHealing note: direct heals use SPA 0 (dropped as an empty-slot marker), "
          "so Healing is excluded from direction A and only HoTs (SPA 100) are detectable in B.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
