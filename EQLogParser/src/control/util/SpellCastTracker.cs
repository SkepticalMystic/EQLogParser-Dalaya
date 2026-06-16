using System.Collections.Generic;

namespace EQLogParser
{
  // Per-attacker cast queue used to attribute Dalaya non-melee DD hits to the spell that caused them.
  //
  // Dalaya DD log lines ("X hit Y for N points of non-melee damage.") carry no spell name.
  // This tracker records damaging spell casts as they are observed by CastLineParser and
  // lets DamageLineParser peek the front of the queue when a DD hit arrives. If the hit
  // timestamp falls within the cast's expected hit window, the spell name is stamped onto
  // DamageRecord.SubType; otherwise SubType stays Labels.Unk ("Unknown").
  //
  // One instance per ParseContext: live contexts share SpellCastTracker.Instance;
  // isolated (Raid Damage) contexts receive a fresh instance via ParseContext.CreateIsolated.
  internal class SpellCastTracker
  {
    // Travel fudge: accounts for log-timestamp granularity (±1s) and spell flight time.
    private const double FudgeSeconds = 2.0;

    internal static SpellCastTracker Instance { get; } = new();

    private readonly Dictionary<string, Queue<PendingCast>> _queues =
      new(System.StringComparer.OrdinalIgnoreCase);

    // Called by CastLineParser when a damaging spell cast begins.
    internal void Push(string attacker, string spellName, uint castingTimeMs, double castStartTime)
    {
      if (!_queues.TryGetValue(attacker, out var queue))
        _queues[attacker] = queue = new Queue<PendingCast>();
      queue.Enqueue(new PendingCast
      {
        SpellName = spellName,
        CastingTimeSeconds = castingTimeMs / 1000.0,
        CastStartTime = castStartTime
      });
    }

    // Called by CastLineParser when a spell cast is interrupted. Marks the first
    // matching unfinished entry so it is skipped on the next TryAttributeHit call.
    internal void Cancel(string attacker, string spellName)
    {
      if (!_queues.TryGetValue(attacker, out var queue)) return;
      foreach (var cast in queue)
      {
        if (!cast.Cancelled && cast.SpellName == spellName)
        {
          cast.Cancelled = true;
          break;
        }
      }
    }

    // Called by DamageLineParser when a non-melee DD hit arrives. Returns the spell
    // name from the front of the attacker's queue if the hit falls within that spell's
    // expected hit window, or null if no match is found.
    internal string TryAttributeHit(string attacker, double hitTime)
    {
      if (!_queues.TryGetValue(attacker, out var queue)) return null;
      while (queue.Count > 0)
      {
        var cast = queue.Peek();
        var hitDeadline = cast.CastStartTime + cast.CastingTimeSeconds + FudgeSeconds;

        // Prune stale entries (their hit window has closed).
        if (hitTime > hitDeadline)
        {
          queue.Dequeue();
          continue;
        }

        // Skip entries cancelled by interrupt (prune them too).
        if (cast.Cancelled)
        {
          queue.Dequeue();
          continue;
        }

        // Hit arrived within the cast window: attribute and consume.
        if (hitTime >= cast.CastStartTime)
        {
          queue.Dequeue();
          return cast.SpellName;
        }

        // Hit is before this cast's start — no match possible in this context.
        break;
      }
      return null;
    }

    // Clears all queued casts. Called from DamageLineParser.ResetParseState between test runs.
    internal void Clear() => _queues.Clear();

    private class PendingCast
    {
      internal string SpellName;
      internal double CastingTimeSeconds;
      internal double CastStartTime;
      internal bool Cancelled;
    }
  }
}
