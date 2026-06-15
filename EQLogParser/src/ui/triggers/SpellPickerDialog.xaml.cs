using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace EQLogParser
{
  // Trigger spell picker (project-trigger-spell-picker Phases 1-5). Lets the user
  // browse player-castable spells and either insert one of the spell's log messages
  // into the trigger Pattern field (insert mode) or fully configure the current
  // trigger as a buff/HoT/DoT duration tracker (bundle mode). Launched from
  // PatternEditor's "Spell..." button. Filters mirror the Spell Browser (P5).
  public partial class SpellPickerDialog
  {
    private const string AllClasses    = "All Classes";
    private const string AllCategories = "All";
    private const string AllTypes      = "All";
    private const string AnyEffect     = "Any Effect";
    private const string AnySlot       = "Any Slot";
    private const string NoOverlay     = "No Timer Overlay";

    // Index-aligned with the messageField combo and GetFieldText below.
    private static readonly string[] MessageFieldLabels = ["Lands on You", "Lands on Other", "Wear Off"];

    // Insert mode: the chosen message text, valid when IsOkClicked && BundleResult == null.
    public string SelectedText { get; private set; }
    public bool IsOkClicked { get; private set; }

    // Bundle mode (P4/P5): all fields needed to configure the current trigger.
    // Non-null only when the user clicked Generate in bundle mode.
    public record TriggerBundle(
      string Pattern,
      bool UseRegex,
      string PreviousPattern,
      int TimerType,
      double DurationSeconds,
      string EndEarlyPattern,
      string EndEarlyPattern2,
      string AltTimerName,
      string OverlayId
    );

    public TriggerBundle BundleResult { get; private set; }

    private readonly List<SpellData> _allSpells;
    private readonly bool _enableBundleMode;
    private bool _loaded;
    private bool _resetting;

    private sealed record SpaOption(int Spa)
    {
      public override string ToString() => $"{Spa} — {SpellDecoder.GetSpaName(Spa)}";
    }

    private sealed record OverlayOption(string Id, string Name)
    {
      public override string ToString() => Name;
    }

    public SpellPickerDialog(bool enableBundleMode = false)
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      _enableBundleMode = enableBundleMode;
      _allSpells = [.. EQDataStore.Instance.GetSpellsForPicker()];

      foreach (var label in MessageFieldLabels)
      {
        messageField.Items.Add(label);
      }
      messageField.SelectedIndex = 0;

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

      typeFilter.Items.Add(AllTypes);
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

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
      _loaded = true;

      if (_enableBundleMode)
      {
        modeToggleRow.Visibility = Visibility.Visible;
      }

      overlayPicker.Items.Add(NoOverlay);
      foreach (var ov in (await TriggerStateDB.Instance.GetAllOverlays())
                           .Where(o => o.OverlayData.IsTimerOverlay))
      {
        overlayPicker.Items.Add(new OverlayOption(ov.Id, ov.Name));
      }
      overlayPicker.SelectedIndex = 0;

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

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
      if (!_loaded) return;

      var isBundle = bundleModeRadio.IsChecked == true;
      casterRow.Visibility     = isBundle ? Visibility.Visible   : Visibility.Collapsed;
      insertModeLabel.Visibility = isBundle ? Visibility.Collapsed : Visibility.Visible;
      messageField.Visibility  = isBundle ? Visibility.Collapsed : Visibility.Visible;
      insertButton.Content     = isBundle ? "Generate" : "Insert";
      hintText.Text            = string.Empty;
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
      _resetting = true;
      searchBox.Clear();
      levelMin.Clear();
      levelMax.Clear();
      SelectDefaultClass();
      categoryFilter.SelectedIndex = 0;
      typeFilter.SelectedIndex     = 0;
      hasSpaFilter.SelectedIndex   = 0;
      inSlotFilter.SelectedIndex   = 0;
      _resetting = false;
      ApplyFilter();
    }

    private void ApplyFilter()
    {
      var className = classFilter.SelectedItem as string;
      var classFlag = (string.IsNullOrEmpty(className) || className == AllClasses)
        ? (SpellClass?)null
        : EQDataStore.Instance.GetSpellClassByName(className);

      var category = categoryFilter.SelectedItem as string;
      if (category == AllCategories) category = null;

      bool? beneficial = (typeFilter.SelectedItem as string) switch
      {
        "Beneficial"  => true,
        "Detrimental" => false,
        _             => null
      };

      var hasMin = int.TryParse(levelMin.Text, out var minLevel);
      var hasMax = int.TryParse(levelMax.Text, out var maxLevel);

      var spaOption = hasSpaFilter.SelectedItem as SpaOption;
      var slotStr   = inSlotFilter.SelectedItem as string;
      var slotIdx   = slotStr != null && slotStr != AnySlot
        ? int.Parse(slotStr.Split(' ')[1]) - 1
        : -1;

      var query = SpellPickerFilter.Apply(
        _allSpells, searchBox.Text, classFlag, category,
        beneficial,
        hasMin ? minLevel : null,
        hasMax ? maxLevel : null).AsEnumerable();

      if (spaOption != null || slotIdx >= 0)
      {
        query = query.Where(s =>
        {
          var effects = EQDataStore.Instance.GetSpellEffects(s.Id);
          if (effects?.Slots == null) return false;
          if (spaOption != null && slotIdx >= 0)
            return effects.Slots.Any(e => e.Spa == spaOption.Spa && e.Slot == slotIdx);
          if (spaOption != null)
            return effects.Slots.Any(e => e.Spa == spaOption.Spa);
          return effects.Slots.Any(e => e.Slot == slotIdx);
        });
      }

      dataGrid.ItemsSource = query.ToList();
      hintText.Text = string.Empty;
    }

    private void SpellSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      if (dataGrid.SelectedItem is SpellData spell)
      {
        for (var i = 0; i < MessageFieldLabels.Length; i++)
        {
          if (!string.IsNullOrEmpty(GetFieldText(spell, i)))
          {
            messageField.SelectedIndex = i;
            break;
          }
        }
      }
    }

    private void SpellCellDoubleTapped(object sender, GridCellDoubleTappedEventArgs e) => TryInsert();

    private void InsertClick(object sender, RoutedEventArgs e) => TryInsert();

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      IsOkClicked = false;
      Close();
    }

    private void TryInsert()
    {
      if (bundleModeRadio.IsChecked == true)
      {
        TryGenerate();
        return;
      }

      if (dataGrid.SelectedItem is not SpellData spell)
      {
        hintText.Text = "Select a spell first.";
        return;
      }

      var text = GetFieldText(spell, messageField.SelectedIndex);
      if (string.IsNullOrEmpty(text))
      {
        hintText.Text = "That spell has no text for the selected message.";
        return;
      }

      SelectedText = text;
      IsOkClicked = true;
      Close();
    }

    private void TryGenerate()
    {
      if (dataGrid.SelectedItem is not SpellData spell)
      {
        hintText.Text = "Select a spell first.";
        return;
      }

      var casterName = casterBox.Text.Trim();
      var hasName    = !string.IsNullOrEmpty(casterName);

      var castingLine = hasName
        ? $"{casterName} begins casting {spell.Name}."
        : $"begins casting {spell.Name}.";

      var landsOnOther = spell.LandsOnOther?.Trim();
      var hasLanding   = !string.IsNullOrEmpty(landsOnOther);

      string pattern, previousPattern, endEarlyPattern, endEarlyPattern2, altTimerName;
      bool useRegex;

      if (hasLanding)
      {
        // Buff/HoT/DoT tracker: fires when spell lands on target.
        // Prepend {S1} to capture the target name.
        var prefix   = landsOnOther.StartsWith('\'') ? "{S1}" : "{S1} ";
        pattern          = prefix + landsOnOther;
        useRegex         = true;
        previousPattern  = hasName ? castingLine : string.Empty;
        endEarlyPattern  = spell.WearOff?.Trim() ?? string.Empty;
        endEarlyPattern2 = string.Empty;
        altTimerName     = $"{spell.Name} - {{S1}}";
      }
      else
      {
        // Fallback: no landing message — use casting line as Pattern.
        pattern          = castingLine;
        useRegex         = false;
        previousPattern  = string.Empty;
        endEarlyPattern  = hasName
          ? $"{casterName}'s {spell.Name} spell is interrupted."
          : string.Empty;
        endEarlyPattern2 = string.Empty;
        altTimerName     = spell.Name;
      }

      var durationSeconds = spell.Duration * 6.0;
      var overlayId = overlayPicker.SelectedItem is OverlayOption op ? op.Id : null;

      if (durationSeconds == 0)
      {
        hintText.Text = "Warning: spell has no duration — timer will show 0 seconds.";
      }

      BundleResult = new TriggerBundle(
        Pattern:          pattern,
        UseRegex:         useRegex,
        PreviousPattern:  previousPattern,
        TimerType:        1,
        DurationSeconds:  durationSeconds,
        EndEarlyPattern:  endEarlyPattern,
        EndEarlyPattern2: endEarlyPattern2,
        AltTimerName:     altTimerName,
        OverlayId:        overlayId
      );
      IsOkClicked = true;
      Close();
    }

    private static string GetFieldText(SpellData spell, int fieldIndex) => fieldIndex switch
    {
      0 => spell.LandsOnYou,
      1 => spell.LandsOnOther,
      2 => spell.WearOff,
      _ => null
    };
  }
}
