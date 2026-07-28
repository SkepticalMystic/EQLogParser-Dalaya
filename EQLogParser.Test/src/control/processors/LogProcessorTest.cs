using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using EQLogParser;

namespace EQLogParserTest.Control.Processors
{
  [TestClass]
  public class LogProcessorTest
  {
    // Regression for the Ezran raid-log hang (2026-07-27): LogProcessor.LinkTo's background
    // consumer used to let any exception from DoPreProcess escape the foreach loop, killing the
    // consumer task while LogReader kept queueing lines into the bounded BlockingCollection —
    // filling it and blocking the reader forever (observed as the app frozen at a fixed "Reading
    // Log.." percentage indefinitely). The fix wraps each item's processing in its own try/catch
    // so one malformed line is logged and skipped instead of stopping consumption of everything
    // queued after it.
    [TestMethod]
    public void LinkTo_MalformedLineDoesNotStopConsumerFromProcessingLaterLines()
    {
      var ctx = ParseContext.CreateIsolated();
      ctx.PlayerRegistry.PlayerName = "Tester";
      ctx.PlayerRegistry.AddVerifiedPlayer("Tester", 1000.0);

      var fights = new List<Fight>();
      ctx.FightManager.EventsNewFight += fights.Add;

      var processor = new LogProcessor("dummy.txt", ctx);
      var collection = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>(), 100);
      processor.LinkTo(collection);

      // Shorter than the 28-char date prefix DoPreProcess unconditionally slices off via
      // `line[27..]`. LogReader's own HandleLine gates on Length > 28 before ever queueing a
      // line, so production never sends this today — but nothing stops a future producer bug
      // from doing so, and if it does, this must not silently kill the whole consumer.
      collection.Add(new LogReaderItem("short line", 0, false));

      var goodLine = "[Sun Jul 26 20:00:00 2026] Tester crushes a test boss for 100 points of damage.";
      var dt = DateUtil.ParseStandardDate(goodLine);
      collection.Add(new LogReaderItem(goodLine, DateUtil.ToDotNetSeconds(dt), false));
      collection.CompleteAdding();

      // Poll instead of relying on Dispose() as a sync barrier: Dispose() sets _isDisposed
      // true *before* waiting on the background task, and the consumer loop checks
      // _isDisposed on every iteration — calling Dispose() immediately can make it bail
      // before reaching the second queued item regardless of whether the fix works.
      var sw = Stopwatch.StartNew();
      while (fights.Count < 1 && sw.ElapsedMilliseconds < 3000)
      {
        Thread.Sleep(20);
      }

      processor.Dispose();

      Assert.AreEqual(1, fights.Count,
        "Consumer should keep processing lines queued after a malformed one instead of dying.");
    }
  }
}
