using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace EQLogParser
{
  public partial class SpellBrowserWindow
  {
    private const string AllClasses    = "All Classes";
    private const string AllCategories = "All";
    private const string AnyEffect     = "Any Effect";
    private const string AnySlot       = "Any Slot";

    private readonly List<SpellData> _allSpells;
    private bool _loaded;
    private bool _resetting;

    private record SpellBrowserRow(SpellData Spell)
    {
      public string Name     => Spell.Name;
      public int    Level    => Spell.Level == 255 ? 0 : Spell.Level;
      public string Classes  => SpellDecoder.DecodeClasses(Spell.ClassMask, Spell.Level, showLevel: false);
      public int    Mana     => Spell.Mana;
      public int    Range    => Spell.Range;
      public string Skill    => SpellDecoder.DecodeSkill(Spell.Skill);
      public string Target   => SpellDecoder.DecodeTarget(Spell.Target);
      public string Resist   => SpellDecoder.DecodeResist(Spell.Resist);
      public string Category => Spell.Category?.Replace(";", ", ") ?? "";
    }

    // Displayed in the Has Effect combo as "N — Short Name".
    private sealed record SpaOption(int Spa)
    {
      public override string ToString() => $"{Spa} — {SpellDecoder.GetSpaName(Spa)}";
    }

    public SpellBrowserWindow()
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();

      _allSpells = [.. EQDataStore.Instance.GetSpellsForBrowser()];

      classFilter.Items.Add(AllClasses);
      foreach (var className in EQDataStore.Instance.GetClassList())
      {
        classFilter.Items.Add(className);
      }
      SelectDefaultClass();

      categoryFilter.Items.Add(AllCategories);
      foreach (var category in EQDataStore.Instance.GetAllCategories())
      {
        categoryFilter.Items.Add(category);
      }
      categoryFilter.SelectedIndex = 0;

      typeFilter.Items.Add("All");
      typeFilter.Items.Add("Beneficial");
      typeFilter.Items.Add("Detrimental");
      typeFilter.SelectedIndex = 0;

      hasSpaFilter.Items.Add(AnyEffect);
      foreach (var spa in EQDataStore.Instance.GetUsedSpas())
      {
        hasSpaFilter.Items.Add(new SpaOption(spa));
      }
      hasSpaFilter.SelectedIndex = 0;

      inSlotFilter.Items.Add(AnySlot);
      for (var i = 1; i <= 12; i++)
      {
        inSlotFilter.Items.Add($"Slot {i}");
      }
      inSlotFilter.SelectedIndex = 0;
    }

    private void SelectDefaultClass()
    {
      var playerClass = PlayerRegistry.Instance.GetDefaultPlayerClass(ConfigUtil.PlayerName);
      classFilter.SelectedItem = !string.IsNullOrEmpty(playerClass) && classFilter.Items.Contains(playerClass)
        ? playerClass
        : AllClasses;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      _loaded = true;
      ApplyFilter();
      searchBox.Focus();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
      if (_loaded && !_resetting)
      {
        ApplyFilter();
      }
    }

    private void ApplyFilter()
    {
      var text = searchBox.Text;
      var cls  = classFilter.SelectedItem as string;
      var cat  = categoryFilter.SelectedItem as string;
      var type = typeFilter.SelectedItem as string;

      var classFlag = (cls == AllClasses || string.IsNullOrEmpty(cls))
        ? (SpellClass?)null
        : EQDataStore.Instance.GetSpellClassByName(cls);

      var hasMin = int.TryParse(levelMin.Text, out var minLevel);
      var hasMax = int.TryParse(levelMax.Text, out var maxLevel);
      var filterLevel = hasMin || hasMax;

      var spaOption = hasSpaFilter.SelectedItem as SpaOption;
      var slotStr   = inSlotFilter.SelectedItem as string;
      // "Slot N" → 0-based index; AnySlot → -1 (no slot filter)
      var slotIdx = slotStr != null && slotStr != AnySlot
        ? int.Parse(slotStr.Split(' ')[1]) - 1
        : -1;

      var query = _allSpells.AsEnumerable();

      if (classFlag is { } cf)
      {
        query = query.Where(s => (s.ClassMask & (int)cf) != 0);
      }

      if (!string.IsNullOrEmpty(cat) && cat != AllCategories)
      {
        query = query.Where(s => !string.IsNullOrEmpty(s.Category) &&
          s.Category.Split(';').Contains(cat, StringComparer.OrdinalIgnoreCase));
      }

      if (type == "Beneficial")
      {
        query = query.Where(s => s.IsBeneficial);
      }
      else if (type == "Detrimental")
      {
        query = query.Where(s => !s.IsBeneficial);
      }

      if (!string.IsNullOrWhiteSpace(text))
      {
        var term = text.Trim();
        query = query.Where(s => s.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
      }

      if (filterLevel)
      {
        query = query.Where(s =>
        {
          var displayLevel = s.Level == 255 ? 0 : (int)s.Level;
          if (hasMin && displayLevel < minLevel) return false;
          if (hasMax && displayLevel > maxLevel) return false;
          return true;
        });
      }

      if (spaOption != null || slotIdx >= 0)
      {
        var targetSpa  = spaOption?.Spa;
        var targetSlot = slotIdx;
        query = query.Where(s =>
        {
          var effects = EQDataStore.Instance.GetSpellEffects(s.Id);
          if (effects?.Slots == null)
          {
            return false;
          }
          // Both set: the named SPA must appear in that specific slot.
          if (targetSpa.HasValue && targetSlot >= 0)
          {
            return effects.Slots.Any(e => e.Spa == targetSpa.Value && e.Slot == targetSlot);
          }
          if (targetSpa.HasValue)
          {
            return effects.Slots.Any(e => e.Spa == targetSpa.Value);
          }
          return effects.Slots.Any(e => e.Slot == targetSlot);
        });
      }

      var rows = query.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                      .Select(s => new SpellBrowserRow(s))
                      .ToList();
      dataGrid.ItemsSource = rows;
      countLabel.Text = $"{rows.Count:N0} spells";
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
      _resetting = true;
      searchBox.Clear();
      levelMin.Clear();
      levelMax.Clear();
      SelectDefaultClass();
      categoryFilter.SelectedIndex  = 0;
      typeFilter.SelectedIndex      = 0;
      hasSpaFilter.SelectedIndex    = 0;
      inSlotFilter.SelectedIndex    = 0;
      _resetting = false;
      ApplyFilter();
    }

    private void OnCellDoubleTapped(object sender, GridCellDoubleTappedEventArgs e)
    {
      if (dataGrid.SelectedItem is SpellBrowserRow row &&
          SyncFusionUtil.OpenWindow(out var win, typeof(SpellDetailsPopup), "spellDetailsWindow", "Spell Details") &&
          win.Content is SpellDetailsPopup viewer)
      {
        viewer.Init(row.Spell);
      }
    }
  }
}
