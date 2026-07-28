using log4net;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class LogProcessor : ILogProcessor
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly string _fileName;
    private readonly ParseContext _context;
    private long _lineCount;
    private Task _readTask;
    private volatile bool _isDisposed;

    // Default ctor delegates to the live singleton context. Background parse flows construct
    // their own NpcDamageManager and pass a custom ParseContext.
    internal LogProcessor(string fileName) : this(fileName, ParseContext.Live(null)) { }

    internal LogProcessor(string fileName, ParseContext context)
    {
      _fileName = fileName ?? string.Empty;
      _context = context;
    }

    // Synchronous entry point for background parse flows (e.g. Raid Damage window loading an
    // exported log). Reuses DoPreProcess so chat skipping, double-line splitting, and the
    // parser chain all behave the same as the live tailing flow.
    internal void ProcessSync(string line, double dateTime)
    {
      DoPreProcess(line, dateTime, false);
    }

    public void LinkTo(BlockingCollection<LogReaderItem> collection)
    {
      // start archive if enabled
      ChatDB.Instance.Init();

      _readTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in collection.GetConsumingEnumerable())
          {
            if (_isDisposed) break;

            try
            {
              DoPreProcess(data.Line, data.Ts, data.IsMonitor);
            }
            catch (Exception ex)
            {
              // Defense in depth: a single malformed line must never kill this consumer.
              // LogReader.HandleLine keeps queueing into a *bounded* BlockingCollection —
              // if this loop dies, the queue fills and the reader blocks on Add() forever,
              // which surfaces as the app frozen at a fixed "Reading Log.." percentage with
              // no error shown. Log and move on to the next queued line instead.
              Log.Error($"Error processing log line, skipping: {data.Line}", ex);
            }
          }
        }
        catch (Exception ex)
        {
          Log.Error("Problem loading log file.", ex);
        }
        finally
        {
          collection?.Dispose();
        }
      });
    }

    private void DoPreProcess(string line, double dateTime, bool monitor)
    {
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime, LineNumber = _lineCount };

      // avoid having other things parse chat by accident
      if (ChatLineParser.ParseChatType(lineData.Action) is { } chatType)
      {
        chatType.BeginTime = lineData.BeginTime;
        chatType.Text = line; // workaround for now?
        ChatDB.Instance.Add(chatType);

        if (!monitor)
        {
          TriggerUtil.CheckQuickShare(chatType, lineData.Action, lineData.BeginTime, false, TriggerStateDB.DefaultUser);
        }
      }
      else
      {
        string doubleLine = null;
        double extraDouble = 0;

        // only if it's not a chat line check if two lines are on the same line
        if (lineData.Action.IndexOf('[') is var index and > -1 && lineData.Action.Length > (index + 28) &&
            lineData.Action[index + 25] == ']' && char.IsDigit(lineData.Action[index + 24]))
        {
          var original = lineData.Action;
          lineData.Action = original[..index];
          doubleLine = original[index..];

          if (DateUtil.ParseStandardDate(doubleLine) is var newDate && newDate != DateTime.MinValue)
          {
            extraDouble = DateUtil.ToDotNetSeconds(newDate);
          }
        }

        if (_context.PreLineParser.NeedProcessing(lineData))
        {
          // may as well split once if most things use it
          lineData.Split = lineData.Action.Split(' ');
          if (!_context.DamageLineParser.Process(lineData))
          {
            if (!_context.HealingLineParser.Process(lineData))
            {
              if (!_context.MiscLineParser.Process(lineData))
              {
                _context.CastLineParser.Process(lineData);
              }
            }
          }

          _lineCount++;
        }

        if (doubleLine != null)
        {
          DoPreProcess(doubleLine, extraDouble, monitor);
        }
      }
    }

    public void Dispose()
    {
      if (!_isDisposed)
      {
        _isDisposed = true;

        try
        {
          _readTask.Wait(2000);
        }
        catch (Exception ex)
        {
          Log.Error("Log reading task not completed.", ex);
        }
      }
    }
  }
}
