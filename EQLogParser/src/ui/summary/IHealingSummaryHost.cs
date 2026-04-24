using System;

namespace EQLogParser
{
  // Abstracts the globals that HealingSummary reaches into for cross-window coordination.
  // Mirror of IDamageSummaryHost / ITankingSummaryHost for the healing view.
  //
  // Healing-specific coupling vs damage/tanking:
  //   - MainActions.EventsHealingSummaryOptionsChanged (options/filter toggle event)
  //   - MainActions.FireHealingSelectionChanged
  //   - HealingSummary doesn't subscribe to EventsHealingSelectionChanged — it only fires
  //     selection changes, so the host interface has no subscription for that event.
  internal interface IHealingSummaryHost
  {
    event Action<bool> EventsClearedActiveData;
    event Action<string> EventsChartOpened;
    event Action<string> EventsHealingSummaryOptionsChanged;
    void FireHealingSelectionChanged(PlayerStatsSelectionChangedEventArgs args);
    void CopyToEqClick(string label);
  }

  // Default host for the main Healing Summary tab — wires the live singletons.
  internal sealed class MainActionsHealingHost : IHealingSummaryHost
  {
    public event Action<bool> EventsClearedActiveData
    {
      add => DataManager.Instance.EventsClearedActiveData += value;
      remove => DataManager.Instance.EventsClearedActiveData -= value;
    }

    public event Action<string> EventsChartOpened
    {
      add => MainActions.EventsChartOpened += value;
      remove => MainActions.EventsChartOpened -= value;
    }

    public event Action<string> EventsHealingSummaryOptionsChanged
    {
      add => MainActions.EventsHealingSummaryOptionsChanged += value;
      remove => MainActions.EventsHealingSummaryOptionsChanged -= value;
    }

    public void FireHealingSelectionChanged(PlayerStatsSelectionChangedEventArgs args)
      => MainActions.FireHealingSelectionChanged(args);

    public void CopyToEqClick(string label) => MainActions.CopyToEqClick(label);
  }

  // Host for a HealingSummary embedded inside the Raid Damage window. Routes clear-active-data
  // onto the isolated DataManager, forwards options-changed so global healing filter toggles
  // rebuild both views, and no-ops the chart/selection/copy paths that only make sense for
  // the "current parse".
  internal sealed class RaidDamageHealingHost : IHealingSummaryHost
  {
    private readonly DataManager _dataManager;

    public RaidDamageHealingHost(DataManager dataManager)
    {
      _dataManager = dataManager;
    }

    public event Action<bool> EventsClearedActiveData
    {
      add => _dataManager.EventsClearedActiveData += value;
      remove => _dataManager.EventsClearedActiveData -= value;
    }

    public event Action<string> EventsChartOpened { add { } remove { } }

    public event Action<string> EventsHealingSummaryOptionsChanged
    {
      add => MainActions.EventsHealingSummaryOptionsChanged += value;
      remove => MainActions.EventsHealingSummaryOptionsChanged -= value;
    }

    public void FireHealingSelectionChanged(PlayerStatsSelectionChangedEventArgs args) { }

    public void CopyToEqClick(string label) { }
  }
}
