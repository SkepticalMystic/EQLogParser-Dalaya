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

    private readonly List<SpellData> _allSpells;
    private bool _loaded;

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
      if (_loaded)
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

      var rows = query.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                      .Select(s => new SpellBrowserRow(s))
                      .ToList();
      dataGrid.ItemsSource = rows;
      countLabel.Text = $"{rows.Count:N0} spells";
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
