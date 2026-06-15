using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  // Caches BitmapSource objects for spell gem icons loaded from data/icons/gemicon{N}.jpg.
  // At most 216 unique icons are ever loaded, so the cache is bounded and safe to hold
  // for the lifetime of the app.
  internal static class SpellIconCache
  {
    private static readonly Dictionary<int, BitmapSource> _cache = [];
    private static readonly string _iconsDir = Path.Combine("data", "icons");

    // Returns the icon for the given icon ID, or null when the ID is 0 or the
    // file is missing. Results are cached — safe to call on every row render.
    internal static BitmapSource Get(int iconId, int pixelWidth = 28)
    {
      if (iconId <= 0) return null;
      if (_cache.TryGetValue(iconId, out var cached)) return cached;

      var path = Path.Combine(_iconsDir, $"gemicon{iconId}.jpg");
      if (!File.Exists(path))
      {
        _cache[iconId] = null;
        return null;
      }

      try
      {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.DecodePixelWidth = pixelWidth;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        _cache[iconId] = img;
        return img;
      }
      catch
      {
        _cache[iconId] = null;
        return null;
      }
    }
  }
}
