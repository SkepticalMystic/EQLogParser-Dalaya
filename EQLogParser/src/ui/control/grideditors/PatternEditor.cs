using log4net;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  internal class PatternEditor : BaseTypeEditor
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private TextBox _theTextBox;
    private CheckBox _theCheckBox;
    private Button _spellButton;
    private Grid _grid;
    private bool _userTurnedOff;

    // P4: set in Attach() so SpellButtonClick can write bundle fields directly to
    // the model and trigger a property-grid refresh without owning the grid.
    private TriggerPropertyModel _triggerModel;
    private bool _isPatternField;

    public void SetForeground(string foreground)
    {
      // this only works if there's one reference to this editor...
      // TODO figure out better way
      _theTextBox?.SetResourceReference(Control.ForegroundProperty, foreground);
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
      };

      BindingOperations.SetBinding(_theTextBox, TextBox.TextProperty, binding);

      // P4: only the Pattern-field editor enables bundle mode and holds the model ref.
      _isPatternField = info.Name == "Pattern";
      _triggerModel = _isPatternField ? info.SelectedObject as TriggerPropertyModel : null;

      var bindName = info.Name switch
      {
        "Pattern" => "UseRegex",
        "PreviousPattern" => "PreviousUseRegex",
        "EndEarlyPattern" => "EndUseRegex",
        "EndEarlyPattern2" => "EndUseRegex2",
        "EndEarlyPattern3" => "EndUseRegex3",
        "ClosePattern" => "UseCloseRegex",
        _ => null
      };

      if (bindName != null)
      {
        binding = new Binding(bindName)
        {
          Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
          Source = info.SelectedObject,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true,
        };

        BindingOperations.SetBinding(_theCheckBox, ToggleButton.IsCheckedProperty, binding);
      }
    }

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      if (_grid != null)
        return _grid;

      _userTurnedOff = false;
      _grid = new Grid();
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      // Dalaya: column for the "Spell..." button (see SpellPickerDialog).
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

      _theTextBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0)
      };

      _theTextBox.SetValue(Grid.ColumnProperty, 0);
      _theTextBox.TextChanged += TheTextChanged;

      // Dalaya: opens the spell picker and either inserts a spell's log message
      // (insert mode) or writes a full cast-timer bundle to the trigger model
      // (bundle mode, P4). See memory project-trigger-spell-picker.
      _spellButton = new Button
      {
        Content = "Spell…",
        Padding = new Thickness(8, 0, 8, 0),
        Margin = new Thickness(4, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Insert a spell's log message into this pattern"
      };
      _spellButton.SetValue(Grid.ColumnProperty, 1);
      _spellButton.Click += SpellButtonClick;

      _theCheckBox = new CheckBox
      {
        Content = "Use Regex",
        Template = (ControlTemplate)Application.Current.Resources["CustomCheckBoxTemplate"]
      };

      _theCheckBox.SetValue(Grid.ColumnProperty, 2);
      _theCheckBox.Checked += TheCheckBoxChecked;
      _theCheckBox.Unchecked += TheCheckBoxChecked;
      _grid.Children.Add(_theTextBox);
      _grid.Children.Add(_spellButton);
      _grid.Children.Add(_theCheckBox);
      return _grid;
    }

    private async void SpellButtonClick(object sender, RoutedEventArgs e)
    {
      if (_theTextBox == null) return;

      try
      {
        var dialog = new SpellPickerDialog(enableBundleMode: _isPatternField);
        dialog.ShowDialog();

        if (dialog.BundleResult != null && _triggerModel != null)
        {
          var b = dialog.BundleResult;

          // Pattern field (the one this editor owns)
          _theTextBox.Text = b.Pattern;
          _theTextBox.CaretIndex = _theTextBox.Text.Length;
          _theTextBox.Focus();

          // Trigger scalar fields
          _triggerModel.UseRegex                   = b.UseRegex;
          _triggerModel.PreviousPattern            = b.PreviousPattern;
          _triggerModel.PreviousLineWindowSeconds  = b.PreviousLineWindowSeconds;
          _triggerModel.TimerType                  = b.TimerType;
          _triggerModel.DurationSeconds  = b.DurationSeconds;
          _triggerModel.EndEarlyPattern  = b.EndEarlyPattern;
          _triggerModel.EndEarlyPattern2 = b.EndEarlyPattern2;
          _triggerModel.AltTimerName     = b.AltTimerName;

          // Overlay: update both the persisted ID list and the UI binding
          if (b.OverlayId != null)
          {
            if (!_triggerModel.SelectedOverlays.Contains(b.OverlayId))
            {
              _triggerModel.SelectedOverlays.Add(b.OverlayId);
            }

            var item = _triggerModel.SelectedTimerOverlays?
                         .FirstOrDefault(x => x.Value == b.OverlayId);
            if (item != null)
            {
              item.IsChecked = true;
            }
            else if (_triggerModel.SelectedTimerOverlays == null)
            {
              // New trigger not yet loaded via TriggerUtil.Copy — build the collection now.
              var all = await TriggerStateDB.Instance.GetAllOverlays();
              _triggerModel.SelectedTimerOverlays = new ObservableCollection<ComboBoxItemDetails>(
                all.Select(o => new ComboBoxItemDetails
                {
                  IsChecked = o.Id == b.OverlayId,
                  Text  = o.Name,
                  Value = o.Id
                }));
            }
          }

          RefreshPropertyGrid();
        }
        else if (dialog.CategoryRegexResult != null && _triggerModel != null)
        {
          // P5: category regex — sets Pattern + UseRegex only; leaves all timer fields alone.
          _theTextBox.Text = dialog.CategoryRegexResult.Pattern;
          _theTextBox.CaretIndex = _theTextBox.Text.Length;
          _theTextBox.Focus();
          _triggerModel.UseRegex = true;
          _triggerModel.SpellNameLookup = dialog.CategoryRegexResult.SpellNameLookup;
          RefreshPropertyGrid();
        }
        else if (dialog.IsOkClicked && !string.IsNullOrEmpty(dialog.SelectedText))
        {
          var caret = _theTextBox.CaretIndex;
          _theTextBox.Text = _theTextBox.Text.Insert(caret, dialog.SelectedText);
          _theTextBox.CaretIndex = caret + dialog.SelectedText.Length;
          _theTextBox.Focus();
        }
      }
      catch (Exception ex)
      {
        Log.Error("Failed to open spell picker", ex);
      }
    }

    // Walk the visual tree to find the containing PropertyGrid and force it to
    // re-read all values from the model via the null/re-set pattern.
    private void RefreshPropertyGrid()
    {
      DependencyObject parent = VisualTreeHelper.GetParent(_grid);
      while (parent != null)
      {
        if (parent is PropertyGrid pg)
        {
          var obj = pg.SelectedObject;
          pg.SelectedObject = null;
          pg.SelectedObject = obj;
          break;
        }
        parent = VisualTreeHelper.GetParent(parent);
      }
    }

    private void TheTextChanged(object sender, TextChangedEventArgs e)
    {
      if (!_userTurnedOff && TriggerUtil.IsProbRegex(_theTextBox?.Text) && _theCheckBox.IsChecked == false)
      {
        _theCheckBox.IsChecked = true;
      }
    }

    private void TheCheckBoxChecked(object sender, RoutedEventArgs e)
    {
      // original source will be either the real checkbox or one in the
      // control template. this is handled with default callbacks in
      // app.xaml.cs for normal cases?
      if (e.OriginalSource is CheckBox box)
      {
        // ignore checkbox in the control template (has no name)
        if (box.Content == null)
        {
          return;
        }

        if (box.DataContext == null)
        {
          // reset just in case
          _userTurnedOff = false;
        }
        else
        {
          // turn off listeners
          _theTextBox.TextChanged -= TheTextChanged;
          // toggle text to trigger the ValueChanged event
          var previous = _theTextBox.Text;
          _theTextBox.Text += " ";
          _theTextBox.Text = previous;
          _theTextBox.SelectionStart = _theTextBox.Text.Length;
          // put listener back on
          _theTextBox.TextChanged += TheTextChanged;

          // if the toggle is changed to false it must have been a user decision
          if (_theCheckBox.IsChecked == false)
          {
            _userTurnedOff = true;
          }
        }
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      _triggerModel = null;
      _isPatternField = false;

      if (_theTextBox != null)
      {
        _theTextBox.TextChanged -= TheTextChanged;
        BindingOperations.ClearAllBindings(_theTextBox);
        _theTextBox = null;
      }

      if (_theCheckBox != null)
      {
        _theCheckBox.Checked -= TheCheckBoxChecked;
        _theCheckBox.Unchecked -= TheCheckBoxChecked;
        BindingOperations.ClearAllBindings(_theCheckBox);
        _theCheckBox = null;
      }

      if (_spellButton != null)
      {
        _spellButton.Click -= SpellButtonClick;
        _spellButton = null;
      }

      if (_grid != null)
      {
        _grid.Children.Clear();
        _grid = null;
      }
    }
  }
}
