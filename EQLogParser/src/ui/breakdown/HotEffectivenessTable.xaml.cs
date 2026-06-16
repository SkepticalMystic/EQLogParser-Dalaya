using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  // Phase 3 V1 of project_dot_hot_validation: per-spell HoT effectiveness for a
  // single player, scoped to the active fight selection. Mirrors HitLogViewer's
  // pattern of accepting CurrentGroups so totals match the Healing Summary.
  //
  // Columns:
  //   Spell      — spell name from the heal log
  //   Ticks      — observed HoT tick count
  //   Total Heal — sum of healing across all ticks
  //   Avg Heal   — average per tick
  //   Max Heal   — largest single tick (usually a crit)
  //   Crits      — ticks carrying the Crit modifier
  //   Crit %     — crit rate
  //   Base Heal  — spell-data per-tick base value (hidden by default; toggle via
  //                right-click "Show Base Heal"). Useful for patch drift detection.
  //   Amplifier  — Avg Heal / Base Heal; captures the multiplicative stack of
  //                focus effects, AAs, Power Bonus, casting/proficiency skills,
  //                and crit averaging.
  //
  // Spells with no spell-effects.json entry show "—" in Base Heal and Amplifier.
  // Per-cast dropped-tick metrics are deferred (need cast-instance linkage
  // shared with project_dd_attribution).
  public partial class HotEffectivenessTable : UserControl
  {
    private GridColumn _baseHealCol;

    public HotEffectivenessTable()
    {
      InitializeComponent();
      BuildColumns();
    }

    private void BuildColumns()
    {
      AddSpellNameColumn("SpellName", ThemeConfig.CurrentSpellWidth);

      AddNumeric("TickCount", "Ticks",
        "Number of HoT ticks observed within the active fight selection.",
        decimalDigits: 0);

      AddNumeric("TotalHealing", "Total Heal",
        "Sum of healing from this spell's ticks across the active fight selection.",
        decimalDigits: 0);

      AddNumeric("AverageObserved", "Avg Heal",
        "Average healing per observed tick, across crit and non-crit ticks.",
        decimalDigits: 1);

      AddText("NonCritAvgDisplay", "Non-Crit Avg",
        "Average heal per non-crit tick — the consistent per-tick value for the caster's current amplifier stack, with crit randomness removed. For flat-base HoTs (the typical Dalaya case) this should be roughly constant across all non-crit ticks of a given spell.",
        TextAlignment.Right);

      AddNumeric("MaxHeal", "Max Heal",
        "Largest single tick observed — typically a critical heal.",
        decimalDigits: 0);

      AddNumeric("CritCount", "Crits",
        "Number of ticks that critted (carry the Crit modifier).",
        decimalDigits: 0);

      AddText("CritRateDisplay", "Crit %",
        "Percentage of observed ticks that critted.",
        TextAlignment.Right);

      AddText("ExpectedDisplay", "Base Heal",
        "Spell-data base per-tick value, before any modifiers (focus effects, AAs, Power Bonus, casting/proficiency skills, crit). Druid HoTs tick every 2s on Dalaya; others tick every 6s.",
        TextAlignment.Right);
      _baseHealCol = dataGrid.Columns[^1];
      _baseHealCol.IsHidden = true;

      AddText("RatioDisplay", "Amplifier",
        "Avg Heal / Base Heal. Captures the multiplicative stack of focus effects, AAs, Power Bonus, casting/proficiency skills, and crit averaging. Consistent ratios across a player's spells suggest the spell-data baselines are correct; an outlier may indicate a spells.txt mismap or a Dalaya mechanic the formula port doesn't handle.",
        TextAlignment.Right);
    }

    private void AddText(string mapping, string header, string tooltip, TextAlignment align = TextAlignment.Left, double? width = null)
    {
      var col = new GridTextColumn
      {
        MappingName = mapping,
        HeaderText = header,
        TextAlignment = align,
        HeaderTemplate = HeaderTemplateWithTooltip(header, tooltip)
      };
      if (width.HasValue) col.Width = width.Value;
      dataGrid.Columns.Add(col);
    }

    // Spell-name column: template column with 800ms hover tooltip showing decoded spell effects.
    private void AddSpellNameColumn(string mapping, double? width = null)
    {
      var converter = new SpellNameTooltipConverter();
      var tb = new FrameworkElementFactory(typeof(TextBlock));
      tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
      tb.SetBinding(TextBlock.TextProperty, new Binding(mapping));
      tb.SetBinding(FrameworkElement.ToolTipProperty, new Binding(mapping) { Converter = converter });
      tb.SetValue(ToolTipService.InitialShowDelayProperty, 800);
      tb.SetValue(ToolTipService.ShowDurationProperty, 15000);

      var col = new GridTemplateColumn
      {
        MappingName = mapping,
        HeaderText = "Spell",
        HeaderTemplate = HeaderTemplateWithTooltip("Spell", "Spell name as recorded in the heal log."),
        CellTemplate = new DataTemplate { VisualTree = tb }
      };
      if (width.HasValue) col.Width = width.Value;
      dataGrid.Columns.Add(col);
    }

    private void AddNumeric(string mapping, string header, string tooltip, int decimalDigits)
    {
      dataGrid.Columns.Add(new GridNumericColumn
      {
        MappingName = mapping,
        HeaderText = header,
        TextAlignment = TextAlignment.Right,
        NumberDecimalDigits = decimalDigits,
        NumberGroupSizes = [3],
        HeaderTemplate = HeaderTemplateWithTooltip(header, tooltip)
      });
    }

    // Header template that preserves the displayed header text but adds an
    // explanatory tooltip on hover. Built programmatically so each column can
    // bind its own tooltip text without per-column XAML.
    private void BaseHealToggleClick(object sender, RoutedEventArgs e)
    {
      if (_baseHealCol == null) return;
      _baseHealCol.IsHidden = !_baseHealCol.IsHidden;
      baseHealToggle.Header = _baseHealCol.IsHidden ? "Show Base Heal" : "Hide Base Heal";
    }

    private static DataTemplate HeaderTemplateWithTooltip(string headerText, string tooltipText)
    {
      var tb = new FrameworkElementFactory(typeof(TextBlock));
      tb.SetValue(TextBlock.TextProperty, headerText);
      tb.SetValue(TextBlock.ToolTipProperty, tooltipText);
      tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
      return new DataTemplate { VisualTree = tb };
    }

    // Initialize from a selected PlayerStats + the active fight selection's
    // ActionGroups. Iterating groups (rather than RecordsStore.GetAllHeals) is
    // what scopes the table to the selected fight(s); without this, the table
    // would show whole-log totals while the Healing Summary above shows
    // selected-fight totals. Pattern mirrors HitLogViewer.InitAsync.
    //
    // Caster level defaults to 65 (Dalaya cap). For Calc 100 HoTs (currently the
    // only formula in shipping Dalaya HoT data) level is irrelevant; if level-
    // scaled HoTs appear in a future patch, this will need to plumb a real level.
    internal void Init(PlayerStats playerStats, List<List<ActionGroup>> groups)
    {
      if (playerStats == null) return;

      titleLabel.Content = $"HoT Effectiveness — {playerStats.Name}";

      var dataStore = EQDataStore.Instance;
      var casterClass = dataStore.GetClassEnum(playerStats.ClassName) ?? 0;
      const byte casterLevel = 65;

      var healRecords = (groups ?? [])
        .SelectMany(fight => fight ?? [])
        .SelectMany(actionGroup => actionGroup?.Actions ?? Enumerable.Empty<IAction>())
        .OfType<HealRecord>();

      var rows = HotEffectivenessAggregator.Aggregate(healRecords, playerStats.OrigName ?? playerStats.Name,
        casterLevel, casterClass, dataStore);

      dataGrid.ItemsSource = rows
        .Select(r => new
        {
          r.SpellName,
          r.TickCount,
          r.TotalHealing,
          r.AverageObserved,
          NonCritAvgDisplay = r.NonCritAverage.HasValue ? r.NonCritAverage.Value.ToString("N1") : "—",
          r.MaxHeal,
          r.CritCount,
          CritRateDisplay = $"{r.CritRate * 100:F1}%",
          ExpectedDisplay = r.ExpectedPerTick?.ToString("N0") ?? "—",
          RatioDisplay = r.Ratio.HasValue ? $"{r.Ratio.Value:F2}x" : "—"
        })
        .ToList();
    }
  }
}
