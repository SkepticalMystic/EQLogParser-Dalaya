using System.Collections.Generic;
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
