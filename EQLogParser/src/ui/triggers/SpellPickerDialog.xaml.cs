using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
      double PreviousLineWindowSeconds,
      int TimerType,
      double DurationSeconds,
      string EndEarlyPattern,
      string EndEarlyPattern2,
      string AltTimerName,
      string OverlayId
    );

    public TriggerBundle BundleResult { get; private set; }

    // Category regex mode (P5): only Pattern is returned; caller sets UseRegex = true.
    // SpellNameLookup is non-null for landing-mode triggers only: maps each matched
    // "lands on other" text back to the canonical spell name for {SpellName} substitution.
    public record CategoryRegex(string Pattern, Dictionary<string, string> SpellNameLookup = null);
    public CategoryRegex CategoryRegexResult { get; private set; }

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
      classFilter.SelectedIndex = 0;

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

      var isBundle   = bundleModeRadio.IsChecked == true;
      var isCatRegex = catRegexModeRadio.IsChecked == true;
      var isInsert   = !isBundle && !isCatRegex;

      casterRow.Visibility      = isBundle   ? Visibility.Visible   : Visibility.Collapsed;
      catRegexRow.Visibility    = isCatRegex ? Visibility.Visible   : Visibility.Collapsed;
      insertModeLabel.Visibility = isInsert  ? Visibility.Visible   : Visibility.Collapsed;
      messageField.Visibility   = isInsert   ? Visibility.Visible   : Visibility.Collapsed;
      insertButton.Content      = isInsert   ? "Insert" : "Generate";

      if (isCatRegex)
        UpdateCatRegexHint();
      else
        hintText.Text = string.Empty;
    }

    private void CasterTypeChanged(object sender, RoutedEventArgs e)
    {
      if (!_loaded) return;
      otherCasterBox.IsEnabled = otherCastCheck.IsChecked == true;
    }

    private void CatTypeChanged(object sender, RoutedEventArgs e)
    {
      if (!_loaded) return;
      var isCast = castRadio.IsChecked == true;
      catCasterLabel.Visibility = isCast ? Visibility.Visible : Visibility.Collapsed;
      catCasterBox.Visibility   = isCast ? Visibility.Visible : Visibility.Collapsed;
      catCasterHint.Visibility  = isCast ? Visibility.Visible : Visibility.Collapsed;
      UpdateCatRegexHint();
    }

    private void UpdateCatRegexHint()
    {
      var spells = dataGrid.ItemsSource as List<SpellData>;
      var count  = spells?.Count ?? 0;
      if (count == 0)
      {
        hintText.Text = "No spells match the current filters.";
        return;
      }
      if (landingRadio.IsChecked == true)
      {
        var withLanding = spells.Count(s => !string.IsNullOrWhiteSpace(s.LandsOnOther));
        hintText.Text = withLanding == 0
          ? "No landing messages found for the filtered spells."
          : $"{withLanding} spell{(withLanding == 1 ? "" : "s")} with landing text (of {count} matching).";
      }
      else
      {
        hintText.Text = $"{count} spell{(count == 1 ? "" : "s")} will be included.";
      }
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
      _resetting = true;
      searchBox.Clear();
      levelMin.Clear();
      levelMax.Clear();
      classFilter.SelectedIndex = 0;
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
      if (catRegexModeRadio?.IsChecked == true)
        UpdateCatRegexHint();
      else
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
      if (catRegexModeRadio.IsChecked == true)
      {
        TryCategoryRegex();
        return;
      }
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

    private void TryCategoryRegex()
    {
      var spells = dataGrid.ItemsSource as List<SpellData>;
      if (spells == null || spells.Count == 0)
      {
        hintText.Text = "No spells match the current filters.";
        return;
      }

      string pattern;
      Dictionary<string, string> spellNameLookup = null;
      if (castRadio.IsChecked == true)
      {
        // The (?<SpellName>...) group captures the spell name directly from the log line.
        var casterName = catCasterBox.Text.Trim();
        var alt = string.Join("|", spells.Select(s => Regex.Escape(s.Name)).Distinct().OrderBy(x => x));
        pattern = string.IsNullOrEmpty(casterName)
          ? $@"{{S1}} begins casting (?<SpellName>{alt})\."
          : $@"{Regex.Escape(casterName)} begins casting (?<SpellName>{alt})\.";
      }
      else
      {
        var landingSpells = spells
          .Select(s => (Text: s.LandsOnOther?.Trim(), s.Name))
          .Where(x => !string.IsNullOrEmpty(x.Text))
          .ToList();
        if (landingSpells.Count == 0)
        {
          hintText.Text = "No landing messages found for the filtered spells.";
          return;
        }

        // Build lookup before escaping so keys match the raw log text.
        spellNameLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (text, name) in landingSpells)
          spellNameLookup.TryAdd(text, name);

        var texts = landingSpells
          .Select(x => Regex.Escape(x.Text))
          .Distinct()
          .OrderBy(x => x)
          .ToList();
        // (?<SpellName>...) captures the landing text; TriggerProcessor resolves it to
        // the spell name via SpellNameLookup at match time.
        pattern = $"(?<SpellName>{string.Join("|", texts)})";
      }

      CategoryRegexResult = new CategoryRegex(pattern, spellNameLookup);
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

      var isOther    = otherCastCheck.IsChecked == true;
      var otherName  = otherCasterBox.Text.Trim();
      var hasOther   = isOther && !string.IsNullOrEmpty(otherName);

      // "You begin casting" (first person) vs third-person forms.
      var castingLine = !isOther
        ? $"You begin casting {spell.Name}."
        : hasOther
          ? $"{otherName} begins casting {spell.Name}."
          : $"begins casting {spell.Name}.";

      var landsOnOther = spell.LandsOnOther?.Trim();
      var hasLanding   = !string.IsNullOrEmpty(landsOnOther);

      string pattern, previousPattern, endEarlyPattern, endEarlyPattern2, altTimerName;
      bool useRegex;
      double previousLineWindowSeconds;

      // Cast time in seconds; 0 means instant-cast (no "begins casting" log line).
      var castSeconds = spell.CastingTimeMs / 1000.0;

      if (hasLanding)
      {
        // Buff/HoT/DoT tracker: fires when spell lands on target.
        // Prepend {S1} to capture the target name.
        var prefix   = landsOnOther.StartsWith('\'') ? "{S1}" : "{S1} ";
        pattern      = prefix + landsOnOther;
        useRegex     = true;
        altTimerName = $"{spell.Name} - {{S1}}";

        // Instant-cast spells produce no "begins casting" log line.
        if (castSeconds > 0)
        {
          previousPattern           = castingLine;
          previousLineWindowSeconds = castSeconds + 2.0;
        }
        else
        {
          previousPattern           = string.Empty;
          previousLineWindowSeconds = 0;
        }

        // Generic EQ wear-off line — fires when the spell drops off any target.
        endEarlyPattern  = $"Your {spell.Name} spell affecting";
        endEarlyPattern2 = string.Empty;
      }
      else
      {
        // Fallback: no landing message — use casting line as Pattern.
        pattern                   = castingLine;
        useRegex                  = false;
        previousPattern           = string.Empty;
        previousLineWindowSeconds = 0;
        // Interrupt line differs between self and other.
        endEarlyPattern = !isOther
          ? $"Your {spell.Name} spell is interrupted."
          : hasOther
            ? $"{otherName}'s {spell.Name} spell is interrupted."
            : string.Empty;
        endEarlyPattern2 = string.Empty;
        altTimerName     = spell.Name;
      }

      // spell.Duration is already in seconds (loaded as raw ticks × 6 from spells.txt).
      var durationSeconds = (double)spell.Duration;
      var overlayId = overlayPicker.SelectedItem is OverlayOption op ? op.Id : null;

      if (durationSeconds == 0)
      {
        hintText.Text = "Warning: spell has no duration — timer will show 0 seconds.";
      }

      BundleResult = new TriggerBundle(
        Pattern:                    pattern,
        UseRegex:                   useRegex,
        PreviousPattern:            previousPattern,
        PreviousLineWindowSeconds:  previousLineWindowSeconds,
        TimerType:                  1,
        DurationSeconds:            durationSeconds,
        EndEarlyPattern:            endEarlyPattern,
        EndEarlyPattern2:           endEarlyPattern2,
        AltTimerName:               altTimerName,
        OverlayId:                  overlayId
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
