using System;

namespace EQLogParser
{
  internal class PreLineParser
  {
    internal static PreLineParser Instance { get; } = new();

    private readonly PlayerManager _playerManager;

    public PreLineParser() : this(PlayerManager.Instance) { }

    public PreLineParser(PlayerManager playerManager)
    {
      _playerManager = playerManager;
    }

    // Process things that can easily identify a player
    internal bool NeedProcessing(LineData lineData)
    {
      var found = false;
      var action = lineData.Action;

      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (Player)", StringComparison.OrdinalIgnoreCase))
        {
          _playerManager.AddVerifiedPlayer(action[19..], lineData.BeginTime);
          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" joined the raid.", StringComparison.OrdinalIgnoreCase) && !action.StartsWith("You have", StringComparison.OrdinalIgnoreCase))
        {
          if (PlayerManager.IsPossiblePlayerName(action, action.Length - 17))
          {
            var test = action[..^17];
            _playerManager.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^22];
          if (PlayerManager.IsPossiblePlayerName(test))
          {
            _playerManager.AddVerifiedPlayer(test, lineData.BeginTime);
          }
          else
          {
            _playerManager.AddMerc(test);
          }

          found = true;
        }
        else if (action.EndsWith(" has left the raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^19];
          if (PlayerManager.IsPossiblePlayerName(test))
          {
            _playerManager.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^20];
          if (PlayerManager.IsPossiblePlayerName(test))
          {
            _playerManager.AddVerifiedPlayer(test, lineData.BeginTime);
          }
          else
          {
            _playerManager.AddMerc(test);
          }

          found = true;
        }
        else if (action.EndsWith(" is now the leader of your raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^32];
          if (PlayerManager.IsPossiblePlayerName(test))
          {
            _playerManager.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.StartsWith("Glug, glug, glug...  ", StringComparison.OrdinalIgnoreCase))
        {
          var end = PlayerManager.FindPossiblePlayerName(action, out var isCrossServer, 21, -1, ' ');
          if (end != -1 && !isCrossServer && action.AsSpan()[end..].StartsWith(" takes a drink ", StringComparison.OrdinalIgnoreCase))
          {
            _playerManager.AddVerifiedPlayer(action[21..end], lineData.BeginTime);
            found = true;
          }
        }
      }

      return !found;
    }
  }
}
