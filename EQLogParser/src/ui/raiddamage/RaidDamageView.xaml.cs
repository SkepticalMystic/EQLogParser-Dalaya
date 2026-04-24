using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class RaidDamageView : UserControl, IDocumentContent
  {
    private readonly ObservableCollection<RaidDamageSource> _sources = [];
    private readonly ObservableCollection<MergedFightRow> _fights = [];

    // Dedicated isolated context used to run BuildTotalStats for rendering merged-fight DPS.
    // Kept separate from per-source parse contexts so stats computation state doesn't get tangled.
    private readonly ParseContext _statsContext = ParseContext.CreateIsolated();

    // Embedded DamageSummary bound to the isolated DamageStatsManager. It owns its own
    // event subscriptions (via ContentLoaded) and renders the full tree grid, context menu,
    // pet rollups, column chooser, etc. — identical to the main DPS Summary tab.
    private readonly DamageSummary _damageSummary;

    public RaidDamageView()
    {
      InitializeComponent();
      sourcesGrid.ItemsSource = _sources;
      fightsGrid.ItemsSource = _fights;

      _damageSummary = new DamageSummary(
        _statsContext.DamageStatsManager,
        new RaidDamageHost(_statsContext.DataManager),
        "RaidDamageSummaryColumns");
      summaryHost.Content = _damageSummary;

      UpdateFooterStatus();
      RebuildMergedFights();
    }

    // The DockingManager calls this when the tab is closed. Forwarding to the embedded
    // DamageSummary tears down its event subscriptions (EventsGenerationStatus,
    // EventsClearedActiveData via the isolated DataManager, MainActions options-changed).
    public void HideContent()
    {
      _damageSummary?.HideContent();
    }

    private async void AddSourceClick(object sender, RoutedEventArgs e)
    {
      var dialog = new OpenFileDialog
      {
        Filter = "EQ Log files (eqlog_*.txt)|eqlog_*.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        Title = "Add Log Source"
      };

      if (dialog.ShowDialog() != true)
      {
        return;
      }

      var filePath = dialog.FileName;
      var sourcePlayer = FightMerger.TryParsePlayerNameFromLogFile(filePath);
      if (string.IsNullOrEmpty(sourcePlayer))
      {
        new MessageWindow(
          $"Could not extract a source player name from the filename.\n\nExpected format: eqlog_PlayerName_server.txt\n\nFile: {Path.GetFileName(filePath)}",
          "Raid Damage").ShowDialog();
        return;
      }

      if (_sources.Any(s => string.Equals(s.SourcePlayer, sourcePlayer, StringComparison.Ordinal)))
      {
        new MessageWindow($"A source for {sourcePlayer} is already loaded.", "Raid Damage").ShowDialog();
        return;
      }

      var source = new RaidDamageSource
      {
        FilePath = filePath,
        SourcePlayer = sourcePlayer,
        IsSelected = true,
        Context = ParseContext.CreateIsolated()
      };
      source.StatusText = "(parsing...)";

      source.Context.DataManager.EventsNewFight += (_, fight) => source.AddFight(fight);

      _sources.Add(source);
      UpdateFooterStatus();

      try
      {
        await Task.Run(() => ParseFile(source));
        source.StatusText = $"({source.FightCount} fights, {FormatDamage(source.TotalDamage)})";
      }
      catch (Exception ex)
      {
        source.StatusText = "(parse failed)";
        new MessageWindow($"Failed to parse {Path.GetFileName(filePath)}:\n{ex.Message}", "Raid Damage").ShowDialog();
      }

      RebuildMergedFights();
      UpdateFooterStatus();
    }

    private static void ParseFile(RaidDamageSource source)
    {
      var processor = new LogProcessor(source.FilePath, source.Context);
      using var fs = new FileStream(source.FilePath, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
      using var reader = new StreamReader(fs);
      string line;
      while ((line = reader.ReadLine()) != null)
      {
        if (line.Length < 28)
        {
          continue;
        }
        var dt = DateUtil.ParseStandardDate(line);
        if (dt == DateTime.MinValue)
        {
          continue;
        }
        processor.ProcessSync(line, DateUtil.ToDouble(dt));
      }
    }

    private void RemoveSourceClick(object sender, RoutedEventArgs e)
    {
      var selected = sourcesGrid.SelectedItems?.Cast<RaidDamageSource>().ToList();
      if (selected == null || selected.Count == 0)
      {
        return;
      }

      foreach (var source in selected)
      {
        _sources.Remove(source);
      }

      RebuildMergedFights();
      UpdateFooterStatus();
    }

    private void SourcesSelectionChanged(object sender, Syncfusion.UI.Xaml.Grid.GridSelectionChangedEventArgs e)
    {
      removeSourceButton.IsEnabled = sourcesGrid.SelectedItems is { Count: > 0 };
    }

    private void SourceCheckboxClick(object sender, RoutedEventArgs e)
    {
      Dispatcher.BeginInvoke(() =>
      {
        RebuildMergedFights();
        UpdateFooterStatus();
      });
    }

    private void FightsSelectionChanged(object sender, Syncfusion.UI.Xaml.Grid.GridSelectionChangedEventArgs e)
    {
      UpdatePerPlayerSummary();
    }

    // Runs the merger and repopulates the middle fights grid. Also refreshes the right pane
    // (with no fights selected, the summary treats "all fights" as the scope).
    private void RebuildMergedFights()
    {
      _fights.Clear();

      var selectedSources = _sources.Where(s => s.IsSelected && s.Fights.Count > 0).ToList();
      if (selectedSources.Count == 0)
      {
        fightsTitle.Text = _sources.Count == 0
          ? "Merged Fights — No Sources Loaded"
          : "Merged Fights — No Sources Selected";
        UpdatePerPlayerSummary();
        return;
      }

      var fightSources = selectedSources
        .Select(s => new FightSource { SourcePlayer = s.SourcePlayer, Fights = s.Fights })
        .ToList();
      var merged = FightMerger.MergeFromSources(fightSources);

      // Match the Fight List's default (Tanking unchecked): only fights with at least one
      // player-damage hit. Otherwise pure-tanking encounters (NPCs hit you, you didn't hit
      // back) inflate the count and don't contribute to a DPS merge anyway.
      foreach (var f in merged.Where(f => f.DamageHits > 0).OrderByDescending(f => f.BeginTime))
      {
        _fights.Add(new MergedFightRow(f));
      }

      fightsTitle.Text = $"Merged Fights — {_fights.Count} ({selectedSources.Count} source{(selectedSources.Count == 1 ? "" : "s")})";
      UpdatePerPlayerSummary();
    }

    // Called whenever fight selection changes, or after a merger rebuild. If no fights are
    // selected, the pane stays blank (matching DPS Summary's "No NPCs Selected" default).
    private void UpdatePerPlayerSummary()
    {
      var selectedFights = fightsGrid.SelectedItems?.Cast<MergedFightRow>().Select(r => r.Fight).ToList();

      if (selectedFights is null or { Count: 0 })
      {
        // BuildTotalStats with no Npcs drives the embedded DamageSummary to the "No NPCs"
        // state via the NONPC event path. No need to touch the control directly.
        Task.Run(() => _statsContext.DamageStatsManager.BuildTotalStats(new GenerateStatsOptions()));
        mergeStatus.Text = "";
        return;
      }

      // Assign unique Ids so DamageStatsManager can rank/group fights. Merged Fight objects
      // start with Id=0 which causes hashing/equality collisions in the stats computation.
      var id = 1;
      foreach (var f in selectedFights) { f.Id = id++; }

      var options = new GenerateStatsOptions();
      options.Npcs.AddRange(selectedFights);
      options.AllRanges = new TimeRange();
      foreach (var f in selectedFights)
      {
        if (!double.IsNaN(f.BeginDamageTime) && !double.IsNaN(f.LastDamageTime))
        {
          options.AllRanges.Add(new TimeSegment(f.BeginDamageTime, f.LastDamageTime));
        }
      }

      // Re-seed from the live PlayerManager. _statsContext is constructed at app startup
      // (docking manager creates the view eagerly), so its PlayerManager snapshot misses any
      // pet→owner mappings that accumulated during the current live session. Without this,
      // DamageStatsManager.UpdatePetMapping fails to find owners and pets show as their own
      // top-level rows instead of rolling into "OwnerName +Pets" aggregates.
      _statsContext.PlayerManager.SeedFrom(PlayerManager.Instance);

      // Run on background thread — BuildTotalStats does meaningful work and fires events
      // that route through the embedded DamageSummary's subscription.
      Task.Run(() => _statsContext.DamageStatsManager.BuildTotalStats(options));

      mergeStatus.Text = $"{selectedFights.Count} fight{(selectedFights.Count == 1 ? "" : "s")} selected";
    }

    private void UpdateFooterStatus()
    {
      var selectedCount = _sources.Count(s => s.IsSelected);
      sourcesStatus.Text = _sources.Count == 0
        ? "No sources. Click Add to load an exported log."
        : $"{_sources.Count} source{(_sources.Count == 1 ? "" : "s")} • {selectedCount} selected";
    }

    private static string FormatDamage(long damage)
    {
      if (damage >= 1_000_000_000) return $"{damage / 1_000_000_000.0:F2}B";
      if (damage >= 1_000_000) return $"{damage / 1_000_000.0:F2}M";
      if (damage >= 1_000) return $"{damage / 1_000.0:F1}K";
      return damage.ToString();
    }
  }

  internal class MergedFightRow
  {
    public Fight Fight { get; }
    public string BeginTimeString => Fight.BeginTimeString;
    public string Name => Fight.Name;
    public long Damage => Fight.DamageTotal;

    public string DamageString
    {
      get
      {
        var d = Damage;
        if (d >= 1_000_000_000) return $"{d / 1_000_000_000.0:F2}B";
        if (d >= 1_000_000) return $"{d / 1_000_000.0:F2}M";
        if (d >= 1_000) return $"{d / 1_000.0:F1}K";
        return d.ToString();
      }
    }

    public string DurationString
    {
      get
      {
        if (double.IsNaN(Fight.BeginDamageTime) || double.IsNaN(Fight.LastDamageTime)) return "";
        var s = (int)Math.Round(Fight.LastDamageTime - Fight.BeginDamageTime);
        if (s < 0) s = 0;
        if (s >= 60) return $"{s / 60}m {s % 60}s";
        return $"{s}s";
      }
    }

    public MergedFightRow(Fight fight)
    {
      Fight = fight;
    }
  }

  internal class RaidDamageSource : INotifyPropertyChanged
  {
    public string FilePath { get; set; }
    public string SourcePlayer { get; set; }
    public ParseContext Context { get; set; }
    public List<Fight> Fights { get; } = [];

    private bool _isSelected;
    public bool IsSelected
    {
      get => _isSelected;
      set { _isSelected = value; OnPropertyChanged(); }
    }

    private string _statusText = "";
    public string StatusText
    {
      get => _statusText;
      set { _statusText = value; OnPropertyChanged(); }
    }

    public int FightCount => Fights.Count;
    public long TotalDamage => Fights.Sum(f => f.DamageTotal);

    internal void AddFight(Fight fight)
    {
      Fights.Add(fight);
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
  }

}
