using System;
using System.Collections.Generic;

namespace EQLogParser;

internal enum SpellResist
{
  Undefined = -2, Reflected = -1, Unresistable = 0, Magic, Fire, Cold, Poison, Disease, Lowest, Average, Physical, Corruption
}

internal class SpellData
{
  public string Id { get; set; }
  public string Name { get; set; }
  public string NameAbbrv { get; set; }
  public ushort Duration { get; set; }
  public ushort MaxHits { get; set; }
  public bool IsBeneficial { get; set; }
  public SpellResist Resist { get; set; }
  public short Damaging { get; set; }
  public byte Target { get; set; }
  public ushort ClassMask { get; set; }
  public byte Level { get; set; }
  public byte[] ClassLevels { get; set; }
  public bool HasAmbiguity { get; set; }
  public string LandsOnYou { get; set; }
  public string LandsOnOther { get; set; }
  public bool SongWindow { get; set; }
  public string WearOff { get; set; }
  public byte Proc { get; set; }
  public byte Adps { get; set; }
  public byte Rank { get; set; }
  public bool Mgb { get; set; }
  public bool SeenRecently { get; set; }
  public bool IsUnknown { get; set; }
  // CastingTimeMs / RecastTimeMs are sourced from spells_us.txt cols 13 and 15
  // via the parser-format extension at cols 20 and 21. CastingTimeMs == 0 means
  // instant-cast (no cast bar). See tools/spells/convert_spells.py and
  // memory project_dd_attribution for the planned consumer (cast-window
  // correlation to attribute "non-melee" damage to its caster's spell).
  public uint CastingTimeMs { get; set; }
  public uint RecastTimeMs { get; set; }
  public string Category { get; set; }
  // Skill / RecourseID / TimerID are sourced from spells_us.txt cols 100/150/167
  // via the parser-format extension at cols 23/24/25 (see convert_spells.py).
  // Skill is the raw SoD SpellSkill enum value (-1 = none) — lets consumers
  // distinguish disciplines/abilities from spells without name heuristics.
  // RecourseID is the spell that procs when this one lands (0 = none) — the
  // basis for future proc attribution. TimerID is the shared AA/discipline
  // cooldown-group id (0 = none). All default to 0 on pre-extension spells.txt.
  public int Skill { get; set; }
  public int RecourseID { get; set; }
  public int TimerID { get; set; }
  // Mana/Range/ResistMod sourced from spells_us.txt cols 19/9/147 via parser
  // cols 26/27/28. All default to 0 on pre-extension spells.txt files.
  public int Mana { get; set; }
  public int Range { get; set; }
  public int ResistMod { get; set; }
  // IconId sourced from spells_us.txt col 144 via parser col 29. Maps to
  // data/icons/gemicon{N}.jpg. 0 means no icon. Defaults to 0 on older spells.txt.
  public int IconId { get; set; }
  // Human-readable spell description from dbstr_us.txt type-6 entries, loaded at
  // startup from data/spell-descriptions.json (emitted by convert_spells.py --dbstr).
  // Empty string when no description is available (e.g. AAs, unnamed spells).
  public string Description { get; set; } = string.Empty;
}

internal class SpellCountData
{
  public Dictionary<string, Dictionary<string, uint>> PlayerCastCounts { get; set; } = [];
  public Dictionary<string, Dictionary<string, uint>> PlayerInterruptedCounts { get; set; } = [];
  public Dictionary<string, Dictionary<string, uint>> PlayerReceivedCounts { get; set; } = [];
  public Dictionary<string, uint> MaxCastCounts { get; set; } = [];
  public Dictionary<string, uint> MaxReceivedCounts { get; set; } = [];
  public Dictionary<string, SpellData> UniqueSpells { get; set; } = [];
  public Dictionary<string, bool> UniquePlayers { get; set; } = [];
}

internal class SpellTreeNode
{
  public List<SpellData> SpellData { get; set; } = [];
  public Dictionary<string, SpellTreeNode> Words { get; set; } = [];
}

internal class SpellTreeResult
{
  public List<SpellData> SpellData { get; set; }
  public int DataIndex { get; set; }
}
