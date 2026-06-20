using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingBreakdown.xaml
  /// </summary>
  public partial class TankingBreakdown
  {
    public TankingBreakdown()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    private void SpellDetailsClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is not PlayerSubStats sub || string.IsNullOrEmpty(sub.Name)) return;
      var spell = EQDataStore.Instance.GetSpellByAbbrv(sub.Name);
      if (spell == null) return;
      if (SyncFusionUtil.OpenWindow(out var win, typeof(SpellDetailsPopup), "spellDetailsWindow", "Spell Details")
          && win.Content is SpellDetailsPopup viewer) viewer.Init(spell);
    }

    private void CompareSpellsClick(object sender, RoutedEventArgs e)
    {
      var spells = dataGrid.SelectedItems
        .OfType<PlayerSubStats>()
        .Select(s => EQDataStore.Instance.GetSpellByAbbrv(s.Name))
        .Where(s => s != null)
        .Distinct()
        .ToList();
      if (spells.Count < 2) return;
      if (SyncFusionUtil.OpenWindow(out var win, typeof(SpellTableWindow), "spellTableWindow", "Compare Spells")
          && win.Content is SpellTableWindow tv) tv.Init(spells, compareMode: true);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      Task.Delay(100).ContinueWith(_ =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          titleLabel.Content = currentStats?.ShortTitle;
          dataGrid.ItemsSource = selectedStats;
          dataGrid.IsEnabled = true;
          UiElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }
  }
}
