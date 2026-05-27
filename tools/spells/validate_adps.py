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

Usage:
    python validate_adps.py
"""

from __future__ import annotations

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
from convert_spells import CATEGORY_TO_ADPS  # noqa: E402


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


def main() -> int:
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
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
