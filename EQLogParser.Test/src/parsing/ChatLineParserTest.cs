using EQLogParser;

namespace EQLogParserTest.Parsing
{
  [TestClass]
  public class ChatLineParserTest
  {
    // Regression for the Ezran raid-log hang (2026-07-27): the EQ client bounces an
    // accidental "/tell <word>" with 'You told <word>, '<word> is not online at this
    // time''. When that stray word contains an apostrophe before any whitespace/comma
    // (e.g. "I'm"), MatchTellPlayer hit the quote char while wsIndex was still -1 and
    // sliced span[..-1], throwing ArgumentOutOfRangeException. That exception was
    // uncaught in LogProcessor's background consumer task, which silently died while
    // LogReader kept queueing lines into the bounded BlockingCollection — filling it and
    // blocking the reader forever (observed as the app hanging at a fixed "Reading Log.."
    // percentage indefinitely).
    [TestMethod]
    public void ParseChatType_TellBounceWithApostropheBeforeSeparator_DoesNotThrow()
    {
      var action = "You told I'm, 'I'm is not online at this time'";

      var result = ChatLineParser.ParseChatType(action);

      // The malformed receiver field means this doesn't cleanly parse as a Tell — the
      // important behavior under test is that it returns (possibly null) instead of throwing.
      Assert.IsTrue(result == null || result.Channel == ChatChannels.Tell);
    }

    [TestMethod]
    public void ParseChatType_NormalTell_StillParsesReceiver()
    {
      var action = "You told Hoggle, 'hello there'";

      var result = ChatLineParser.ParseChatType(action);

      Assert.IsNotNull(result);
      Assert.AreEqual(ChatChannels.Tell, result.Channel);
      Assert.AreEqual("Hoggle", result.Receiver);
    }
  }
}
