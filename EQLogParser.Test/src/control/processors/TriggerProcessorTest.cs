using EQLogParser;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EQLogParserTest
{
  [TestClass]
  public class TriggerProcessorTest
  {
    private const string CastPattern = "begins to cast";
    private const string LandPattern = "is rent apart by deathly claws";

    [TestMethod]
    public void NoPreviousPatternConfigured_PassesThrough()
    {
      // A trigger with no PreviousPattern should always pass the previous-line check.
      var wrapper = MakeWrapper(previousPattern: null, useRegex: false, windowSeconds: 0);
      var buffer = MakeBuffer((1.0, "anything"));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 2.0, out _, out _);

      Assert.IsTrue(found, "Triggers without a previous-pattern constraint should pass.");
    }

    [TestMethod]
    public void PreviousPatternConfigured_EmptyBufferFails()
    {
      // The very first incoming line has nothing before it; previous-pattern triggers must not fire.
      var wrapper = MakeWrapper(previousPattern: CastPattern, useRegex: false, windowSeconds: 0);
      var buffer = new LinkedList<LineData>();

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 1.0, out _, out _);

      Assert.IsFalse(found);
    }

    [TestMethod]
    public void Window0_OnlyImmediatePriorLine_Matches()
    {
      // Legacy behavior preserved: with window=0, only the most recent buffered line is inspected.
      var wrapper = MakeWrapper(previousPattern: CastPattern, useRegex: false, windowSeconds: 0);
      var buffer = MakeBuffer(
        (1.0, "Ezran begins to cast a spell."),
        (2.0, "Some unrelated chatter."));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 3.0, out _, out _);

      Assert.IsFalse(found, "With window=0 and a non-matching immediate prior line, the trigger must not match an earlier line.");
    }

    [TestMethod]
    public void Window0_ImmediatePriorLineMatches_Fires()
    {
      var wrapper = MakeWrapper(previousPattern: CastPattern, useRegex: false, windowSeconds: 0);
      var buffer = MakeBuffer((1.0, "Ezran begins to cast a spell."));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 2.0, out _, out _);

      Assert.IsTrue(found);
    }

    [TestMethod]
    public void Window_MatchInsideWindow_Fires()
    {
      // Cast line 4s before the current line; with a 6s window the match should be found.
      var wrapper = MakeWrapper(previousPattern: CastPattern, useRegex: false, windowSeconds: 6);
      var buffer = MakeBuffer(
        (1.0, "Ezran begins to cast a spell."),
        (2.0, "filler 1"),
        (3.0, "filler 2"),
        (4.0, "filler 3"));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 5.0, out _, out _);

      Assert.IsTrue(found);
    }

    [TestMethod]
    public void Window_MatchOutsideWindow_DoesNotFire()
    {
      // Cast line 8s before the current line; with a 6s window it falls outside the scan range.
      var wrapper = MakeWrapper(previousPattern: CastPattern, useRegex: false, windowSeconds: 6);
      var buffer = MakeBuffer(
        (1.0, "Ezran begins to cast a spell."),
        (5.0, "filler"),
        (8.0, "filler"));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 9.0, out _, out _);

      Assert.IsFalse(found);
    }

    [TestMethod]
    public void Window_MultipleMatches_NewestWins()
    {
      // Two matching cast lines inside the window; the newest one's capture group should be returned.
      var pattern = "(?<caster>\\w+) begins to cast";
      var wrapper = MakeWrapper(previousPattern: pattern, useRegex: true, windowSeconds: 10);
      var buffer = MakeBuffer(
        (1.0, "Alpha begins to cast a spell."),
        (3.0, "filler"),
        (5.0, "Bravo begins to cast a spell."),
        (6.0, "filler"));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 7.0, out var matches, out _);

      Assert.IsTrue(found);
      Assert.IsNotNull(matches);
      Assert.IsTrue(matches.ContainsKey("caster"), "Expected named capture 'caster' to be returned.");
      Assert.AreEqual("Bravo", matches["caster"], "The most recent matching candidate's capture should win.");
    }

    [TestMethod]
    public void Window_RegexMatchInsideWindow_Fires()
    {
      // Regex path uses ContainsText / StartText optimizations; make sure they're applied correctly inside a window.
      var wrapper = MakeWrapper(previousPattern: "Ezran begins to cast", useRegex: true, windowSeconds: 5);
      var buffer = MakeBuffer(
        (1.0, "Ezran begins to cast a spell."),
        (2.0, "ambient line"));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 3.0, out _, out _);

      Assert.IsTrue(found);
    }

    [TestMethod]
    public void Window_BoundaryInclusive()
    {
      // currentBegin - candidate.BeginTime == window must still match (boundary is inclusive).
      var wrapper = MakeWrapper(previousPattern: CastPattern, useRegex: false, windowSeconds: 5);
      var buffer = MakeBuffer((0.0, "Ezran begins to cast a spell."));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 5.0, out _, out _);

      Assert.IsTrue(found, "Window boundary should be inclusive (<=, not <).");
    }

    [TestMethod]
    public void Window_OldestEntryMatchesButOutsideWindow_NewerNonMatch_DoesNotFire()
    {
      // The only matching line is older than the window; ensure we don't walk past the window cutoff.
      var wrapper = MakeWrapper(previousPattern: CastPattern, useRegex: false, windowSeconds: 3);
      var buffer = MakeBuffer(
        (1.0, "Ezran begins to cast a spell."),
        (2.0, "filler"),
        (3.0, "filler"),
        (4.0, "filler"));

      var found = TriggerProcessor.CheckPreviousLine(wrapper, buffer, 5.0, out _, out _);

      Assert.IsFalse(found);
    }

    private static TriggerWrapper MakeWrapper(string? previousPattern, bool useRegex, double windowSeconds)
    {
      var wrapper = new TriggerWrapper
      {
        Id = "test",
        Name = "Test",
        TriggerData = new Trigger
        {
          PreviousPattern = previousPattern,
          PreviousUseRegex = useRegex,
          PreviousLineWindowSeconds = windowSeconds
        }
      };

      if (string.IsNullOrEmpty(previousPattern)) return wrapper;

      if (useRegex)
      {
        wrapper.PreviousRegex = new Regex(previousPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        wrapper.PreviousRegexNOptions = [];
      }
      else
      {
        wrapper.ModifiedPreviousPattern = previousPattern;
      }

      return wrapper;
    }

    private static LinkedList<LineData> MakeBuffer(params (double Time, string Action)[] entries)
    {
      var list = new LinkedList<LineData>();
      foreach (var (time, action) in entries)
      {
        list.AddLast(new LineData { Action = action, BeginTime = time });
      }
      return list;
    }
  }
}
