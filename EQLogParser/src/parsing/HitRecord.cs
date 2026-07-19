using System;
using System.Diagnostics.CodeAnalysis;

namespace EQLogParser;

public class HitRecord : IAction
{
  public uint Total { get; set; }
  public uint OverTotal { get; set; }
  public string Type { get; set; }
  public string SubType { get; set; }
  public short ModifiersMask { get; set; }
}

internal class HealRecord : HitRecord
{
  public string Healer { get; set; }
  public string Healed { get; set; }
}

[SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
internal class DamageRecord : HitRecord
{
  public string Attacker { get; set; }
  public string AttackerOwner { get; set; }
  public string Defender { get; set; }
  public string DefenderOwner { get; set; }
  public bool AttackerIsSpell { get; set; }

  public override bool Equals(object obj)
  {
    if (obj is not DamageRecord other) return false;
    if (Attacker != other.Attacker || AttackerOwner != other.AttackerOwner || Defender != other.Defender ||
      DefenderOwner != other.DefenderOwner || AttackerIsSpell != other.AttackerIsSpell || Total != other.Total ||
      OverTotal != other.OverTotal || Type != other.Type || ModifiersMask != other.ModifiersMask)
    {
      return false;
    }
    // For DD records, treat "Unknown" SubType as a wildcard so a named record from one log
    // source deduplicates against an unattributed record from another source. Without this,
    // multi-log merges double-count any DD that only one source can attribute to a spell.
    if (Type == Labels.Dd && (SubType == Labels.Unk || other.SubType == Labels.Unk))
    {
      return true;
    }
    return SubType == other.SubType;
  }

  public override int GetHashCode()
  {
    var hash1 = HashCode.Combine(Attacker, AttackerOwner, Defender, DefenderOwner, AttackerIsSpell, Total);
    // For DD records, normalize SubType to Labels.Dd in the hash so that named and Unknown
    // DD records from different sources fall into the same hash bucket. Equals then decides
    // whether they are truly equal (both named with the same spell, or one is Unknown).
    var subTypeForHash = (Type == Labels.Dd) ? Labels.Dd : SubType;
    var hash2 = HashCode.Combine(OverTotal, Type, subTypeForHash, ModifiersMask);
    return HashCode.Combine(hash1, hash2);
  }
}
