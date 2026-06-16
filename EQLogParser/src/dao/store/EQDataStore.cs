using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  internal enum SpellClass
  {
    War = 1, Clr = 2, Pal = 4, Rng = 8, Shd = 16, Dru = 32, Mnk = 64, Brd = 128, Rog = 256,
    Shm = 512, Nec = 1024, Wiz = 2048, Mag = 4096, Enc = 8192, Bst = 16384, Ber = 32768
  }

  internal enum SpellTarget
  {
    Los = 1, Casterae = 2, Castergroup = 3, Casterpb = 4, Singletarget = 5, Self = 6, Targetae = 8, Pet = 14,
    Casterpbplayers = 36, Pet2 = 38, Nearbyplayersae = 40, Targetgroup = 41, Directionae = 42, Targetringae = 45
  }

  internal enum SpellResist
  {
    Undefined = -2, Reflected = -1, Unresistable = 0, Magic, Fire, Cold, Poison, Disease, Lowest, Average, Physical, Corruption
  }

  internal static class Labels
  {
    public const string Absorb = "Absorb";
    public const string Dd = "Direct Damage";
    public const string Dot = "DoT Tick";
    public const string Ds = "Damage Shield";
    public const string Rs = "Reverse DS";
    public const string Bane = "Bane Damage";
    public const string OtherDmg = "Other Damage";
    public const string Proc = "Proc";
    public const string Hot = "HoT Tick";
    public const string Heal = "Direct Heal";
    public const string Melee = "Melee";
    public const string SelfHeal = "Melee Heal";
    public const string NoData = "No Data Available";
    public const string NoNpcs = "No NPCs Selected";
    public const string PetPlayerOption = "Players +Pets";
    public const string PlayerOption = "Players";
    public const string PetOption = "Pets";
    public const string RaidOption = "Raid";
    public const string RaidTotals = "Totals";
    public const string Riposte = "Riposte";
    public const string AllOption = "Uncategorized";
    public const string ByGroupOption = "Group View";
    public const string Unassigned = "Unknown Pet Owner";
    public const string Unk = "Unknown";
    public const string UnkSpell = "Unknown Spell";
    public const string ReceivedHealParse = "Received Healing";
    public const string HealParse = "Healing";
    public const string TankParse = "Tanking";
    public const string TopHealParse = "Top Heals";
    public const string DamageParse = "Damage";
    public const string Miss = "Miss";
    public const string Dodge = "Dodge";
    public const string Parry = "Parry";
    public const string Block = "Block";
    public const string Invulnerable = "Invulnerable";
  }

  internal interface IEQDataStore
  {
    SpellData GetDamagingSpellByName(string name);
    SpellData GetSpellByName(string name);
    SpellData GetHotSpellByName(string name);
    bool IsOldSpell(string name);
    string AbbreviateSpellName(string spell);
    SpellData GetSpellByAbbrv(string abbrv);
  }

  internal class EQDataStore : IEQDataStore, ILifecycle
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // singleton with set for unit test
    internal static EQDataStore Instance { get; set; } = new();

    private static readonly SpellAbbrvComparer AbbrvComparer = new();
    private readonly HashSet<SpellData> _allSpellData = [];
    private readonly Dictionary<string, bool> _oldSpellNamesDb = [];
    private readonly SpellTreeNode _landsOnOtherTree = new();
    private readonly SpellTreeNode _landsOnYouTree = new();
    private readonly SpellTreeNode _wearOffTree = new();

    // definitely used in single thread
    private readonly Dictionary<string, string> _titleToClass = [];
    private readonly ConcurrentDictionary<string, byte> _allNpcs = new();
    private readonly ConcurrentDictionary<string, SpellData> _spellsAbbrvDb = new();
    private readonly ConcurrentDictionary<string, string> _spellsToClass = new();
    private readonly ConcurrentDictionary<string, string> _spellAbbrvCache = new();
    private readonly ConcurrentDictionary<string, List<SpellData>> _spellsNameDb = new();
    private readonly ConcurrentDictionary<string, SpellData> _unknownSpellDb = new();
    // Per-spell effect slot data from data/spell-effects.json, keyed by SpellData.Id
    // (string). Populated at startup; consumed by ComputeHotTickInfo. See
    // memory project_dot_hot_validation Phase 2 + tools/spells/convert_spells.py.
    private readonly Dictionary<string, SpellEffects> _spellEffects = [];
    // Spell lookup by numeric ID string — populated alongside _spellsNameDb.
    // Consumed by SpellDetailsPopup to resolve [Spell N] references in effect text.
    private readonly Dictionary<string, SpellData> _spellsById = [];
    // Human-readable spell descriptions from data/spell-descriptions.json (keyed by
    // spell id string), emitted by convert_spells.py --dbstr. Populated at startup
    // and stamped onto SpellData.Description during spell loading.
    private readonly Dictionary<string, string> _spellDescriptions = [];
    private readonly ConcurrentDictionary<string, string> _classColors = new();
    private readonly ConcurrentDictionary<SpellClass, string> _classNames = new();
    private readonly ConcurrentDictionary<string, SpellClass> _classesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sortedClassList = [];
    private readonly int _classListCount;

    // rank abbreviation
    private readonly HashSet<string> RankWords;
    private readonly Regex RomanRegex = new(@"^M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal EQDataStore()
    {
      var spellList = new List<SpellData>();
      LifecycleManager.Register(this);

      RankWords = new(StringComparer.OrdinalIgnoreCase)
      {
        "Azia", "Beza", "Caza", "Third", "Fifth", "Octave"
      };

      // populate ClassNames from SpellClass enum and resource table
      foreach (var item in Enum.GetValues<SpellClass>())
      {
        if (Enum.GetName(item)?.ToUpperInvariant() is string { } resourceName)
        {
          var name = Resource.ResourceManager.GetString(resourceName, CultureInfo.InvariantCulture);
          if (!string.IsNullOrEmpty(name))
          {
            _classNames[item] = string.Intern(name);
            _classesByName[name] = item;
          }

          var color = Resource.ResourceManager.GetString($"{resourceName}_COLOR", CultureInfo.InvariantCulture);
          if (!string.IsNullOrEmpty(color))
          {
            _classColors[name] = color;
          }
        }
      }

      _sortedClassList.AddRange(_classNames.Values);
      _sortedClassList.Sort();
      _classListCount = _sortedClassList.Count;

      // Player title mapping for /who queries
      ConfigUtil.ReadList(@"data\titles.txt").ForEach(line =>
      {
        var split = line.Split('=');
        if (split.Length == 2)
        {
          _titleToClass[split[0]] = split[0];
          foreach (var title in split[1].Split(','))
          {
            _titleToClass[title + " (" + split[0] + ")"] = split[0];
          }
        }
      });

      // Old Spell cache (EQEMU)
      ConfigUtil.ReadList(@"data\oldspells.txt").ForEach(line => _oldSpellNamesDb[line] = true);

      var procCache = new Dictionary<string, bool>();
      foreach (var line in ConfigUtil.ReadList(@"data\procs.txt").Where(line => line.Length > 0 && line[0] != '#'))
      {
        procCache[line] = true;
        procCache[$"New {line}"] = true;
      }

      LoadSpellEffects();
      LoadSpellDescriptions();

      foreach (ref var line in CollectionsMarshal.AsSpan(ConfigUtil.ReadList(@"data\spells.txt")))
      {
        try
        {
          var spellData = ParseCustomSpellData(line);
          if (spellData != null)
          {
            spellData.Proc = procCache.ContainsKey(spellData.Name) ? (byte)1 : (byte)0;
            spellList.Add(spellData);

            if (_spellsNameDb.TryGetValue(spellData.Name, out var spellDataList))
            {
              spellDataList.Add(spellData);
            }
            else
            {
              _spellsNameDb[spellData.Name] = [spellData];
            }

            if (!string.IsNullOrEmpty(spellData.Id))
            {
              _spellsById.TryAdd(spellData.Id, spellData);
            }

            if (_spellsAbbrvDb.TryAdd(spellData.NameAbbrv, spellData))
            {
            }
            else if (string.Compare(_spellsAbbrvDb[spellData.NameAbbrv].Name, spellData.Name, StringComparison.OrdinalIgnoreCase) < 0)
            {
              // try to keep the newest version
              _spellsAbbrvDb[spellData.NameAbbrv] = spellData;
            }

            // restricted received spells to only ADPS related
            if (!string.IsNullOrEmpty(spellData.LandsOnOther) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath([.. spellData.LandsOnOther.Trim().Split(' ')], _landsOnOtherTree, spellData);
            }

            if (!string.IsNullOrEmpty(spellData.LandsOnYou) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath([.. spellData.LandsOnYou.Trim().Split(' ')], _landsOnYouTree, spellData);
            }

            if (!string.IsNullOrEmpty(spellData.WearOff) && (spellData.Adps > 0 || spellData.IsBeneficial))
            {
              BuildSpellPath([.. spellData.WearOff.Trim().Split(' ')], _wearOffTree, spellData);
            }
          }
        }
        catch (OverflowException ex)
        {
          Log.Error("Error reading spell data", ex);
        }
      }

      var keepOut = new Dictionary<string, byte>();

      var itemSpellsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
      foreach (var line in ConfigUtil.ReadList(@"data\itemspells.txt").Where(line => line.Length > 0 && line[0] != '#'))
      {
        itemSpellsCache[string.Intern(line)] = true;
      }

      foreach (ref var spell in CollectionsMarshal.AsSpan(spellList))
      {
        _allSpellData.Add(spell);

        // ignore spells tied to items for building spell class cache
        if (itemSpellsCache.ContainsKey(spell.Name)) continue;

        // exact class match
        if (spell.Level < 255 && _classNames.TryGetValue((SpellClass)spell.ClassMask, out var theClassName))
        {
          // Obviously illusions are bad to look for
          // Call of Fire is Ranger only and self target but VT clickie lets warriors use it
          if (!spell.NameAbbrv.Contains("Illusion", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains("Mount", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.EndsWith(" gate", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains(" Synergy", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains("Call of Fire", StringComparison.OrdinalIgnoreCase) &&
            !spell.NameAbbrv.Contains("Pet Heal", StringComparison.OrdinalIgnoreCase) &&
            !(spell.ClassMask == (int)SpellClass.Clr && spell.NameAbbrv.Contains("Effect")) &&
            !(spell.ClassMask == (int)SpellClass.Brd && spell.Level >= 250))
          {
            // these need to be unique and keep track if a conflict is found
            if (_spellsToClass.ContainsKey(spell.Name))
            {
              _spellsToClass.TryRemove(spell.Name, out _);
              keepOut[spell.Name] = 1;
            }
            else if (!keepOut.ContainsKey(spell.Name))
            {
              _spellsToClass[spell.Name] = theClassName;
            }
          }
        }
        else
        {
          // these need to be unique and keep track if a conflict is found
          if (_spellsToClass.ContainsKey(spell.Name))
          {
            _spellsToClass.TryRemove(spell.Name, out _);
            keepOut[spell.Name] = 1;
          }
        }
      }

      // load NPCs
      foreach (ref var line in CollectionsMarshal.AsSpan(ConfigUtil.ReadList(@"data\npcs.txt")))
      {
        if (line?.Trim() is string trimmed && trimmed.Length > 0)
        {
          _allNpcs[string.Intern(trimmed)] = 1;
        }
      }

      return;
    }

    internal bool IsKnownNpc(string npc) => !string.IsNullOrEmpty(npc) && _allNpcs.ContainsKey(npc.ToLower(CultureInfo.CurrentCulture));
    public bool IsOldSpell(string name) => !string.IsNullOrEmpty(name) && _oldSpellNamesDb.ContainsKey(name);
    internal bool IsPlayerSpell(string name) => GetSpellByName(name)?.ClassMask > 0;

    internal string GetClassFromTitle(string title) => _titleToClass.GetValueOrDefault(title);
    internal List<string> GetClassList() => [.. _sortedClassList];
    internal int GetClassListCount() => _classListCount;
    internal bool IsValidClassName(string className) => !string.IsNullOrEmpty(className) && _classesByName.ContainsKey(className);

    // Player-castable spells for the trigger SpellPickerDialog. One entry per
    // spell abbreviation (mirrors GetAllCategories' use of _spellsAbbrvDb so
    // ranks collapse to a single row). Phase-agnostic — the SpellPickerFilter
    // applies search/class/category narrowing. See memory project-trigger-spell-picker.
    internal IEnumerable<SpellData> GetSpellsForPicker() => _spellsAbbrvDb.Values.Where(s => s.ClassMask > 0);

    // All spells, deduped by abbreviation (one row per unique name), sorted by name.
    // Unlike GetSpellsForPicker(), includes NPC/ability entries (no ClassMask filter).
    internal IEnumerable<SpellData> GetSpellsForBrowser() =>
      _spellsAbbrvDb.Values
        .Where(static s => !string.IsNullOrWhiteSpace(s.Name) &&
                           !(s.Name.Length >= 2 && char.IsLetter(s.Name[0]) && s.Name[1..].All(char.IsDigit)))
        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

    // Sorted distinct SPA numbers that appear in the loaded spell-effects data (SPA 254
    // excluded — it marks unused slots). Used to populate the Spell Browser "Has Effect"
    // filter dropdown so the list reflects only effects actually present in this patch.
    internal IEnumerable<int> GetUsedSpas() =>
      _spellEffects.Values
        .Where(e => e.Slots != null)
        .SelectMany(e => e.Slots)
        .Select(s => s.Spa)
        .Where(spa => spa != 254)
        .Distinct()
        .OrderBy(spa => spa);

    // Map a class display name (as returned by GetClassList /
    // PlayerRegistry.GetDefaultPlayerClass) to its SpellClass flag, for the
    // spell picker's class filter. Null when the name isn't a known class.
    internal SpellClass? GetSpellClassByName(string className) =>
      !string.IsNullOrEmpty(className) && _classesByName.TryGetValue(className, out var cls) ? cls : null;

    public string AbbreviateSpellName(string spell)
    {
      if (_spellAbbrvCache.TryGetValue(spell, out var cached))
        return cached;

      // Split once into tokens
      var parts = spell.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      var count = parts.Length;

      // --- Handle "<name> Rk. I|II|III|..."
      if (count >= 3 &&
          parts[count - 2].Equals("Rk.", StringComparison.OrdinalIgnoreCase) &&
          IsRoman(parts[count - 1]))
      {
        count -= 2;
      }

      // --- Strip other trailing rank indicators
      while (count > 0)
      {
        var last = parts[count - 1];

        if (RankWords.Contains(last))
        {
          count--;
          continue;
        }

        if (IsRoman(last))
        {
          count--;
          continue;
        }

        if (int.TryParse(last, out _))
        {
          count--;
          continue;
        }

        break; // reached base name
      }

      // If nothing removed → return original
      if (count == parts.Length)
      {
        _spellAbbrvCache[spell] = spell;
        return string.Intern(spell);
      }

      // Rebuild abbreviated name
      var result = string.Join(" ", parts, 0, count);

      _spellAbbrvCache[spell] = result;
      return string.Intern(result);

      bool IsRoman(string s) => RomanRegex.IsMatch(s);
    }


    internal SpellData AddUnknownSpell(string spellName)
    {
      if (!_unknownSpellDb.TryGetValue(spellName, out var result))
      {
        // unknown spell
        var spellData = new SpellData
        {
          Id = string.Intern(spellName),
          Name = string.Intern(spellName),
          NameAbbrv = string.Intern(AbbreviateSpellName(spellName)),
          IsUnknown = true
        };
        _unknownSpellDb[spellName] = spellData;
        result = spellData;
      }
      return result;
    }

    internal string GetClassColor(string className)
    {
      if (!string.IsNullOrEmpty(className) && _classColors.TryGetValue(className, out var color))
      {
        return color;
      }
      return null;
    }

    internal SpellClass? GetClassEnum(string className)
    {
      if (!string.IsNullOrEmpty(className) && _classesByName.TryGetValue(className, out var theClass))
      {
        return theClass;
      }
      return 0;
    }

    internal string GetSpellClass(string name)
    {
      if (!string.IsNullOrEmpty(name) && _spellsToClass.TryGetValue(name, out var result))
      {
        return result;
      }
      return null;
    }

    public SpellData GetSpellByAbbrv(string abbrv)
    {
      if (!string.IsNullOrEmpty(abbrv) && abbrv != Labels.Unassigned && _spellsAbbrvDb.TryGetValue(abbrv, out var value))
      {
        return value;
      }

      return null;
    }

    internal SpellData GetSpellDataByName(string name)
    {
      if (_spellsAbbrvDb.TryGetValue(name, out var spellData))
        return spellData;
      if (_spellsNameDb.TryGetValue(name, out var list))
        return list.Find(item => item.Adps > 0);
      return null;
    }

    internal SpellData GetDetSpellByName(string name)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => !item.IsBeneficial);
      }

      return spellData;
    }

    public SpellData GetDamagingSpellByName(string name)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => item.Damaging > 0);
      }

      return spellData;
    }

    // Returns a beneficial spell entry under this name (used by HealingValidator
    // to look up Target type for the AoE-heal filter). The historical filter was
    // `Damaging < 0` (live-EQ convention where healing spells carry a negative
    // Damaging value) but `convert_spells.py:239` only ever emits `Damaging` as
    // `0` or `1` for Dalaya, so the old filter never matched -- HealingValidator
    // was a silent no-op on every Dalaya log. The new filter `IsBeneficial &&
    // Damaging > 0` finds any beneficial spell with observable log output, which
    // is enough for the AoE check (the caller cares about Target, not whether
    // the spell heals specifically).
    internal SpellData GetHealingSpellByName(string name)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        spellData = spellList.Find(item => item.IsBeneficial && item.Damaging > 0);
      }

      return spellData;
    }

    // HoT-specific lookup. Several Dalaya spells exist twice in spells.txt under the
    // same name: a player-cast entry (Level <= 250, Duration=0, just the cast frame)
    // and a server-side recourse/autocast entry (Level=255, Duration > 0, the HoT
    // tick effect). Example: "Relic: Sihala's Empathy" (id 1076 + id 7591), where
    // 1076 autocasts 7591 — the log line shows the spell name but the ticking
    // effect is the level-255 variant. GetSpellByName's level-prefer logic
    // returns the player-cast entry, hiding the HoT signal. This lookup walks
    // the full spell list and returns the first entry with both Duration > 0 and
    // IsBeneficial set, regardless of level.
    public SpellData GetHotSpellByName(string name)
    {
      if (string.IsNullOrEmpty(name) || name == Labels.UnkSpell)
      {
        return null;
      }

      if (!_spellsNameDb.TryGetValue(name, out var spellList))
      {
        return null;
      }

      return spellList.Find(item => item.Duration > 0 && item.IsBeneficial);
    }

    public IEnumerable<string> GetAllCategories() =>
      _spellsAbbrvDb.Values
        .Where(s => !string.IsNullOrEmpty(s.Category))
        .SelectMany(s => s.Category.Split(';'))
        .Distinct()
        .OrderBy(c => c);

    public SpellData GetSpellByName(string name)
    {
      SpellData spellData = null;

      if (!string.IsNullOrEmpty(name) && name != Labels.UnkSpell && _spellsNameDb.TryGetValue(name, out var spellList))
      {
        if (spellList.Count <= 10)
        {
          foreach (var spell in CollectionsMarshal.AsSpan(spellList))
          {
            if (spellData == null || (spellData.Level < spell.Level && spell.Level <= 250) || (spellData.Level > 250 && spell.Level <= 250))
            {
              spellData = spell;
            }
          }
        }
        else
        {
          spellData = spellList.LastOrDefault();
        }
      }

      return spellData;
    }

    internal bool TryGetLandsOnOther(string[] split, out SpellTreeResult found, out string player)
    {
      player = null;
      found = SearchSpellPath(_landsOnOtherTree, split);

      if (found.SpellData.Count > 0 && found.DataIndex > -1)
      {
        player = string.Join(" ", [.. split], 0, found.DataIndex + 1);
        if (player.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
        {
          // if string is only 2 then it must be invalid
          player = (player.Length > 2) ? player[..^2] : null;
        }

        found.SpellData = FindByLandsOn(player, found.SpellData);
        return true;
      }

      return false;
    }

    internal bool TryGetLandsOnYou(string[] split, out SpellTreeResult found)
    {
      found = SearchSpellPath(_landsOnYouTree, split);

      if (found.DataIndex == 0 && found.SpellData.Count > 0)
      {
        found.SpellData = FindByLandsOn(ConfigUtil.PlayerName, found.SpellData);

        // check Adps
        var spellDataSet = AdpsTracker.Instance.GetLandsOnSpells(found.SpellData[0].LandsOnYou);
        if (spellDataSet != null)
        {
          var spellData = spellDataSet.Count == 1 ? spellDataSet.First() : FindPreviousCast(ConfigUtil.PlayerName, [.. spellDataSet], true);

          // this only handles latest versions of spells so an older one may have given us the landsOn string and then it wasn't found
          // for some spells this makes sense because of the level requirements and it wouldn't do anything but thats not true for all of them
          // need to handle older spells and multiple rate values
          if (spellData != null)
          {
            AdpsTracker.Instance.UpdateAdps(spellData);
          }
        }

        return true;
      }

      return false;
    }

    internal bool TryGetWearOff(string[] split, out SpellTreeResult found)
    {
      found = SearchSpellPath(_wearOffTree, split);

      if (found.DataIndex == 0 && found.SpellData.Count > 0)
      {
        found.SpellData = FindByLandsOn(split[0], found.SpellData);

        // check Adps
        var spellDataSet = AdpsTracker.Instance.GetWearOffSpells(found.SpellData[0].WearOff);
        if (spellDataSet != null)
        {
          var spellData = spellDataSet.First();
          AdpsTracker.Instance.RemoveWearOff(spellData);
        }

        return true;
      }

      return false;
    }

    // Loads data/spell-effects.json into _spellEffects. Called once from the
    // constructor before spells.txt is parsed. Errors are logged but non-fatal —
    // ComputeHotTickInfo will simply return null for spells with no effect data,
    // and existing ADPS/breakdown features continue to work from spells.txt alone.
    private void LoadSpellEffects()
    {
      const string path = @"data\spell-effects.json";
      try
      {
        if (!File.Exists(path))
        {
          Log.Warn($"spell-effects sidecar not found at {path}; ComputeHotTickInfo will return null for all spells");
          return;
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dict = JsonSerializer.Deserialize<Dictionary<string, SpellEffects>>(json, options);
        if (dict == null) return;

        foreach (var (id, effects) in dict)
        {
          _spellEffects[string.Intern(id)] = effects;
        }
      }
      catch (IOException ex)
      {
        Log.Error("Error reading spell-effects.json", ex);
      }
      catch (JsonException ex)
      {
        Log.Error("Error parsing spell-effects.json", ex);
      }
    }

    // Loads data/spell-descriptions.json into _spellDescriptions. Called once from
    // the constructor before spells.txt is parsed. Non-fatal — descriptions are
    // cosmetic; all other features continue to work if the file is absent.
    private void LoadSpellDescriptions()
    {
      const string path = @"data\spell-descriptions.json";
      try
      {
        if (!File.Exists(path))
        {
          Log.Warn($"spell-descriptions sidecar not found at {path}; spell descriptions will not be available");
          return;
        }

        var json = File.ReadAllText(path);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (dict == null) return;

        foreach (var (id, desc) in dict)
        {
          _spellDescriptions[string.Intern(id)] = desc;
        }
      }
      catch (IOException ex)
      {
        Log.Error("Error reading spell-descriptions.json", ex);
      }
      catch (JsonException ex)
      {
        Log.Error("Error parsing spell-descriptions.json", ex);
      }
    }

    // Returns the SpellData for a numeric spell ID, or null when not found.
    // Used by SpellDetailsPopup to resolve [Spell N] references in effect text.
    internal SpellData GetSpellById(int id) =>
      _spellsById.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var s) ? s : null;

    // Returns the effect-slot data for a spell, or null when the spell isn't in
    // the sidecar (Healing Increment placeholders, etc.).
    internal SpellEffects GetSpellEffects(string spellId)
    {
      return !string.IsNullOrEmpty(spellId) && _spellEffects.TryGetValue(spellId, out var effects) ? effects : null;
    }

    // SPA effect id constants relevant to periodic-tick computation.
    private const int SpaCurrentHp = 0;          // classic regen spells (Regeneration, Chloroplast)
    private const int SpaCurrentHpRepeating = 100;

    // Computes the expected per-tick healing for a HoT spell cast by a player of
    // the given level and class. Returns null when the spell has no HoT slot in
    // the sidecar (direct heals, non-periodic spells, items without effect data).
    //
    // Output components:
    //   PerTickAmount       — spell-data base value scaled by Calc formula at
    //                         tick=1 (gear/AA modifiers NOT applied; this is the
    //                         spell-data baseline only).
    //   TickIntervalSeconds — 2 for Druid casters (Dalaya-specific HoT cadence),
    //                         else 6 (standard EverQuest server tick).
    //   TickCount           — durationSeconds / tickIntervalSeconds.
    //   TotalExpected       — PerTickAmount * TickCount.
    //
    // See memory project_dot_hot_validation Phase 2. Note: the Druid 2s override
    // is keyed on the caster's class only — a non-Druid clicking a Druid-line HoT
    // item gets the 6s interval, which may be wrong if the server resolves tick
    // rate by spell line rather than caster class. Phase 3 verification will
    // surface that signal if it shows up.
    // Name-based overload. Resolves dual-entry autocast spells correctly by
    // routing through GetHotSpellByName (which prefers the Duration > 0 entry).
    // Example: log line for "Relic: Sihala's Empathy" — the player-cast entry
    // (id 1076) has no SPA 100, but the autocast'd HoT effect (id 7591) does.
    internal HotTickInfo? ComputeHotTickInfo(string spellName, byte casterLevel, SpellClass casterClass)
    {
      var spell = GetHotSpellByName(spellName);
      return spell == null ? null : ComputeHotTickInfo(spell, casterLevel, casterClass);
    }

    internal HotTickInfo? ComputeHotTickInfo(SpellData spell, byte casterLevel, SpellClass casterClass)
    {
      if (spell == null) return null;

      var effects = GetSpellEffects(spell.Id);
      if (effects == null || effects.Slots == null) return null;

      // Find the first SPA 100 (Current_HP_Repeating) slot with a positive base
      // value (positive = heal, negative = damage). DoT classification belongs to
      // a sibling method; this one is HoT-only by contract.
      SpellSlotEffect hotSlot = null;
      foreach (var slot in effects.Slots)
      {
        if (slot.Spa == SpaCurrentHpRepeating && slot.Base1 > 0)
        {
          hotSlot = slot;
          break;
        }
      }

      // SPA 100 not found — fall back to SPA 0. Classic Dalaya regen spells
      // (Regeneration, Chloroplast, Pack variants) store per-tick HP restore
      // in SPA 0 with calc=100 rather than SPA 100. Guard with duration > 0
      // so instant heals (which also use SPA 0 but have durationBase=0) are
      // not mistaken for HoTs.
      if (hotSlot == null && (effects.DurationBase > 0 || effects.DurationCalc > 0))
      {
        foreach (var slot in effects.Slots)
        {
          if (slot.Spa == SpaCurrentHp && slot.Base1 > 0)
          {
            hotSlot = slot;
            break;
          }
        }
      }

      if (hotSlot == null) return null;

      var perTickAmount = SpellFormula.CalcValue(hotSlot.Calc, hotSlot.Base1, hotSlot.Max, tick: 1, casterLevel);
      var durationGameTicks = SpellFormula.CalcDuration(effects.DurationCalc, effects.DurationBase, casterLevel);
      // CalcDuration returns 0 when DurationCalc == 0, but Dalaya carries a real
      // DurationBase on those spells (e.g. Racing Flames, Prayer). The converter
      // treats DurationBase as the authoritative duration (see tools/spells/
      // convert_spells.py), so fall back to it rather than computing a zero tick
      // count — otherwise the HoT Effectiveness view shows 0 expected healing.
      if (durationGameTicks == 0 && effects.DurationBase > 0)
      {
        durationGameTicks = effects.DurationBase;
      }
      var durationSeconds = durationGameTicks * 6;
      var tickIntervalSeconds = casterClass == SpellClass.Dru ? 2 : 6;
      var tickCount = tickIntervalSeconds > 0 ? durationSeconds / tickIntervalSeconds : 0;
      var totalExpected = perTickAmount * tickCount;

      return new HotTickInfo(perTickAmount, tickIntervalSeconds, tickCount, totalExpected);
    }

    internal SpellData ParseCustomSpellData(string line)
    {
      SpellData spellData = null;
      if (!string.IsNullOrEmpty(line))
      {
        var data = line.Split('^');
        if (data.Length >= 11)
        {
          var duration = int.Parse(data[3], CultureInfo.InvariantCulture) * 6; // as seconds
          var beneficial = int.Parse(data[4], CultureInfo.InvariantCulture);
          var target = byte.Parse(data[6], CultureInfo.InvariantCulture);
          var classMask = ushort.Parse(data[7], CultureInfo.InvariantCulture);

          // deal with too big or too small values
          // all adps we care about is in the range of a few minutes
          if (duration > ushort.MaxValue)
          {
            duration = ushort.MaxValue;
          }
          else if (duration < 0)
          {
            duration = 0;
          }

          var level = byte.Parse(data[2], CultureInfo.InvariantCulture);

          spellData = new SpellData
          {
            Id = string.Intern(data[0]),
            Name = string.Intern(data[1]),
            NameAbbrv = string.Intern(AbbreviateSpellName(data[1])),
            Level = level,
            Duration = (ushort)duration,
            IsBeneficial = beneficial != 0,
            Target = target,
            MaxHits = ushort.Parse(data[5], CultureInfo.InvariantCulture),
            ClassMask = classMask,
            Damaging = short.Parse(data[8], CultureInfo.InvariantCulture),
            //CombatSkill = uint.Parse(data[9], CultureInfo.InvariantCulture),
            Resist = (SpellResist)int.Parse(data[10], CultureInfo.InvariantCulture),
            SongWindow = data[11] == "1" || data[11] == "-1",
            Adps = byte.Parse(data[12], CultureInfo.InvariantCulture),
            Mgb = data[13] == "1",
            Rank = byte.Parse(data[14], CultureInfo.InvariantCulture),
            HasAmbiguity = data[15] == "1" || data[16] == "1",
            LandsOnYou = string.Intern(data[17]),
            LandsOnOther = string.Intern(data[18]),
            WearOff = string.Intern(data[19]),
            // Cols 20 and 21 were added to the parser format in 1.1.3. Older
            // spells.txt files without these fields fall back to 0 (instant-cast,
            // no recast lockout), matching pre-extension behavior.
            CastingTimeMs = data.Length > 20 ? uint.Parse(data[20], CultureInfo.InvariantCulture) : 0,
            RecastTimeMs = data.Length > 21 ? uint.Parse(data[21], CultureInfo.InvariantCulture) : 0,
            Category = data.Length > 22 ? string.Intern(data[22]) : string.Empty,
            // Cols 23-25 (Skill/RecourseID/TimerID) are a later parser-format
            // extension. Skill is signed (-1 = no skill), so parse as int. Older
            // spells.txt files without these fields fall back to 0.
            Skill = data.Length > 23 ? int.Parse(data[23], CultureInfo.InvariantCulture) : 0,
            RecourseID = data.Length > 24 ? int.Parse(data[24], CultureInfo.InvariantCulture) : 0,
            TimerID = data.Length > 25 ? int.Parse(data[25], CultureInfo.InvariantCulture) : 0,
            // Cols 26-28 (Mana/Range/ResistMod) added after v1.2.0. Default to 0
            // on older spells.txt files.
            Mana = data.Length > 26 ? int.Parse(data[26], CultureInfo.InvariantCulture) : 0,
            Range = data.Length > 27 ? int.Parse(data[27], CultureInfo.InvariantCulture) : 0,
            ResistMod = data.Length > 28 ? int.Parse(data[28], CultureInfo.InvariantCulture) : 0,
            // Col 29 (IconId) added alongside spell-descriptions.json sidecar. Defaults
            // to 0 (no icon) on older spells.txt files.
            IconId = data.Length > 29 ? int.Parse(data[29], CultureInfo.InvariantCulture) : 0,
            Description = _spellDescriptions.TryGetValue(string.Intern(data[0]), out var desc) ? desc : string.Empty
          };
        }
      }

      return spellData;
    }

    private static SpellData FindPreviousCast(string player, IEnumerable<SpellData> output, bool isAdps = false)
    {
      SpellData[] filtered = null;
      foreach (var (_, cast) in RecordsStore.Instance.GetSpellsLast(8))
      {
        if (!cast.Interrupted)
        {
          filtered ??= [.. output.Where(value => !isAdps || value.Adps > 0)];
          foreach (var value in filtered)
          {
            if ((value.Target != (int)SpellTarget.Self || cast.Caster == player) && value.Name == cast.Spell)
            {
              return value;
            }
          }
        }
      }
      return null;
    }

    private static List<SpellData> FindByLandsOn(string player, List<SpellData> output)
    {
      List<SpellData> result = null;
      if (output.Count == 1)
      {
        result = output;
      }
      else if (output.Count > 1)
      {
        var foundSpellData = FindPreviousCast(player, output);
        if (foundSpellData == null)
        {
          // one more thing, if all the abbreviations look the same then we know the spell
          // even if the version is wrong. grab the newest
          result = (output.Distinct(AbbrvComparer).Count() == 1) ? [output.First()] : output;
        }
        else
        {
          result = [foundSpellData];
        }
      }

      return result;
    }
    public void Clear(bool serverChanged = true)
    {
      foreach (var spellData in _allSpellData)
      {
        spellData.SeenRecently = false;
      }
      _unknownSpellDb.Clear();
    }

    public void Shutdown() => Clear();

    internal static bool ResolveSpellAmbiguity(ReceivedSpell spell, double currentTime, out SpellData replaced)
    {
      replaced = null;

      var className = PlayerRegistry.Instance.GetPlayerClass(spell.Receiver, currentTime);
      var spellClass = (int)Instance.GetClassEnum(className);
      var subset = spell.Ambiguity.FindAll(test => test.Target == (int)SpellTarget.Self && spellClass != 0 && (test.ClassMask & spellClass) == spellClass);
      var distinct = subset.Distinct(AbbrvComparer).ToList();
      if (distinct.Count == 1)
      {
        replaced = distinct.First();
      }
      else
      {
        var recent = spell.Ambiguity.FirstOrDefault(spellData => spellData.SeenRecently);
        replaced = recent ?? spell.Ambiguity.First();
      }

      return replaced != null;
    }

    /// <summary>
    /// Searches for a spell path in the provided spell tree.
    /// </summary>
    public static SpellTreeResult SearchSpellPath(SpellTreeNode node, string[] split, int lastIndex = -1)
    {
      if (lastIndex == -1)
      {
        lastIndex = split.Length - 1;
      }

      if (node.Words.TryGetValue(split[lastIndex], out var child))
      {
        if (lastIndex > 0)
        {
          return SearchSpellPath(child, split, lastIndex - 1);
        }

        return new SpellTreeResult { SpellData = child.SpellData, DataIndex = lastIndex };
      }

      return new SpellTreeResult { SpellData = node.SpellData, DataIndex = lastIndex };
    }

    private static void BuildSpellPath(IReadOnlyList<string> data, SpellTreeNode node, SpellData spellData, int lastIndex = -1)
    {
      if (lastIndex == -1)
      {
        lastIndex = data.Count - 1;
      }

      if (data[lastIndex] == "'s")
      {
        node.SpellData.Add(spellData);
        node.SpellData.Sort(EQDataUtil.SpellDurationCompare);
      }
      else
      {
        if (!node.Words.TryGetValue(data[lastIndex], out var child))
        {
          child = new SpellTreeNode();
          node.Words[data[lastIndex]] = child;
        }

        if (lastIndex == 0)
        {
          child.SpellData.Add(spellData);
          child.SpellData.Sort(EQDataUtil.SpellDurationCompare);
        }
        else
        {
          BuildSpellPath(data, child, spellData, lastIndex - 1);
        }
      }
    }
  }
}