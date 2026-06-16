using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EQLogParser
{
  // Returns a background Brush for a category badge cell, hashed deterministically
  // from the primary (first semicolon-delimited) category name.
  internal class CategoryBadgeColorConverter : IValueConverter
  {
    private static readonly Brush[] Palette =
    [
      new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), // blue
      new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)), // green
      new SolidColorBrush(Color.FromRgb(0x93, 0x33, 0xEA)), // purple
      new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), // red
      new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), // amber
      new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xB2)), // cyan
      new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)), // violet
      new SolidColorBrush(Color.FromRgb(0xBE, 0x18, 0x5D)), // pink
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is not string category || string.IsNullOrEmpty(category))
        return Brushes.Transparent;
      var primary = category.Split(';')[0].Trim();
      var index = (int)((uint)primary.GetHashCode() % Palette.Length);
      return Palette[index];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
      => throw new NotSupportedException();
  }

  // Converts a semicolon-separated category string to display text with · separators.
  internal class CategoryBadgeTextConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
      => value is string s && !string.IsNullOrEmpty(s)
        ? s.Replace(";", " · ")
        : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
      => throw new NotSupportedException();
  }

  // Hides the badge border when Category is empty so the cell stays clean.
  internal class CategoryBadgeVisibilityConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
      => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
      => throw new NotSupportedException();
  }
}
