#!/usr/bin/env python3
"""Validate the category-based ADPS mapping against upstream ground truth.

Compares the category-derived Adps (from convert_spells.py's CATEGORY_TO_ADPS
table + Dalaya source cols 156/157/158) against the upstream-curated overlay
in upstream-adps.json. Reports:

  - exact: category bits == upstream bits
  - subset: category bits <= upstream bits (we under-classify; safe)
  - superset: category bits > upstream bits (we add extras; possibly correct,
              possibly false positives -- sample lists shown)
  - conflict: bits diverge (some in upstream, others in category, neither
              a subset of the other)
  - missed: category produced 0 for a spell upstream classified

Use the conflict/missed/superset lists to tune CATEGORY_TO_ADPS, then re-run.

Then runs an optional SPA cross-validation (--spa-check) that confirms whether
the actual SPA effect IDs in each spell's 12 slots back up its ADPS
classification.  Two sub-reports:

  - unverified: spell has ADPS bit(s) but none of its slots contain a SPA
                that is mechanically expected for that bit (possible mismap)
  - unclassified: spell has a mechanically-expected SPA but carries no ADPS
                  bit (possible false-negative; higher noise than unverified)

Usage:
    python validate_adps.py [--spa-check]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SPELLS_US = Path(r"F:/Dalaya/spells_us.txt")
CATEGORIES_JSON = ROOT / "dalaya-categories.json"
UPSTREAM_JSON = ROOT / "upstream-adps.json"

# Import the curated mapping from the converter so we validate the same table
# that production uses.
sys.path.insert(0, str(ROOT))
from convert_spells import (  # noqa: E402
    CATEGORY_TO_ADPS,
    CASTER_ADPS, MELEE_ADPS, TANK_ADPS, HEALING_ADPS,
    SLOT_SPA_COL,
)

# SPA effect IDs occupy cols 86-97 (12 slots). Confirmed against
# ngdeao/SoD-winspellparser SpellParser.cs LoadSpell lines 2814-2826.
SLOT_COUNT = 12

# Beneficial flag column (0=detrimental, 1/2/3=beneficial).
BENEFICIAL_COL = 83

# Which SPAs are mechanically expected for each ADPS bit.
# A spell is "verified" for a bit if at least one of its 12 slots has a SPA
# in the corresponding set. Not exhaustive — covers the main mechanics only.
# SPA reference: ngdeao/SoD-winspellparser ParseEffect cases.
SPA_FOR_ADPS: dict[int, set[int]] = {
    MELEE_ADPS:   {2,   # ATK
                   11,  # Combat Haste
                   },
    TANK_ADPS:    {1,   # AC
                   55,  # Damage Absorb (Rune)
                   69,  # Max HP
                   },
    CASTER_ADPS:  {14,  # Mana Regen
                   18,  # INT
                   19,  # WIS
                   36,  # Mana Pool
                   },
    HEALING_ADPS: {0,   # Current HP (heals/regen — filtered to beneficial below)
                   100, # Current HP Repeating (HoT)
                   },
}


def bits(mask: int) -> str:
    names = []
    if mask & 1: names.append("Caster")
    if mask & 2: names.append("Melee")
    if mask & 4: names.append("Tank")
    if mask & 8: names.append("Healing")
    return "+".join(names) or "--"


def adps_from_categories(fields: list[str], categories: dict[str, str]) -> int:
    mask = 0
    for col in (156, 157, 158):
        if col >= len(fields):
            break
        cat_id = fields[col].strip()
        if not cat_id or cat_id == "0":
            continue
        label = categories.get(cat_id)
        if label is None:
            continue
        mask |= CATEGORY_TO_ADPS.get(label, 0)
    return mask


def _spell_spas(fields: list[str]) -> set[int]:
    """Return the set of SPA IDs present in this spell's 12 effect slots."""
    result = set()
    for i in range(SLOT_COUNT):
        col = SLOT_SPA_COL + i
        if col >= len(fields):
            break
        raw = fields[col].strip()
        if raw and raw not in ("", "254"):  # 254 = unused marker
            try:
                result.add(int(raw))
            except ValueError:
                pass
    return result


def _is_beneficial(fields: list[str]) -> bool:
    if BENEFICIAL_COL >= len(fields):
        return False
    raw = fields[BENEFICIAL_COL].strip()
    try:
        return int(raw) > 0
    except ValueError:
        return False


def spa_cross_validate(spells: dict[str, list[str]], categories: dict[str, str], limit: int = 20) -> None:
    """Cross-check ADPS classification against actual SPA effect IDs.

    Mode 1 (unverified): spell carries an ADPS bit but has no SPA in the
    expected set for that bit — likely a category-table mismap or a spell that
    achieves the effect indirectly (e.g. procs on hit).

    Mode 2 (unclassified): spell has an expected SPA but no ADPS bit — likely
    missed by both the category overlay and upstream-adps.json. Higher noise.
    SPA 0 is filtered to beneficial-only for Healing to avoid flagging nukes.
    """
    print("\n" + "=" * 70)
    print("SPA cross-validation")
    print("=" * 70)

    unverified: list[tuple[str, int, set[int]]] = []   # (name, adps_mask, spas)
    unclassified: list[tuple[str, int, set[int]]] = [] # (name, implied_bit, spas)

    for name, fields in spells.items():
        cat_mask = adps_from_categories(fields, categories)
        spa_ids = _spell_spas(fields)
        beneficial = _is_beneficial(fields)

        # Mode 1: for each ADPS bit set, check if at least one expected SPA is present.
        for adps_bit, expected_spas in SPA_FOR_ADPS.items():
            if not (cat_mask & adps_bit):
                continue
            # For Healing, SPA 0 only counts when beneficial.
            effective_spas = expected_spas
            if adps_bit == HEALING_ADPS and not beneficial:
                effective_spas = expected_spas - {0}
            if not (spa_ids & effective_spas):
                unverified.append((name, adps_bit, spa_ids))

        # Mode 2: for each SPA in the spell, check if the ADPS bit is set.
        for adps_bit, expected_spas in SPA_FOR_ADPS.items():
            if cat_mask & adps_bit:
                continue  # already classified
            hits = spa_ids & expected_spas
            if not hits:
                continue
            # For Healing, SPA 0 only counts when beneficial.
            if adps_bit == HEALING_ADPS and 0 in hits and not beneficial:
                hits = hits - {0}
            if hits:
                unclassified.append((name, adps_bit, hits))

    # Mode 1 report
    print(f"\nMode 1 — unverified ({len(unverified)} spells with ADPS bit but no expected SPA):")
    if not unverified:
        print("  (none — all classified spells have a matching SPA)")
    else:
        print(f"  Showing first {min(limit, len(unverified))}. "
              "These may be indirect-effect spells, procs, or category mismaps.")
        for name, adps_bit, spa_ids in unverified[:limit]:
            spa_summary = ",".join(str(s) for s in sorted(spa_ids)) or "(none)"
            print(f"    {name!r:48} bit={bits(adps_bit):>8}  spas=[{spa_summary}]")

    # Mode 2 report
    print(f"\nMode 2 — unclassified ({len(unclassified)} spells with expected SPA but no ADPS bit):")
    if not unclassified:
        print("  (none)")
    else:
        print(f"  Showing first {min(limit, len(unclassified))}. "
              "Higher noise — many SPAs appear incidentally. Use to spot systematic gaps.")
        for name, adps_bit, hit_spas in unclassified[:limit]:
            spa_summary = ",".join(str(s) for s in sorted(hit_spas))
            print(f"    {name!r:48} implied={bits(adps_bit):>8}  matching_spas=[{spa_summary}]")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--spa-check", action="store_true",
                        help="Also run SPA cross-validation (checks whether ADPS classifications "
                             "are backed by actual SPA effect IDs in each spell's slots).")
    args = parser.parse_args()

    categories = json.loads(CATEGORIES_JSON.read_text(encoding="utf-8"))
    upstream = {k: int(v) for k, v in json.loads(UPSTREAM_JSON.read_text(encoding="utf-8")).items()}

    spells: dict[str, list[str]] = {}
    with SPELLS_US.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line:
                continue
            fields = line.split("^")
            if len(fields) < 200:
                continue
            spells[fields[1]] = fields

    exact = 0
    subset = []
    superset = []
    conflict = []
    missed = []

    for name, up_mask in upstream.items():
        if name not in spells:
            continue
        cat_mask = adps_from_categories(spells[name], categories)
        if cat_mask == up_mask:
            exact += 1
        elif cat_mask == 0:
            missed.append((name, up_mask))
        elif (cat_mask & up_mask) == cat_mask:
            subset.append((name, up_mask, cat_mask))
        elif (cat_mask & up_mask) == up_mask:
            superset.append((name, up_mask, cat_mask))
        else:
            conflict.append((name, up_mask, cat_mask))

    total = exact + len(subset) + len(superset) + len(conflict) + len(missed)
    print(f"Validating against {total} upstream-classified spells found in Dalaya source:\n")
    print(f"  exact:    {exact:>4} ({100*exact/total:.1f}%)  -- category bits == upstream bits")
    print(f"  subset:   {len(subset):>4} ({100*len(subset)/total:.1f}%)  -- category <= upstream (safe under-classify)")
    print(f"  superset: {len(superset):>4} ({100*len(superset)/total:.1f}%)  -- category >= upstream (added bits -- review)")
    print(f"  conflict: {len(conflict):>4} ({100*len(conflict)/total:.1f}%)  -- bits diverge (tune mapping)")
    print(f"  missed:   {len(missed):>4} ({100*len(missed)/total:.1f}%)  -- category=0 (categories absent / unmapped)")

    def show(label: str, rows: list, limit: int = 15):
        if not rows: return
        print(f"\n  --- {label} (first {min(limit, len(rows))}) ---")
        for r in rows[:limit]:
            if len(r) == 2:
                name, up = r
                print(f"    {name!r:48} upstream={bits(up):>20} ({up})")
            else:
                name, up, cat = r
                print(f"    {name!r:48} upstream={bits(up):>20} ({up}) cat={bits(cat):>20} ({cat})")

    show("conflict", conflict)
    show("superset", superset)
    show("missed", missed)

    if args.spa_check:
        spa_cross_validate(spells, categories)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
