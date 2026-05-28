using log4net;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace EQLogParser
{
  internal class HealingLineParser
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static HealingLineParser Instance { get; } = new();

    // Dalaya-only: "<Healer> performs an exceptional heal! (N)" is a third-person crit
    // announcement. We treat these as crit markers paired to existing heal records (same
    // model as live-EQ "(Critical)" inline modifiers and Dalaya melee-crit pairing in
    // DamageLineParser). Lines that don't pair within a 1s window are discarded —
    // fabricating phantom heal records for unobservable third-party heals would inflate
    // raid healing totals the same way fabricating phantom damage records would.
    private const string ExceptionalHealMarker = " performs an exceptional heal! (";
    private const double ExceptionalPairWindowSeconds = 1.0;

    private readonly IEQDataStore _dataStore;
    private readonly PlayerRegistry _playerRegistry;

    // Buffers for bidirectional crit pairing. Announcements can arrive before OR after
    // the heal line they belong to within the same log second.
    private readonly List<PendingExceptional> _pendingExceptionals = [];
    private readonly List<RecentHeal> _recentHeals = [];

    public HealingLineParser() : this(EQDataStore.Instance, PlayerRegistry.Instance) { }

    public HealingLineParser(IEQDataStore dataStore, PlayerRegistry playerRegistry)
    {
      _dataStore = dataStore;
      _playerRegistry = playerRegistry;
    }

    // Test hook: clear pairing buffers between tests.
    internal void ResetParseState()
    {
      _pendingExceptionals.Clear();
      _recentHeals.Clear();
    }

    // Test entry point: parse a single action line and return the resulting record
    // without writing to RecordsStore. Applies the same "you"/"your" resolution AND
    // exceptional-heal crit pairing as Process so tests can assert against the final
    // attribution. Returns null for announcement lines (they have no record of their
    // own — they only modify a paired heal record).
    internal HealRecord ParseLine(string action, double beginTime = 0d)
    {
      if (string.IsNullOrEmpty(action) || action.Length < 23)
      {
        return null;
      }

      var index = action.LastIndexOf(" healed ", action.Length, StringComparison.Ordinal);
      if (index > -1)
      {
        return ParseHealLine(action, index, beginTime);
      }

      var markerIndex = action.IndexOf(ExceptionalHealMarker, StringComparison.Ordinal);
      if (markerIndex > 0 && action[^1] == ')')
      {
        TryPairExceptional(action, markerIndex, beginTime);
      }
      return null;
    }

    public bool Process(LineData lineData)
    {
      var action = lineData.Action;
      try
      {
        int index;
        if (action.Length >= 23 && (index = action.LastIndexOf(" healed ", action.Length, StringComparison.Ordinal)) > -1 &&
          ParseHealLine(action, index, lineData.BeginTime) is { } record)
        {
          RecordsStore.Instance.Add(record, lineData.BeginTime);
          return true;
        }
        else if (action.Length >= ExceptionalHealMarker.Length + 3 && action[^1] == ')'
          && (index = action.IndexOf(ExceptionalHealMarker, StringComparison.Ordinal)) > 0)
        {
          // No record produced — pairs to an already-stored heal record (or a future one).
          // Either way the line is "consumed" by this parser, so return true to stop the chain.
          TryPairExceptional(action, index, lineData.BeginTime);
          return true;
        }
      }
      catch (ArgumentNullException ne)
      {
        Log.Error(ne);
      }
      catch (NullReferenceException nr)
      {
        Log.Error(nr);
      }
      catch (ArgumentOutOfRangeException aor)
      {
        Log.Error(aor);
      }
      catch (ArgumentException ae)
      {
        Log.Error(ae);
      }

      return false;
    }

    private HealRecord HandleHealed(string part, int optional, double beginTime)
    {
      // [Sun Feb 24 21:00:58 2019] Foob's promised interposition is fulfilled Foob healed himself for 44238 hit points by Promised Interposition Heal V. (Lucky Critical)
      // [Sun Feb 24 21:01:01 2019] Rowanoak is soothed by Brell's Soothing Wave. Farzi healed Rowanoak for 524 hit points by Brell's Sacred Soothing Wave.
      // [Sun Feb 24 21:00:52 2019] Kuvani healed Tolzol over time for 11000 hit points by Spirit of the Wood XXXIV.
      // [Sun Feb 24 21:00:52 2019] Kuvani healed Foob over time for 9409 (11000) hit points by Spirit of the Wood XXXIV.
      // [Sun Feb 24 21:00:58 2019] Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.
      // [Sun Feb 24 21:01:00 2019] Tolzol healed itself for 548 hit points.
      // [Sun Feb 24 21:01:01 2019] Piemastaj`s pet has been healed for 15000 hit points by Enhanced Theft of Essence Effect X.
      // [Sun Feb 24 23:30:51 2019] Piemastaj`s pet glows with holy light. Findawenye healed Piemastaj`s pet for 2823 (78079) hit points by Mending Splash Rk. III. (Critical)
      // [Mon Feb 18 21:21:12 2019] Nylenne has been healed over time for 8211 hit points by Roar of the Lion 6.
      // [Mon Feb 18 21:20:39 2019] You have been healed over time for 1063 (8211) hit points by Roar of the Lion 6.
      // [Mon Feb 18 21:17:35 2019] Snowzz healed Malkatar over time for 8211 hit points by Roar of the Lion 6.
      // [Wed Nov 06 14:19:54 2019] Your ward heals you as it breaks! You healed Niktaza for 8970 (86306) hit points by Healing Ward. (Critical)

      HealRecord record = null;
      var test = part[..optional];

      var done = false;
      var healer = "";
      var healed = "";
      string spell = null;
      string subType = null;
      var type = Labels.Heal;
      var heal = uint.MaxValue;
      uint overHeal = 0;

      var previous = test.Length >= 2 ? test.LastIndexOf(' ', test.Length - 2) : -1;
      if (previous > -1)
      {
        if (test.IndexOf("are ", previous + 1, StringComparison.Ordinal) > -1)
        {
          done = true;
        }
        else if ((previous - 1 >= 0 && (test[previous - 1] == '.' || test[previous - 1] == '!')) || (previous - 9 > 0 &&
          test.IndexOf("fulfilled", previous - 9, StringComparison.Ordinal) > -1))
        {
          healer = test[(previous + 1)..];
        }
        else if (previous - 4 >= 0 && test.IndexOf("has been", previous - 3, StringComparison.Ordinal) > -1)
        {
          healed = test[..(previous - 4)];

          if (part.Length > optional + 17 && part.IndexOf("over time", optional + 8, 9, StringComparison.Ordinal) > -1)
          {
            type = Labels.Hot;
          }
        }
        else if (previous >= 0 && test.IndexOf("has", previous, StringComparison.Ordinal) > -1)
        {
          healer = test[..previous];
          type = Labels.Heal;
          subType = Labels.Heal;
        }
        else if (previous - 5 >= 0 && test.IndexOf("have been", previous - 4, StringComparison.Ordinal) > -1)
        {
          healed = test[..(previous - 5)];

          if (part.Length > optional + 17 && part.IndexOf("over time", optional + 8, 9, StringComparison.Ordinal) > -1)
          {
            type = Labels.Hot;
          }
        }
        else
        {
          var wardIndex = test.IndexOf("`s ward", StringComparison.OrdinalIgnoreCase);
          if (wardIndex > 0)
          {
            // assign owner of ward as healer
            healer = test[..wardIndex];
          }
          // Dalaya: "Your <Spell> healed <Target> for N damage." — the spell name
          // lives in the prefix rather than after " by " at the end. Fallback so it
          // doesn't collide with live-EQ "Your ward heals you as it breaks! You" which
          // already hits the .! branch above.
          else if (test.StartsWith("Your ", StringComparison.Ordinal) && test.Length > 5)
          {
            healer = "You";
            spell = test[5..];
          }
        }
      }
      else
      {
        healer = test[..optional];
      }

      if (!done)
      {
        var amountIndex = -1;
        if (healed.Length == 0)
        {
          var afterHealed = optional + 8;
          var forIndex = part.IndexOf(" for ", afterHealed, StringComparison.Ordinal);

          if (forIndex > 1)
          {
            if (forIndex - 9 >= 0 && part.IndexOf("over time", forIndex - 9, StringComparison.Ordinal) > -1)
            {
              type = Labels.Hot;
              healed = part.Substring(afterHealed, forIndex - afterHealed - 10);
            }
            else
            {
              healed = part[afterHealed..forIndex];
            }

            amountIndex = forIndex + 5;
          }
        }
        else
        {
          if (type == Labels.Heal)
          {
            amountIndex = optional + 12;
          }
          else if (type == Labels.Hot)
          {
            amountIndex = optional + 22;
          }
        }

        if (amountIndex > -1)
        {
          var amountEnd = part.IndexOf(' ', amountIndex);
          if (amountEnd > -1)
          {
            var value = TextUtils.ParseUInt(part[amountIndex..amountEnd]);
            if (value != uint.MaxValue)
            {
              heal = value;
            }

            var overEnd = -1;
            if (part.Length > amountEnd + 1 && part[amountEnd + 1] == '(')
            {
              overEnd = part.IndexOf(')', amountEnd + 2);
              if (overEnd > -1)
              {
                var value2 = TextUtils.ParseUInt(part.AsSpan(amountEnd + 2, overEnd - amountEnd - 2));
                if (value2 != uint.MaxValue)
                {
                  overHeal = value2;
                }
              }
            }

            var rest = overEnd > -1 ? overEnd : amountEnd;
            var byIndex = part.IndexOf(" by ", rest, StringComparison.Ordinal);
            if (byIndex > -1)
            {
              var periodIndex = part.LastIndexOf('.');
              if (periodIndex > -1 && periodIndex - byIndex - 4 > 0)
              {
                spell = part.Substring(byIndex + 4, periodIndex - byIndex - 4);
              }
            }
          }
        }

        // verify heal actually parsed
        if (heal == uint.MaxValue)
          return null;

        if (string.IsNullOrEmpty(healed))
          return null;

        // fix healer
        if (string.IsNullOrEmpty(healer) && spell?.StartsWith("Theft of Essence", StringComparison.OrdinalIgnoreCase) == true)
        {
          healer = Labels.Unk;
        }

        // verify healer parsed properly
        if (string.IsNullOrEmpty(healer) || healer.Length > 64)
          return null;

        healer = _playerRegistry.ReplacePlayer(healer, healed);
        healed = _playerRegistry.ReplacePlayer(healed, healer);

        // check for pets
        var possessive = healed.IndexOf("`s ", StringComparison.Ordinal);
        if (possessive > -1 && _playerRegistry.IsVerifiedPlayer(healed[..possessive]))
        {
          _playerRegistry.AddVerifiedPet(healed);
        }

        // found a bst/mag/nec pet
        if (spell?.StartsWith("Mend Companion", StringComparison.Ordinal) == true || spell?.StartsWith("Warder's Shielding", StringComparison.Ordinal) == true ||
          spell?.StartsWith("Might of the Wild Spirits", StringComparison.Ordinal) == true)
        {
          _playerRegistry.AddVerifiedPet(healed);
          if (PlayerRegistry.IsPossiblePlayerName(healer))
          {
            _playerRegistry.AddVerifiedPlayer(healer, beginTime);
            _playerRegistry.AddPetToPlayer(healed, healer);
          }
        }

        // fix subtype
        if (subType == null)
        {
          subType = string.IsNullOrEmpty(spell) ? Labels.SelfHeal : string.Intern(spell);
        }

        // Dalaya HoT reclassification. Live-EQ heal lines carry "over time" in the
        // line text and are classified to Labels.Hot above. Dalaya's "Your <Spell>
        // healed <Target> for N damage." format carries no such marker, so we look
        // up the spell and reclassify if any spells.txt entry under that name is a
        // duration heal. GetHotSpellByName specifically handles dual-entry autocast
        // spells (e.g., Relic: Sihala's Empathy 1076 + 7591) where the level-255
        // recourse holds the HoT signal — see EQDataStore.GetHotSpellByName.
        if (type == Labels.Heal && !string.IsNullOrEmpty(spell)
            && _dataStore?.GetHotSpellByName(spell) != null)
        {
          type = Labels.Hot;
        }

        record = new HealRecord
        {
          Total = heal,
          OverTotal = overHeal,
          Healer = string.Intern(healer),
          Healed = string.Intern(healed),
          Type = string.Intern(type),
          ModifiersMask = -1,
          SubType = subType
        };

        if (part[^1] == ')')
        {
          // using 4 here since the shortest modifier should at least be 3 even in the future. probably.
          var firstParen = part.LastIndexOf('(', part.Length - 4);
          if (firstParen > -1)
          {
            record.ModifiersMask = LineModifiersParser.ParseHeal(_playerRegistry, record.Healer,
              part.Substring(firstParen + 1, part.Length - 1 - firstParen - 1), beginTime);
          }
        }
      }

      return record;
    }

    // Parse a "... healed ..." line through HandleHealed, normalize the healer/healed
    // names, check for a pending exceptional-heal announcement that matches this record,
    // and remember the record for a future announcement.
    private HealRecord ParseHealLine(string action, int healedIndex, double beginTime)
    {
      var record = HandleHealed(action, healedIndex, beginTime);
      if (record == null)
      {
        return null;
      }

      // Backward pairing: did an "X performs an exceptional heal! (N)" line arrive in the
      // last second whose healer/amount match this record? If so, mark this heal as a crit.
      TrimPendingExceptionals(beginTime);
      for (var i = 0; i < _pendingExceptionals.Count; i++)
      {
        var pending = _pendingExceptionals[i];
        if (pending.Amount == record.Total && string.Equals(pending.Healer, record.Healer, StringComparison.Ordinal))
        {
          record.ModifiersMask = ApplyCrit(record.ModifiersMask);
          _pendingExceptionals.RemoveAt(i);
          break;
        }
      }

      // Forward pairing: announcement may still arrive after this line. Remember the
      // record so a subsequent announcement within the window can update it.
      TrimRecentHeals(beginTime);
      _recentHeals.Add(new RecentHeal(beginTime, record));
      return record;
    }

    // Pair an "X performs an exceptional heal! (N)" announcement with a recent or future
    // heal record. Same model as DamageLineParser's _lastCrit pairing for "delivers a
    // critical blast! (N)" announcements.
    private void TryPairExceptional(string action, int markerIndex, double beginTime)
    {
      if (markerIndex < 2 || markerIndex > 64)
      {
        return;
      }

      var amountStart = markerIndex + ExceptionalHealMarker.Length;
      var closeParen = action.Length - 1;
      if (closeParen <= amountStart)
      {
        return;
      }

      var amount = TextUtils.ParseUInt(action.AsSpan(amountStart, closeParen - amountStart));
      if (amount == uint.MaxValue || amount == 0)
      {
        // (0) means "no crit on this cast" — nothing to mark.
        return;
      }

      var healer = action[..markerIndex];

      // Backward pairing: look for an already-parsed heal record from this healer with
      // this exact amount in the last 1s.
      TrimRecentHeals(beginTime);
      for (var i = _recentHeals.Count - 1; i >= 0; i--)
      {
        var entry = _recentHeals[i];
        if (entry.Record.Total == amount && string.Equals(entry.Record.Healer, healer, StringComparison.Ordinal))
        {
          entry.Record.ModifiersMask = ApplyCrit(entry.Record.ModifiersMask);
          _recentHeals.RemoveAt(i);
          return;
        }
      }

      // Forward pairing: stash until the matching heal line arrives. If it never does
      // (unobservable third-party heal), the entry ages out in TrimPendingExceptionals.
      TrimPendingExceptionals(beginTime);
      _pendingExceptionals.Add(new PendingExceptional(beginTime, string.Intern(healer), amount));
    }

    private void TrimPendingExceptionals(double now)
    {
      while (_pendingExceptionals.Count > 0 && now - _pendingExceptionals[0].Time > ExceptionalPairWindowSeconds)
      {
        _pendingExceptionals.RemoveAt(0);
      }
    }

    private void TrimRecentHeals(double now)
    {
      while (_recentHeals.Count > 0 && now - _recentHeals[0].Time > ExceptionalPairWindowSeconds)
      {
        _recentHeals.RemoveAt(0);
      }
    }

    private static short ApplyCrit(short mask) => mask < 0 ? LineModifiersParser.Crit : (short)(mask | LineModifiersParser.Crit);

    private readonly record struct PendingExceptional(double Time, string Healer, uint Amount);
    private readonly record struct RecentHeal(double Time, HealRecord Record);
  }
}
