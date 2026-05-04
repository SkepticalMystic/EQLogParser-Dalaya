using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  // Hosts a DoubleTextBox (sub-10s, tenths) and a TimeSpanEdit (>=10s, hh:mm:ss) in one
  // container, switching which is visible based on the committed DurationSeconds value.
  internal class AdaptiveDurationEditor : BaseTypeEditor
  {
    private const double LongFormatThreshold = 60.0;

    private Grid _root;
    private DoubleTextBox _shortBox;
    private TimeSpanEdit _longBox;

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      if (_root != null)
      {
        return _root;
      }

      _shortBox = new DoubleTextBox
      {
        ApplyZeroColor = false,
        ShowSpinButton = true,
        ScrollInterval = 0.1,
        NumberDecimalDigits = 1,
        MinValue = 0.1,
        MaxValue = 86399,
        BorderThickness = new Thickness(0),
        Margin = new Thickness(0, 2, 0, 2),
      };
      _shortBox.SetResourceReference(EditorBase.PositiveForegroundProperty, "ContentForeground");

      _longBox = new TimeSpanEdit
      {
        IncrementOnScrolling = false,
        MinValue = TimeSpan.FromSeconds(1),
        MaxValue = new TimeSpan(23, 59, 59),
        Format = "hh : mm : ss",
        BorderThickness = new Thickness(0),
        Margin = new Thickness(0, 2, 0, 2),
      };

      _root = new Grid();
      _root.Children.Add(_shortBox);
      _root.Children.Add(_longBox);

      _shortBox.LostFocus += BoxLostFocus;
      _longBox.LostFocus += LongBoxLostFocus;
      _longBox.GotFocus += LongBoxGotFocus;
      _longBox.PreviewMouseWheel += LongBoxPreviewMouseWheel;
      return _root;
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var shortBinding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
      };
      BindingOperations.SetBinding(_shortBox, DoubleTextBox.ValueProperty, shortBinding);

      var longBinding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
        Converter = SecondsTimeSpanConverter.Instance,
      };
      BindingOperations.SetBinding(_longBox, TimeSpanEdit.ValueProperty, longBinding);

      var seconds = info.Value is double v ? v : 0.0;
      UpdateVisibility(seconds);
    }

    private void BoxLostFocus(object sender, RoutedEventArgs e)
    {
      var current = _shortBox?.Value ?? 0.0;
      UpdateVisibility(current);
    }

    private void LongBoxLostFocus(object sender, RoutedEventArgs e)
    {
      if (sender is TimeSpanEdit edit)
      {
        edit.IncrementOnScrolling = false;
      }

      var current = _longBox?.Value?.TotalSeconds ?? 0.0;
      UpdateVisibility(current);
    }

    private void LongBoxGotFocus(object sender, RoutedEventArgs e)
    {
      if (sender is TimeSpanEdit edit)
      {
        edit.IncrementOnScrolling = true;
      }
    }

    private void LongBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
      if (sender is TimeSpanEdit { SelectionStart: var selected, Value: { } t } edit)
      {
        var inc = e.Delta > 0 ? 1 : -1;
        if (selected >= 10)
        {
          edit.Value = new TimeSpan(t.Hours, t.Minutes, t.Seconds + inc);
        }
        else if (selected >= 5)
        {
          edit.Value = new TimeSpan(t.Hours, t.Minutes + inc, t.Seconds);
        }
        else if (selected >= 0)
        {
          edit.Value = new TimeSpan(t.Hours + inc, t.Minutes, t.Seconds);
        }
        e.Handled = true;
      }
    }

    private void UpdateVisibility(double seconds)
    {
      var useLong = seconds >= LongFormatThreshold;
      if (_shortBox != null)
      {
        _shortBox.Visibility = useLong ? Visibility.Collapsed : Visibility.Visible;
      }
      if (_longBox != null)
      {
        _longBox.Visibility = useLong ? Visibility.Visible : Visibility.Collapsed;
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key) => false;

    public override void Detach(PropertyViewItem property)
    {
      if (_shortBox != null)
      {
        _shortBox.LostFocus -= BoxLostFocus;
        BindingOperations.ClearAllBindings(_shortBox);
        _shortBox.Dispose();
        _shortBox = null;
      }

      if (_longBox != null)
      {
        _longBox.LostFocus -= LongBoxLostFocus;
        _longBox.GotFocus -= LongBoxGotFocus;
        _longBox.PreviewMouseWheel -= LongBoxPreviewMouseWheel;
        BindingOperations.ClearAllBindings(_longBox);
        _longBox.Dispose();
        _longBox = null;
      }

      _root = null;
    }
  }

  internal sealed class SecondsTimeSpanConverter : IValueConverter
  {
    internal static readonly SecondsTimeSpanConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is double d)
      {
        return TimeSpan.FromSeconds(d);
      }
      return TimeSpan.Zero;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is TimeSpan t)
      {
        return t.TotalSeconds;
      }
      return 0.0;
    }
  }
}
