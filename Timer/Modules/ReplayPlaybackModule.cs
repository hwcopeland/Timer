/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using ZstdSharp;

namespace Source2Surf.Timer.Modules;

internal class ReplayPlaybackModule : IReplayPlaybackModule,
                                      IModule,
                                      IGameListener,
                                      IPlayerManagerListener,
                                      IRecordModuleListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge _bridge;
    private readonly IRecordModule _recordModule;
    private readonly IStyleModule _styleModule;
    private readonly ReplayProviderProxy _replayProviderProxy;
    private readonly IPlayerManager _playerManager;
    private readonly ILogger<ReplayPlaybackModule> _logger;

    // Replay caches
    private readonly Dictionary<(int style, int track), ReplayContent>            _replayCache      = [];
    private readonly Dictionary<(int style, int track, int stage), ReplayContent> _stageReplayCache = [];

    // Bot management
    private readonly bool                _hasNoBotParam;
    private readonly List<ReplayBotData> _replayBots = [];
    private readonly ReplayBotData?[]    _replayBotBySlot;
    private readonly ReplayBotConfig[]   _replayBotConfigs;

    // Listener hub
    private readonly ListenerHub<IReplayModuleListener> _replayListenerHub;

    // Paths
    private readonly string _replayDirectory;

    // ConVars
    // ReSharper disable InconsistentNaming
    private readonly IConVar timer_replay_delay;
    // ReSharper restore InconsistentNaming

    private CancellationTokenSource _mapRecordLoadToken = new();

    // Static bot creation flag
    private static bool _expectingBot;

    // Native hook delegate
    // ReSharper disable InconsistentNaming
    private static unsafe delegate* unmanaged<nint, int, int, nint, CStrikeWeaponType, int, bool>
        CCSBotManager_BotAddCommand;
    // ReSharper restore InconsistentNaming

    public ReplayPlaybackModule(InterfaceBridge                bridge,
                                IRecordModule                  recordModule,
                                IStyleModule                   styleModule,
                                ReplayProviderProxy            replayProviderProxy,
                                IPlayerManager                 playerManager,
                                IGameData                      gameData,
                                IInlineHookManager             inlineHookManager,
                                ILogger<ReplayPlaybackModule>  logger)
    {
        _bridge              = bridge;
        _recordModule        = recordModule;
        _styleModule         = styleModule;
        _replayProviderProxy = replayProviderProxy;
        _playerManager       = playerManager;
        _logger              = logger;

        _hasNoBotParam = bridge.ModSharp.HasCommandLine("-nobots");

        _replayListenerHub = new ListenerHub<IReplayModuleListener>(logger);

        _replayBotBySlot = new ReplayBotData?[PlayerSlot.MaxPlayerCount];

        timer_replay_delay = bridge.ConVarManager.CreateConVar("timer_replay_delay",
                                                               2.0f,
                                                               0.1f,
                                                               5.0f,
                                                               "Delay in seconds before starting the next replay after one finishes")
            !;

        unsafe
        {
            if (!inlineHookManager.AddHook("CCSBotManager::MaintainBotQuota",
                                           (nint)(delegate* unmanaged<nint, void>)(&hk_CCSBotManager_MaintainBotQuota),
                                           out _))
            {
                throw new InvalidOperationException("Failed to hook CCSBotManager::MaintainBotQuota");
            }

            CCSBotManager_BotAddCommand
                = (delegate* unmanaged<nint, int, int, nint, CStrikeWeaponType, int, bool>)
                gameData.GetAddress("CCSBotManager::BotAddCommand");
        }

        _replayDirectory = Path.Combine(bridge.TimerDataPath, "replays");

        var configDir = Path.Combine(bridge.SharpPath, "configs");
        Directory.CreateDirectory(configDir);
        var replayConfigPath = Path.Combine(configDir, "timer-replay.jsonc");

        _replayBotConfigs = ReplayShared.LoadReplayBotConfigs(replayConfigPath, logger);

        ReplayShared.EnsureReplayDirectories(_replayDirectory);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void hk_CCSBotManager_MaintainBotQuota(nint a1)
    {
    }

    public bool Init()
    {
        _replayProviderProxy.RefreshProvider();

        _bridge.ModSharp.InstallGameListener(this);

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnPlayerProcessMovementPre);
        _bridge.HookManager.PlayerProcessMovePost.InstallForward(OnPlayerProcessMovementPost);

        _playerManager.RegisterListener(this);
        _recordModule.RegisterListener(this);

        return true;
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);

        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnPlayerProcessMovementPre);
        _bridge.HookManager.PlayerProcessMovePost.RemoveForward(OnPlayerProcessMovementPost);

        _playerManager.UnregisterListener(this);
        _recordModule.UnregisterListener(this);
    }

    public void OnServerActivate()
    {
        // ReSharper disable InconsistentNaming
        if (_bridge.ConVarManager.FindConVar("bot_zombie") is { } bot_zombie)
        {
            bot_zombie.Flags &= ~ConVarFlags.Cheat;
            bot_zombie.Set("1");
        }

        if (_bridge.ConVarManager.FindConVar("bot_stop") is { } bot_stop)
        {
            bot_stop.Flags &= ~ConVarFlags.Cheat;
            bot_stop.Set("1");
        }

        // ReSharper restore InconsistentNaming

        if (_hasNoBotParam)
        {
            _logger.LogWarning("Startup param \"-nobots\" detected, bots won't be added");

            return;
        }

        _bridge.ModSharp.RepeatCallThisMap(3.0f, Timer_CheckReplayBot);
    }

    public void OnMapRecordsLoaded()
    {
        if (_hasNoBotParam)
            return;

        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_bridge.CancellationToken, _mapRecordLoadToken.Token);

        Task.Run(async () =>
        {
            try
            {
                linkedToken.Token.ThrowIfCancellationRequested();

                var stopwatch = new Stopwatch();

                var wrKeys = CollectWRKeys();
                _logger.LogInformation("Found {main} main WRs and {stage} stage WRs",
                    wrKeys.MainKeys.Count, wrKeys.StageKeys.Count);

                // Collect results into temp dictionaries to avoid writing to main-thread caches from background
                var mainResults  = new Dictionary<(int, int), ReplayContent>();
                var stageResults = new Dictionary<(int, int, int), ReplayContent>();

                stopwatch.Start();
                LoadReplaysFromDisk(wrKeys, mainResults, stageResults, linkedToken.Token);
                stopwatch.Stop();
                _logger.LogInformation("LoadReplay (disk): {elapsed}", stopwatch.Elapsed);

                stopwatch.Restart();
                await LoadMissingReplaysFromRemote(wrKeys, mainResults, stageResults, linkedToken.Token);
                stopwatch.Stop();
                _logger.LogInformation("LoadReplay (remote): {elapsed}", stopwatch.Elapsed);

                linkedToken.Token.ThrowIfCancellationRequested();

                // Flush results to main-thread caches
                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                                              {
                                                                  if (linkedToken.IsCancellationRequested)
                                                                  {
                                                                      return;
                                                                  }

                                                                  foreach (var (key, content) in mainResults)
                                                                  {
                                                                      _replayCache[key] = content;
                                                                  }

                                                                  foreach (var (key, content) in stageResults)
                                                                  {
                                                                      _stageReplayCache[key] = content;
                                                                  }
                                                              },
                                                              linkedToken.Token);

                _logger.LogInformation("Replay cache updated: {main} main, {stage} stage",
                                       mainResults.Count, stageResults.Count);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Replay loading canceled");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unexpected error during replay loading");
            }
            finally
            {
                linkedToken.Dispose();
            }
        }, linkedToken.Token);
    }

    public void OnGameShutdown()
    {
        _mapRecordLoadToken.Cancel();
        _mapRecordLoadToken.Dispose();
        _mapRecordLoadToken = new CancellationTokenSource();

        _replayBots.Clear();
        _replayCache.Clear();
        _stageReplayCache.Clear();
        Array.Clear(_replayBotBySlot, 0, _replayBotBySlot.Length);
    }

    public void OnClientPutInServer(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is null)
        {
            return;
        }

        // Only handle Bot players
        if (!client.IsFakeClient)
        {
            return;
        }

        if (!_expectingBot)
        {
            _bridge.ClientManager.KickClient(client,
                                             "no",
                                             NetworkDisconnectionReason.Kicked);

            return;
        }

        var controller = client.GetPlayerController();

        if (controller is not { IsValidEntity: true })
        {
            _logger.LogError("Failed to find bot!!!!");

            _bridge.ClientManager.KickClient(client,
                                             "no",
                                             NetworkDisconnectionReason.Kicked);

            return;
        }

        var botData = new ReplayBotData
        {
            Controller   = controller,
            Index        = controller.Index,
            Frames       = [],
            CurrentFrame = 0,
            Client       = client,
            Status       = EReplayBotStatus.Idle,
            Type         = EReplayBotType.Looping,
            Config       = _replayBotConfigs[_replayBots.Count],
        };

        _replayBots.Add(botData);
        _replayBotBySlot[slot] = botData;

        if (!botData.Config.StageBot)
        {
            FindNextReplay(botData);
        }
        else
        {
            FindNextStageReplay(botData);
        }

        StartReplay(botData);
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);

        // Only handle Bot players
        if (client is not null && client.IsFakeClient)
        {
            if (_replayBots.Find(i => i.Client.Equals(client)) is { } bot)
            {
                _replayBots.Remove(bot);
                _replayBotBySlot[slot] = null;
            }
        }
    }

    public bool OnNewMainReplaySaved(int style, int track, ReplayContent content, ReplaySaveContext context)
    {
        if (_replayCache.TryGetValue((style, track), out var existing)
            && existing.Header.Time <= context.FinishTime)
        {
            return false;
        }

        _replayCache[(style, track)] = content;
        UpdateMainReplayBots(style, track);
        return true;
    }

    public bool OnNewStageReplaySaved(int style, int track, int stage, ReplayContent content, ReplaySaveContext context)
    {
        if (_stageReplayCache.TryGetValue((style, track, stage), out var existing)
            && existing.Header.Time <= context.FinishTime)
        {
            return false;
        }

        _stageReplayCache[(style, track, stage)] = content;
        UpdateStageReplayBots(style, track, stage);
        return true;
    }

    public bool ShouldUploadReplay(ulong steamId, int style, int track, int stage, float time, bool isNewBest)
    {
        var listeners = _replayListenerHub.Snapshot;
        if (listeners.Length == 0) return isNewBest;

        foreach (var listener in listeners)
        {
            try
            {
                if (listener.ShouldUploadReplay(steamId, style, track, stage, time, isNewBest))
                    return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error in ShouldUploadReplay listener: {type}", listener.GetType().Name);
            }
        }

        return false;
    }

    public IReplayBotData? GetReplayBotData(PlayerSlot slot)
    {
        return _replayBotBySlot[slot];
    }

    public IReplayBotData? GetReplayBotByIndex(int index) =>
        (uint) index < (uint) _replayBots.Count ? _replayBots[index] : null;

    public int GetReplayBotCount() =>
        _replayBots.Count;

    public void RegisterListener(IReplayModuleListener listener)
        => _replayListenerHub.Register(listener);

    public void UnregisterListener(IReplayModuleListener listener)
        => _replayListenerHub.Unregister(listener);

    private void Timer_CheckReplayBot()
    {
        if (_replayBots.Count == 0)
        {
            AddReplayBot();

            return;
        }

        foreach (var bot in _replayBots)
        {
            if (_bridge.EntityManager.FindPlayerControllerBySlot(bot.Client.Slot) is not { IsValidEntity: true } controller)
            {
                continue;
            }

            if (controller.GetPlayerPawn() is not { IsValidEntity: true } pawn)
            {
                continue;
            }

            if (!pawn.IsAlive)
            {
                controller.Respawn();

                continue;
            }

            pawn.RemoveAllItems();
        }
    }

    private unsafe void AddReplayBot()
    {
        for (var i = 0; i < _replayBotConfigs.Length; i++)
        {
            _expectingBot = true;

            if (!CCSBotManager_BotAddCommand(0, Random.Shared.Next(2, 4), 0, 0, CStrikeWeaponType.Unknown, 0))
            {
                _logger.LogError("Failed to add bot");
            }

            _expectingBot = false;
        }
    }

    private void StartReplay(ReplayBotData bot)
    {
        bot.CurrentFrame = 0;

        var header = bot.Header;

        if (header == null || bot.Frames.Count == 0)
        {
            bot.Status = EReplayBotStatus.Idle;
        }
        else
        {
            bot.Status = EReplayBotStatus.Start;
            bot.Time   = header.Time;

            bot.Timer = _bridge.ModSharp.PushTimer(() =>
                                                   {
                                                       bot.Timer  = null;
                                                       bot.Status = EReplayBotStatus.Running;

                                                       return TimerAction.Stop;
                                                   },
                                                   timer_replay_delay.GetFloat(),
                                                   GameTimerFlags.StopOnMapEnd);
        }

        SetupReplayBotName(bot);
    }

    private void FindNextReplay(ReplayBotData bot)
    {
        var       maxStyle = _styleModule.GetStyleCount();
        const int maxTrack = TimerConstants.MAX_TRACK;
        var       total    = maxStyle * maxTrack;

        var config = bot.Config;

        var startIndex = bot.Track < 0
            ? 0
            : (bot.Track * maxStyle) + bot.Style + 1;

        // scan the next total–1 entries
        for (var step = 0; step < total; step++)
        {
            var idx = (startIndex + step) % total;

            var track = idx / maxStyle;

            if (!bot.IsTrackAllowed(track))
            {
                continue;
            }

            var style = idx % maxStyle;

            if (!config.Styles.Contains(style))
            {
                continue;
            }

            if (_replayCache.TryGetValue((style, track), out var content))
            {
                bot.Header = content.Header;
                bot.Frames = content.Frames;
                bot.Style  = style;
                bot.Time   = content.Header.Time;
                bot.Track  = track;

                return;
            }
        }

        if (bot.Track < 0)
        {
            bot.Track = 0;
            bot.Style = 0;
        }
    }

    private void FindNextStageReplay(ReplayBotData bot)
    {
        var       maxStyle = _styleModule.GetStyleCount();
        const int maxTrack = TimerConstants.MAX_TRACK;
        const int maxStage = TimerConstants.MAX_STAGE;

        var total = maxStage * maxTrack * maxStyle;

        int startIndex;

        if (bot.Track < 0)
        {
            startIndex = 0;
        }
        else
        {
            startIndex = (bot.Stage   * maxTrack * maxStyle)
                         + (bot.Track * maxStyle)
                         + bot.Style;
        }

        var config = bot.Config;

        // scan the next total–1 entries
        for (var step = 0; step < total; step++)
        {
            var idx = (startIndex + step) % total;

            var stage = idx / (maxTrack * maxStyle);

            var rem = idx % (maxTrack * maxStyle);

            var track = rem / maxStyle;

            if (!bot.IsTrackAllowed(track))
            {
                continue;
            }

            var style = rem % maxStyle;

            if (!config.Styles.Contains(style))
            {
                continue;
            }

            if (_stageReplayCache.TryGetValue((style, track, stage), out var content))
            {
                bot.Header = content.Header;
                bot.Frames = content.Frames;
                bot.Style  = style;
                bot.Track  = track;
                bot.Time   = content.Header.Time;
                bot.Stage  = stage;

                return;
            }
        }

        if (bot.Track < 0)
        {
            bot.Track = 0;
            bot.Style = 0;
        }
    }

    private void OnPlayerProcessMovementPre(Sharp.Shared.HookParams.IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;

        if (!client.IsFakeClient)
        {
            return;
        }

        if (_replayBotBySlot[client.Slot] is not { } bot)
        {
            return;
        }

        if (bot.Status != EReplayBotStatus.Running)
        {
            return;
        }

        if (bot.CurrentFrame >= bot.Frames.Count)
        {
            return;
        }

        var frame = bot.Frames[bot.CurrentFrame];

        var service = arg.Service;

        service.KeyButtons        = frame.PressedButtons;
        service.KeyChangedButtons = frame.ChangedButtons;
        service.ScrollButtons     = frame.ScrollButtons;
    }

    private unsafe void OnPlayerProcessMovementPost(Sharp.Shared.HookParams.IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;

        if (!client.IsFakeClient)
        {
            return;
        }

        if (_replayBotBySlot[client.Slot] is not { } bot)
        {
            return;
        }

        var totalFrames = bot.Frames.Count;

        var pawn = arg.Pawn;

        if (bot.Status == EReplayBotStatus.Idle)
        {
            pawn.SetMoveType(MoveType.None);

            return;
        }

        if (bot.CurrentFrame >= totalFrames && bot.Status != EReplayBotStatus.End)
        {
            bot.Status = EReplayBotStatus.End;

            bot.Timer = _bridge.ModSharp.PushTimer(() =>
                                                   {
                                                       bot.Timer = null;

                                                       if (bot.Type == EReplayBotType.Looping)
                                                       {
                                                           if (bot.Config.StageBot)
                                                           {
                                                               FindNextStageReplay(bot);
                                                           }
                                                           else
                                                           {
                                                               FindNextReplay(bot);
                                                           }

                                                           StartReplay(bot);
                                                       }
                                                       else
                                                       {
                                                           bot.Controller.Respawn();
                                                       }

                                                       return TimerAction.Stop;
                                                   },
                                                   timer_replay_delay.GetFloat() / 2.0f,
                                                   GameTimerFlags.StopOnMapEnd);
        }

        var ending = bot.Status == EReplayBotStatus.End;

        var curFrame = ending ? totalFrames - 1 : bot.CurrentFrame;

        var mv = arg.Info;

        var frame  = bot.Frames[curFrame];
        var angles = frame.Angles;
        mv->ViewAngles = new (angles.X, angles.Y, 0);
        mv->Angles     = mv->ViewAngles;

        var curPos = frame.Origin;
        mv->AbsOrigin = curPos;

        var flags = pawn.Flags;

        flags |= EntityFlags.AtControls;
        flags &= ~(EntityFlags.OnGround | EntityFlags.Fly | EntityFlags.WaterJump);

        pawn.Flags = flags;

        pawn.SetMoveType(bot.Status == EReplayBotStatus.Running ? frame.MoveType : MoveType.None);

        mv->AbsOrigin = curPos;
        mv->Velocity  = frame.Velocity;

        if (bot.Status == EReplayBotStatus.Running)
        {
            bot.CurrentFrame++;
        }
    }

    private void SetupReplayBotName(ReplayBotData bot)
    {
        var config = bot.Config;

        var name = bot.Status == EReplayBotStatus.Idle ? config.IdleName : config.Name;

        var trackStr   = string.Empty;
        var stageStr   = string.Empty;
        var timeStr    = string.Empty;
        var styleStr   = string.Empty;

        if (bot.Status == EReplayBotStatus.Idle)
        {
            trackStr = config.PlayType switch
            {
                EReplayBotPlayType.All       => Utils.GetTrackName(Math.Max(0, bot.Track), true),
                EReplayBotPlayType.MainOnly  => "Main",
                EReplayBotPlayType.BonusOnly => "Bonus",
                _                            => trackStr,
            };
        }
        else
        {
            trackStr = Utils.GetTrackName(bot.Track);

            if (config.StageBot)
            {
                stageStr = $" Stage {bot.Stage}";
            }

            timeStr  = Utils.FormatTime(bot.Header!.Time, true);
            styleStr = _styleModule.GetStyleSetting(bot.Style).Name;
        }

        name = name.Replace("{track}", trackStr, StringComparison.OrdinalIgnoreCase)
                   .Replace("{stage}", stageStr, StringComparison.OrdinalIgnoreCase)
                   .Replace("{style}", styleStr, StringComparison.OrdinalIgnoreCase)
                   .Replace("{time}",  timeStr,  StringComparison.OrdinalIgnoreCase);

        bot.Client.SetName(name);
    }

    private void UpdateMainReplayBots(int style, int track)
    {
        if (!_replayCache.TryGetValue((style, track), out var replayContent))
        {
            return;
        }

        foreach (var bot in _replayBots)
        {
            if (!IsMainReplayBotMatch(bot, style, track))
            {
                continue;
            }

            bot.Frames = replayContent.Frames;
            bot.Header = replayContent.Header;
            StartReplay(bot);
        }
    }

    private void UpdateStageReplayBots(int style, int track, int stage)
    {
        if (!_stageReplayCache.TryGetValue((style, track, stage), out var content))
        {
            return;
        }

        foreach (var bot in _replayBots)
        {
            if (!IsStageReplayBotMatch(bot, style, track, stage))
            {
                continue;
            }

            bot.Frames = content.Frames;
            bot.Header = content.Header;
            bot.Stage  = stage;
            StartReplay(bot);
        }
    }

    private static bool IsMainReplayBotMatch(ReplayBotData bot, int style, int track)
        => (bot.Style    == style || bot.Style < 0)
           && (bot.Track == track || bot.Track < 0)
           && !bot.Config.StageBot;

    private static bool IsStageReplayBotMatch(ReplayBotData bot, int style, int track, int stage)
        => (bot.Style    == style || bot.Style < 0)
           && (bot.Track == track || bot.Track < 0)
           && bot.Config.StageBot
           && bot.Stage == stage;

    private readonly record struct WRKeySet(
        List<(int style, int track, RunRecord wr)> MainKeys,
        List<(int style, int track, int stage, RunRecord wr)> StageKeys
    );

    private WRKeySet CollectWRKeys()
    {
        var mainKeys  = new List<(int style, int track, RunRecord wr)>();
        var stageKeys = new List<(int style, int track, int stage, RunRecord wr)>();

        var styleCount = _styleModule.GetStyleCount();

        for (var style = 0; style < styleCount; style++)
        {
            for (var track = 0; track < TimerConstants.MAX_TRACK; track++)
            {
                if (_recordModule.GetWR(style, track) is { } wr)
                {
                    mainKeys.Add((style, track, wr));
                }

                for (var stage = 1; stage < TimerConstants.MAX_STAGE; stage++)
                {
                    if (_recordModule.GetWR(style, track, stage) is { } stageWr)
                    {
                        stageKeys.Add((style, track, stage, stageWr));
                    }
                }
            }
        }

        return new WRKeySet(mainKeys, stageKeys);
    }

    private void LoadReplaysFromDisk(WRKeySet wrKeys,
                                     Dictionary<(int, int), ReplayContent> mainResults,
                                     Dictionary<(int, int, int), ReplayContent> stageResults,
                                     CancellationToken token)
    {
        var mapName = _bridge.GlobalVars.MapName;

        Parallel.ForEach(wrKeys.MainKeys,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new Decompressor(),
            (key, _, decompressor) =>
            {
                var (style, track, wr) = key;
                var filePath = ReplayShared.BuildMainReplayPath(_replayDirectory, mapName, style, track, wr.Id);

                if (File.Exists(filePath)
                    && ReplayShared.LoadReplayFromPath(filePath, style, track, 0, decompressor, _logger) is { } result)
                {
                    lock (mainResults)
                    {
                        mainResults[(style, track)] = result.Content;
                    }
                }

                return decompressor;
            },
            decompressor => decompressor.Dispose());

        Parallel.ForEach(wrKeys.StageKeys,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new Decompressor(),
            (key, _, decompressor) =>
            {
                var (style, track, stage, wr) = key;
                var filePath = ReplayShared.BuildStageReplayPath(_replayDirectory, mapName, style, track, stage, wr.Id);

                if (File.Exists(filePath)
                    && ReplayShared.LoadReplayFromPath(filePath, style, track, stage, decompressor, _logger) is { } result)
                {
                    lock (stageResults)
                    {
                        stageResults[(style, track, stage)] = result.Content;
                    }
                }

                return decompressor;
            },
            decompressor => decompressor.Dispose());
    }

    private async Task LoadMissingReplaysFromRemote(
        WRKeySet wrKeys,
        Dictionary<(int, int), ReplayContent> mainResults,
        Dictionary<(int, int, int), ReplayContent> stageResults,
        CancellationToken token)
    {
        if (!_replayProviderProxy.IsAvailable) return;

        var mapName = _bridge.GlobalVars.MapName;
        var maxConcurrency = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = new List<Task>();

        foreach (var (style, track, _) in wrKeys.MainKeys)
        {
            token.ThrowIfCancellationRequested();

            if (mainResults.ContainsKey((style, track))) continue;
            tasks.Add(LoadSingleRemoteReplay(semaphore, mapName, style, track, mainResults, token));
        }

        foreach (var (style, track, stage, _) in wrKeys.StageKeys)
        {
            token.ThrowIfCancellationRequested();

            if (stageResults.ContainsKey((style, track, stage))) continue;
            tasks.Add(LoadSingleRemoteStageReplay(semaphore, mapName, style, track, stage, stageResults, token));
        }

        if (tasks.Count > 0)
        {
            _logger.LogInformation("Loading {count} missing replays from remote", tasks.Count);
            await Task.WhenAll(tasks).WaitAsync(token);
        }
    }

    private async Task LoadSingleRemoteReplay(
        SemaphoreSlim semaphore, string mapName, int style, int track,
        Dictionary<(int, int), ReplayContent> results,
        CancellationToken token)
    {
        await semaphore.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();

            var bytes = await _replayProviderProxy.GetReplayAsync(mapName, style, track);

            token.ThrowIfCancellationRequested();

            if (bytes != null && ReplayShared.DeserializeReplay(bytes, style, track, 0, _logger) is { } result)
            {
                lock (results)
                {
                    results[(style, track)] = result.Content;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load remote replay for style={style} track={track}", style, track);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task LoadSingleRemoteStageReplay(
        SemaphoreSlim semaphore, string mapName, int style, int track, int stage,
        Dictionary<(int, int, int), ReplayContent> results,
        CancellationToken token)
    {
        await semaphore.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();

            var bytes = await _replayProviderProxy.GetStageReplayAsync(mapName, style, track, stage);

            token.ThrowIfCancellationRequested();

            if (bytes != null && ReplayShared.DeserializeReplay(bytes, style, track, stage, _logger) is { } result)
            {
                lock (results)
                {
                    results[(style, track, stage)] = result.Content;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load remote stage replay for style={style} track={track} stage={stage}", style, track, stage);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
