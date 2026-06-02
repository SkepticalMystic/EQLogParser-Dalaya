using log4net;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

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
      // Dalaya: column for the "Insert Spell..." button (see SpellPickerDialog).
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

      // Dalaya: opens the spell picker and inserts a spell's log message into the
      // pattern at the caret. See memory project-trigger-spell-picker.
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

    private void SpellButtonClick(object sender, RoutedEventArgs e)
    {
      if (_theTextBox == null)
      {
        return;
      }

      try
      {
        var dialog = new SpellPickerDialog();
        dialog.ShowDialog();
        if (dialog.IsOkClicked && !string.IsNullOrEmpty(dialog.SelectedText))
        {
          var caret = _theTextBox.CaretIndex;
          _theTextBox.Text = _theTextBox.Text.Insert(caret, dialog.SelectedText);
          _theTextBox.CaretIndex = caret + dialog.SelectedText.Length;
          _theTextBox.Focus();
        }
      }
      catch (Exception ex)
      {
        // A failure opening the picker shouldn't take down the property grid.
        Log.Error("Failed to open spell picker", ex);
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
