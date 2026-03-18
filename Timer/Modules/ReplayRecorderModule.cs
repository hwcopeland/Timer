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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

internal class ReplayRecorderModule : IReplayRecorderModule,
                                      IModule,
                                      IGameListener,
                                      IRecordModuleListener,
                                      ITimerModuleListener,
                                      IPlayerManagerListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private const int MinValidFrames = (int) (TimerConstants.Tickrate * 0.7f);

    private readonly InterfaceBridge               _bridge;
    private readonly ITimerModule                  _timerModule;
    private readonly IRecordModule                 _recordModule;
    private readonly IReplayPlaybackModule         _playbackModule;
    private readonly IPlayerManager                _playerManager;
    private readonly ReplayProviderProxy           _replayProviderProxy;
    private readonly IMapInfoModule                _mapInfoModule;
    private readonly ILogger<ReplayRecorderModule> _logger;

    // Player frame data array indexed by PlayerSlot
    private readonly PlayerFrameData?[] _playerFrameData;

    private readonly PendingReplayStore _pendingReplayStore = new ();

    private readonly Dictionary<ReplayMatchKey, FallbackReplayRecord> _fallbackRecords = [];

    // Auto-increment AttemptId counter; overflow wraps harmlessly.
    private int _nextAttemptId;

    private readonly string _replayDirectory;

    // ConVars (recording-related)
    // ReSharper disable InconsistentNaming
    private readonly IConVar timer_replay_prerun_time;
    private readonly IConVar timer_replay_postrun_time;
    private readonly IConVar timer_replay_stage_prerun_time;
    private readonly IConVar timer_replay_stage_postrun_time;
    private readonly IConVar timer_replay_file_compression_level;
    private readonly IConVar timer_replay_file_compression_workers;
    private readonly IConVar timer_replay_remote_upload;
    private readonly IConVar timer_replay_pending_timeout;

    // ReSharper restore InconsistentNaming

    public ReplayRecorderModule(InterfaceBridge               bridge,
                                ITimerModule                  timerModule,
                                IRecordModule                 recordModule,
                                IReplayPlaybackModule         playbackModule,
                                IPlayerManager                playerManager,
                                ReplayProviderProxy           replayProviderProxy,
                                IMapInfoModule                mapInfoModule,
                                ILogger<ReplayRecorderModule> logger)
    {
        _bridge              = bridge;
        _timerModule         = timerModule;
        _recordModule        = recordModule;
        _playbackModule      = playbackModule;
        _playerManager       = playerManager;
        _replayProviderProxy = replayProviderProxy;
        _mapInfoModule       = mapInfoModule;
        _logger              = logger;

        _playerFrameData = new PlayerFrameData?[PlayerSlot.MaxPlayerCount];

        timer_replay_prerun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_prerun_time", 2.0f, 2.0f, 10.0f, "Seconds of player data to record before leaving the start zone")!;

        timer_replay_postrun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_postrun_time", 2.0f, 2.0f, 10.0f, "Seconds of player data to record after finishing a run")!;

        timer_replay_stage_prerun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_stage_prerun_time", 2.0f, 0.0f, 10.0f, "Seconds of player data to record before leaving a stage start zone")!;

        timer_replay_stage_postrun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_stage_postrun_time", 2.0f, 0.0f, 10.0f, "Seconds of player data to record after finishing a stage")!;

        timer_replay_file_compression_level
            = bridge.ConVarManager.CreateConVar("timer_replay_file_compression_level", 3, 0, 19, "Replay file compression level, 0 to disable compression")!;

        timer_replay_file_compression_workers
            = bridge.ConVarManager.CreateConVar("timer_replay_file_compression_workers", 4, 0, 256, "Number of threads for replay file compression, 0 to disable")!;

        timer_replay_remote_upload
            = bridge.ConVarManager.CreateConVar("timer_replay_remote_upload", false, "Enable remote replay uploading")!;

        timer_replay_pending_timeout
            = bridge.ConVarManager.CreateConVar("timer_replay_pending_timeout",
                                                30.0f,
                                                5.0f,
                                                300.0f,
                                                "Timeout in seconds for pending replay before fallback save")!;

        _replayDirectory = Path.Combine(bridge.TimerDataPath, "replays");
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);

        _bridge.ModSharp.InstallGameListener(this);

        _playerManager.RegisterListener(this);

        _timerModule.RegisterListener(this);

        _recordModule.RegisterListener(this);

        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);

        _bridge.ModSharp.RemoveGameListener(this);

        _playerManager.UnregisterListener(this);

        _timerModule.UnregisterListener(this);

        _recordModule.UnregisterListener(this);
    }

    public void OnServerActivate()
    {
        // TTL cleanup: only remove entries older than 10 minutes.
        var                   expiryCutoff = DateTime.UtcNow.AddMinutes(-10);
        List<ReplayMatchKey>? expiredKeys  = null;

        foreach (var (key, record) in _fallbackRecords)
        {
            if (record.CreatedAt < expiryCutoff)
            {
                expiredKeys ??= [];
                expiredKeys.Add(key);
            }
        }

        if (expiredKeys is not null)
        {
            foreach (var key in expiredKeys)
            {
                _fallbackRecords.Remove(key);

                _logger.LogWarning("Removed expired fallback record on map activate for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}",
                                   key.SteamId,
                                   key.Style,
                                   key.Track,
                                   key.Stage,
                                   key.AttemptId);
            }
        }

        // Orphaned temp file cleanup
        try
        {
            if (Directory.Exists(_replayDirectory))
            {
                var cutoff = DateTime.UtcNow.AddHours(-24);

                foreach (var tmpFile in Directory.GetFiles(_replayDirectory, "*.tmp", SearchOption.AllDirectories))
                {
                    try
                    {
                        var creationTime = File.GetCreationTimeUtc(tmpFile);

                        if (creationTime < cutoff)
                        {
                            File.Delete(tmpFile);
                            _logger.LogInformation("Deleted orphaned temp replay file: {Path}", tmpFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned temp file: {Path}", tmpFile);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan for orphaned temp replay files in {Dir}", _replayDirectory);
        }
    }

    public void OnGameShutdown()
    {
        var allPending = _pendingReplayStore.TakeAll();

        foreach (var (key, pending) in allPending)
        {
            SavePendingReplayAsFallback(key, pending);
        }
    }

    public int GetAttemptId(PlayerSlot slot) =>
        _playerFrameData[slot]?.AttemptId ?? 0;

    public void OnClientPutInServer(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is null)
        {
            return;
        }

        if (client.IsFakeClient)
        {
            return;
        }

        var data = new PlayerFrameData
        {
            Frames = new ReplayFrameBuffer(TimerConstants.Tickrate * 60 * 5), SteamId = client.SteamId, Name = client.Name,
        };

        _playerFrameData[slot] = data;
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is not null && client.IsFakeClient)
        {
            return;
        }

        if (_playerFrameData[slot] is not { } frame)
        {
            return;
        }

        if (frame.StagePostFrameTimer is { } stagePostFrameTimer)
        {
            _bridge.ModSharp.StopTimer(stagePostFrameTimer);
            frame.StagePostFrameTimer = null;
        }

        if (frame.PostFrameTimer is { } postFrameTimer)
        {
            _bridge.ModSharp.StopTimer(postFrameTimer);
            frame.PostFrameTimer = null;
        }

        _playerFrameData[slot] = null;
    }

    private bool TryGetFrameData(PlayerSlot slot, [NotNullWhen(true)] out PlayerFrameData? frameData)
    {
        frameData = _playerFrameData[slot];

        return frameData is not null;
    }

    private bool TryGetStageStartTick(PlayerFrameData frameData, int stageIndex, out int startTick)
    {
        if (stageIndex < frameData.StageTimerStartTicks.Count)
        {
            startTick = frameData.StageTimerStartTicks[stageIndex];

            return true;
        }

        startTick = 0;

        _logger.LogWarning("Stage start tick missing for stage index {StageIndex}. Current count: {Count}",
                           stageIndex,
                           frameData.StageTimerStartTicks.Count);

        return false;
    }

    private void SetStageTimerStart(PlayerFrameData frameData, int stageIndex, int currentFrame, int stageNumber)
    {
        var ticksList = frameData.StageTimerStartTicks;
        var count     = ticksList.Count;

        if (count == stageIndex)
        {
            ticksList.Add(currentFrame);

            return;
        }

        if (stageIndex < count)
        {
            ticksList[stageIndex] = currentFrame;

            return;
        }

        _logger.LogError("Attempted to add CurrentFrame to StageTimerStartTick for stage {Stage} (index {Index}) "
                         + "when current stage count is {Count}. Probable logic error elsewhere.",
                         stageNumber,
                         stageIndex,
                         count);
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        // StopTimer triggers ForceCallOnStop → snapshot creation
        if (frameData.PostFrameTimer is { } postFrameTimer)
        {
            _bridge.ModSharp.StopTimer(postFrameTimer);
            frameData.PostFrameTimer = null;
        }

        frameData.AttemptId = _nextAttemptId++;

        frameData.PendingMainRecordResult = null;
        frameData.PendingStageRecordResults.Clear();

        var maxPreFrame = (int) (timer_replay_prerun_time.GetFloat() * TimerConstants.Tickrate);
        ReplayShared.TrimPreRunFrames(frameData, maxPreFrame);

        frameData.NewStageTicks.Clear();
        frameData.StageTimerStartTicks.Clear();

        frameData.TimerStartFrame = frameData.Frames.Count;
    }

    public void OnPlayerStageTimerStart(IPlayerController controller,
                                        IPlayerPawn       pawn,
                                        IStageTimerInfo   stageTimerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        var stage = stageTimerInfo.Stage;
        var idx   = stage - 1;

        SetStageTimerStart(frameData, idx, frameData.Frames.Count, stage);
    }

    public void OnPlayerStageTimerFinish(IPlayerController controller,
                                         IPlayerPawn       pawn,
                                         IStageTimerInfo   stageTimerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frame))
        {
            return;
        }

        frame.NewStageTicks.Add(frame.Frames.Count);

        frame.Name = controller.PlayerName;
        var finishedStage = stageTimerInfo.Stage;

        var lastStage = finishedStage - 1;

        if (!TryGetStageStartTick(frame, lastStage, out var timerStartTick))
        {
            return;
        }

        var time = stageTimerInfo.Time;

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var newStageTicks = frame.NewStageTicks[lastStage];

            var delay              = timer_replay_stage_postrun_time.GetFloat();
            var postRunFrameLength = (int) (TimerConstants.Tickrate * delay);
            var preRunFrameLength  = (int) (TimerConstants.Tickrate * timer_replay_stage_prerun_time.GetFloat());

            if (frame.StagePostFrameTimer is { } stageReplayTimer)
            {
                // we have ForceCallOnStop flag which forces firing the callback
                _bridge.ModSharp.StopTimer(stageReplayTimer);
            }

            frame.StagePostFrameTimer = _bridge.ModSharp.PushTimer(() =>
                                                                   {
                                                                       var startTick = Math.Max(0,
                                                                           timerStartTick - preRunFrameLength);

                                                                       CreateAndStoreStageReplay(frame,
                                                                           startTick,
                                                                           timerStartTick,
                                                                           newStageTicks,
                                                                           postRunFrameLength,
                                                                           finishedStage,
                                                                           time);

                                                                       return TimerAction.Stop;
                                                                   },
                                                                   delay,
                                                                   GameTimerFlags.StopOnMapEnd
                                                                   | GameTimerFlags.ForceCallOnStop);
        });
    }

    public void OnPlayerFinishMap(IPlayerController controller,
                                  IPlayerPawn       pawn,
                                  ITimerInfo        timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frame))
        {
            return;
        }

        frame.Name              = controller.PlayerName;
        frame.TimerFinishFrame  = frame.Frames.Count;
        frame.GrabbingPostFrame = true;
        frame.FinishTime        = timerInfo.Time;
        frame.Style             = timerInfo.Style;
        frame.Track             = timerInfo.Track;

        frame.PostFrameTimer = _bridge.ModSharp.PushTimer(() =>
                                                          {
                                                              frame.PostFrameTimer    = null;
                                                              frame.GrabbingPostFrame = false;

                                                              if (frame.StagePostFrameTimer is { } stagePostFrameTimer)
                                                              {
                                                                  _bridge.ModSharp.StopTimer(stagePostFrameTimer);
                                                              }

                                                              frame.StagePostFrameTimer = null;
                                                              CreateAndStorePendingReplay(slot, frame);

                                                              return TimerAction.Stop;
                                                          },
                                                          timer_replay_postrun_time.GetFloat(),
                                                          GameTimerFlags.StopOnMapEnd | GameTimerFlags.ForceCallOnStop);
    }

    public void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
        var key = new ReplayMatchKey(recordEvent.MapId,
                                     recordEvent.SteamId.AsPrimitive(),
                                     recordEvent.Style,
                                     recordEvent.Track,
                                     recordEvent.Stage,
                                     recordEvent.AttemptId);

        var runId = recordEvent.SavedRecord.Id;

        // 1. Try PendingReplayStore first.
        var pending = _pendingReplayStore.TakeMatch(key);

        if (pending is not null)
        {
            ProcessPendingReplay(pending, key, runId, recordEvent);

            return;
        }

        // 2. Try _fallbackRecords.
        if (_fallbackRecords.Remove(key, out var fallback))
        {
            ProcessFallbackRecord(fallback, key, runId, recordEvent);

            return;
        }

        // 3. Record arrived before post-frame ended — store into PlayerFrameData.
        var client = _bridge.ClientManager.GetGameClient(recordEvent.SteamId);

        if (client is null)
        {
            return;
        }

        var slot = client.Slot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        if (frameData.AttemptId != recordEvent.AttemptId)
        {
            return;
        }

        var result = new PendingRecordResult { RunId = runId, RecordEvent = recordEvent };

        if (recordEvent.IsStageRecord)
        {
            frameData.PendingStageRecordResults[recordEvent.Stage] = result;
        }
        else
        {
            frameData.PendingMainRecordResult = result;
        }
    }

    /// <summary>
    ///     Creates a main replay snapshot, then either processes it directly
    ///     (if PendingMainRecordResult is set) or stores it in PendingReplayStore
    ///     with a timeout timer.
    /// </summary>
    private void CreateAndStorePendingReplay(PlayerSlot slot, PlayerFrameData frame)
    {
        var style = frame.Style;
        var track = frame.Track;

        var snapshot = ReplayShared.CreateMainReplaySnapshot(frame);

        if (snapshot.Frames.Count < MinValidFrames)
        {
            _logger.LogDebug("Discarding main replay snapshot for {SteamId} style={Style} track={Track}: "
                             + "only {FrameCount} frames (min {MinFrames})",
                             frame.SteamId,
                             style,
                             track,
                             snapshot.Frames.Count,
                             MinValidFrames);

            return;
        }

        // Temp file in same directory as final → atomic same-partition File.Move
        var tempPath = ReplayShared.BuildMainReplayPath(_replayDirectory,
                                                        _bridge.GlobalVars.MapName,
                                                        style,
                                                        track,
                                                        null);

        tempPath = Path.ChangeExtension(tempPath, ".tmp");

        var mapId = _mapInfoModule.GetCurrentMapProfile().MapId;

        var key = new ReplayMatchKey(mapId,
                                     frame.SteamId.AsPrimitive(),
                                     style,
                                     track,
                                     0,
                                     frame.AttemptId);

        // Record arrived before post-frame ended — process directly.
        if (frame.PendingMainRecordResult is { } pendingResult)
        {
            frame.PendingMainRecordResult = null;

            var pending = new PendingReplay { Snapshot = snapshot, TempFilePath = tempPath };

            ProcessPendingReplay(pending, key, pendingResult.RunId, pendingResult.RecordEvent);

            return;
        }

        var pendingReplay = new PendingReplay { Snapshot = snapshot, TempFilePath = tempPath };

        var replaced = _pendingReplayStore.Add(key, pendingReplay);

        if (replaced is not null)
        {
            _logger.LogWarning("Replaced existing PendingReplay for key {Key}", key);
            SavePendingReplayAsFallback(key, replaced);
        }

        var timeoutSeconds = timer_replay_pending_timeout.GetFloat();
        var capturedKey = key;

        var timerId = _bridge.ModSharp.PushTimer(() =>
                                                 {
                                                     var timedOut = _pendingReplayStore.TakeMatch(capturedKey);

                                                     if (timedOut is not null)
                                                     {
                                                         timedOut.TimeoutTimerId = null;

                                                         _logger
                                                             .LogWarning("Pending replay timed out for {SteamId} style={Style} track={Track} attemptId={AttemptId}",
                                                                         capturedKey.SteamId,
                                                                         capturedKey.Style,
                                                                         capturedKey.Track,
                                                                         capturedKey.AttemptId);

                                                         SavePendingReplayAsFallback(capturedKey, timedOut);
                                                     }

                                                     return TimerAction.Stop;
                                                 },
                                                 timeoutSeconds,
                                                 GameTimerFlags.StopOnMapEnd);

        pendingReplay.TimeoutTimerId = timerId;
    }

    private void CreateAndStoreStageReplay(PlayerFrameData frame,
                                           int             startTick,
                                           int             stageStartFrame,
                                           int             stageFinishFrame,
                                           int             postRunFrameCount,
                                           int             stage,
                                           float           finishTime)
    {
        frame.StagePostFrameTimer = null;

        var style = frame.Style;
        var track = frame.Track;

        var snapshot = ReplayShared.CreateStageReplaySnapshot(frame,
                                                              startTick,
                                                              stageStartFrame,
                                                              stageFinishFrame,
                                                              postRunFrameCount,
                                                              finishTime);

        if (snapshot.Frames.Count < MinValidFrames)
        {
            _logger.LogDebug("Discarding stage replay snapshot for {SteamId} style={Style} track={Track} stage={Stage}: "
                             + "only {FrameCount} frames (min {MinFrames})",
                             frame.SteamId,
                             style,
                             track,
                             stage,
                             snapshot.Frames.Count,
                             MinValidFrames);

            return;
        }

        var tempPath = ReplayShared.BuildStageReplayPath(_replayDirectory,
                                                         _bridge.GlobalVars.MapName,
                                                         style,
                                                         track,
                                                         stage,
                                                         null);

        tempPath = Path.ChangeExtension(tempPath, ".tmp");

        var mapId = _mapInfoModule.GetCurrentMapProfile().MapId;

        var key = new ReplayMatchKey(mapId,
                                     frame.SteamId.AsPrimitive(),
                                     style,
                                     track,
                                     stage,
                                     frame.AttemptId);

        if (frame.PendingStageRecordResults.TryGetValue(stage, out var pendingResult))
        {
            var pending = new PendingReplay { Snapshot = snapshot, TempFilePath = tempPath };

            ProcessPendingReplay(pending, key, pendingResult.RunId, pendingResult.RecordEvent);

            return;
        }

        var pendingReplay = new PendingReplay { Snapshot = snapshot, TempFilePath = tempPath };

        var replaced = _pendingReplayStore.Add(key, pendingReplay);

        if (replaced is not null)
        {
            _logger.LogWarning("Replaced existing stage PendingReplay for key {Key}", key);
            SavePendingReplayAsFallback(key, replaced);
        }

        var timeoutSeconds = timer_replay_pending_timeout.GetFloat();
        var capturedKey = key;

        var timerId = _bridge.ModSharp.PushTimer(() =>
                                                 {
                                                     var timedOut = _pendingReplayStore.TakeMatch(capturedKey);

                                                     if (timedOut is not null)
                                                     {
                                                         timedOut.TimeoutTimerId = null;

                                                         _logger
                                                             .LogWarning("Pending stage replay timed out for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}",
                                                                         capturedKey.SteamId,
                                                                         capturedKey.Style,
                                                                         capturedKey.Track,
                                                                         capturedKey.Stage,
                                                                         capturedKey.AttemptId);

                                                         SavePendingReplayAsFallback(capturedKey, timedOut);
                                                     }

                                                     return TimerAction.Stop;
                                                 },
                                                 timeoutSeconds,
                                                 GameTimerFlags.StopOnMapEnd);

        pendingReplay.TimeoutTimerId = timerId;
    }

    /// <summary>
    ///     Consumes a PendingReplay: cancels timeout timer, builds final path, delegates to WriteReplayToDiskAndNotify.
    /// </summary>
    private void ProcessPendingReplay(PendingReplay pending, ReplayMatchKey key, long runId, PlayerRecordSavedEvent recordEvent)
    {
        if (pending.TimeoutTimerId is { } timerId && _bridge.ModSharp.IsValidTimer(timerId))
        {
            _bridge.ModSharp.StopTimer(timerId);
        }

        pending.TimeoutTimerId = null;

        var filePath = key.Stage == 0
            ? ReplayShared.BuildMainReplayPath(_replayDirectory, _bridge.GlobalVars.MapName, key.Style, key.Track, runId)
            : ReplayShared.BuildStageReplayPath(_replayDirectory,
                                                _bridge.GlobalVars.MapName,
                                                key.Style,
                                                key.Track,
                                                key.Stage,
                                                runId);

        var context = new ReplaySaveContext
        {
            SteamId       = recordEvent.SteamId.AsPrimitive(),
            FinishTime    = pending.Snapshot.Header.Time,
            AttemptResult = recordEvent.RecordType,
        };

        WriteReplayToDiskAndNotify(pending.Snapshot, filePath, context, key.Style, key.Track, key.Stage, runId);
    }

    /// <summary>
    ///     Serializes snapshot, writes to disk, notifies Playback, and handles upload.
    ///     Runs I/O in Task.Run. On failure, fallback-notifies with NoNewRecord.
    /// </summary>
    private void WriteReplayToDiskAndNotify(ReplaySaveSnapshot snapshot,
                                            string             filePath,
                                            ReplaySaveContext  context,
                                            int                style,
                                            int                track,
                                            int                stage,
                                            long?              runId)
    {
        var header = snapshot.Header;
        var frames = snapshot.Frames;

        var uploadEnabled      = timer_replay_remote_upload.GetBool();
        var mapName            = _bridge.GlobalVars.MapName;
        var compressionLevel   = timer_replay_file_compression_level.GetInt32();
        var compressionWorkers = timer_replay_file_compression_workers.GetInt32();

        Task.Run(async () =>
        {
            try
            {
                if (!await ReplayShared.WriteReplayToFileAsync(header,
                                                               filePath,
                                                               frames,
                                                               compressionLevel,
                                                               compressionWorkers,
                                                               _logger)
                                       .ConfigureAwait(false))
                {
                    throw new IOException($"Failed to write replay to {filePath}");
                }

                var replayContent = new ReplayContent { Header = header, Frames = frames };

                var isNewBest = false;

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    isNewBest = stage == 0
                        ? _playbackModule.OnNewMainReplaySaved(style, track, replayContent, context)
                        : _playbackModule.OnNewStageReplaySaved(style, track, stage, replayContent, context);
                }).ConfigureAwait(false);

                if (runId is { } savedRunId
                    && uploadEnabled
                    && _replayProviderProxy.IsAvailable
                    && _playbackModule.ShouldUploadReplay(header.SteamId, style, track, stage, header.Time, isNewBest))
                {
                    try
                    {
                        var replayBytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);

                        if (stage == 0)
                        {
                            await _replayProviderProxy.UploadReplayAsync(mapName,
                                                                         style,
                                                                         track,
                                                                         header.SteamId,
                                                                         (ulong) savedRunId,
                                                                         replayBytes).ConfigureAwait(false);
                        }
                        else
                        {
                            await _replayProviderProxy.UploadStageReplayAsync(mapName,
                                                                              style,
                                                                              track,
                                                                              stage,
                                                                              header.SteamId,
                                                                              (ulong) savedRunId,
                                                                              replayBytes).ConfigureAwait(false);
                        }
                    }
                    catch (Exception uploadEx)
                    {
                        _logger.LogError(uploadEx,
                                         "Failed to upload replay remotely for style={Style} track={Track} stage={Stage}",
                                         style,
                                         track,
                                         stage);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                                 "Failed to write replay to {Path} for style={Style} track={Track} stage={Stage}",
                                 filePath,
                                 style,
                                 track,
                                 stage);

                var fallbackContext = context with { AttemptResult = EAttemptResult.NoNewRecord };

                var fallbackContent = new ReplayContent { Header = header, Frames = frames };

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (stage == 0)
                    {
                        _playbackModule.OnNewMainReplaySaved(style, track, fallbackContent, fallbackContext);
                    }
                    else
                    {
                        _playbackModule.OnNewStageReplaySaved(style, track, stage, fallbackContent, fallbackContext);
                    }
                }).ConfigureAwait(false);
            }
        });
    }

    private void SavePendingReplayAsFallback(ReplayMatchKey key, PendingReplay pending)
    {
        if (pending.TimeoutTimerId is { } timerId)
        {
            try
            {
                _bridge.ModSharp.StopTimer(timerId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StopTimer for timeout timer {TimerId} failed (likely already cleaned)", timerId);
            }
        }

        pending.TimeoutTimerId = null;

        var snapshot = pending.Snapshot;
        var header   = snapshot.Header;
        var frames   = snapshot.Frames;
        var tempPath = pending.TempFilePath;
        var style    = key.Style;
        var track    = key.Track;
        var stage    = key.Stage;
        var steamId  = key.SteamId;
        var compressionLevel   = timer_replay_file_compression_level.GetInt32();
        var compressionWorkers = timer_replay_file_compression_workers.GetInt32();

        var writeTask = Task.Run(async () =>
        {
            try
            {
                if (!await ReplayShared.WriteReplayToFileAsync(header,
                                                               tempPath,
                                                               frames,
                                                               compressionLevel,
                                                               compressionWorkers,
                                                               _logger)
                                       .ConfigureAwait(false))
                {
                    throw new IOException($"Failed to write fallback replay to {tempPath}");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                                 "Failed to write fallback replay to {Path} for {SteamId} style={Style} track={Track} stage={Stage}",
                                 tempPath,
                                 steamId,
                                 style,
                                 track,
                                 stage);
            }
        });

        var fallbackContext = new ReplaySaveContext
        {
            SteamId = steamId, FinishTime = header.Time, AttemptResult = EAttemptResult.NoNewRecord,
        };

        var fallbackContent = new ReplayContent { Header = header, Frames = frames };

        _ = _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            if (stage == 0)
            {
                _playbackModule.OnNewMainReplaySaved(style, track, fallbackContent, fallbackContext);
            }
            else
            {
                _playbackModule.OnNewStageReplaySaved(style, track, stage, fallbackContent, fallbackContext);
            }
        });

        _fallbackRecords[key] = new FallbackReplayRecord
        {
            TempFilePath = tempPath, WriteTask = writeTask, CreatedAt = DateTime.UtcNow,
        };

        _logger.LogWarning("Fallback-saved pending replay for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId} to {Path}",
                           steamId,
                           style,
                           track,
                           stage,
                           key.AttemptId,
                           tempPath);

        // Expire old entries (> 10 min)
        var                   expiryCutoff = DateTime.UtcNow.AddMinutes(-10);
        List<ReplayMatchKey>? expiredKeys  = null;

        foreach (var (fbKey, fbRecord) in _fallbackRecords)
        {
            if (fbRecord.CreatedAt < expiryCutoff)
            {
                expiredKeys ??= [];
                expiredKeys.Add(fbKey);
            }
        }

        if (expiredKeys is not null)
        {
            foreach (var expiredKey in expiredKeys)
            {
                _fallbackRecords.Remove(expiredKey);

                _logger.LogWarning("Removed expired fallback record for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId} (older than 10 minutes)",
                                   expiredKey.SteamId,
                                   expiredKey.Style,
                                   expiredKey.Track,
                                   expiredKey.Stage,
                                   expiredKey.AttemptId);
            }
        }
    }

    /// <summary>
    ///     Processes a late OnRecordSaved for a fallback-saved replay:
    ///     await WriteTask → File.Move → re-notify Playback → upload.
    /// </summary>
    private void ProcessFallbackRecord(FallbackReplayRecord   fallback,
                                       ReplayMatchKey         key,
                                       long                   runId,
                                       PlayerRecordSavedEvent recordEvent)
    {
        var tempPath  = fallback.TempFilePath;
        var writeTask = fallback.WriteTask;
        var style     = key.Style;
        var track     = key.Track;
        var stage     = key.Stage;
        var steamId   = key.SteamId;
        var attemptId = key.AttemptId;
        var mapId     = key.MapId;
        var createdAt = fallback.CreatedAt;

        var finalPath = stage == 0
            ? ReplayShared.BuildMainReplayPath(_replayDirectory, _bridge.GlobalVars.MapName, style, track, runId)
            : ReplayShared.BuildStageReplayPath(_replayDirectory, _bridge.GlobalVars.MapName, style, track, stage, runId);

        var uploadEnabled       = timer_replay_remote_upload.GetBool();
        var mapName             = _bridge.GlobalVars.MapName;
        var replayProviderProxy = _replayProviderProxy;
        var playbackModule      = _playbackModule;
        var bridge              = _bridge;
        var logger              = _logger;
        var attemptResult       = recordEvent.RecordType;
        var recordSteamId       = recordEvent.SteamId.AsPrimitive();

        Task.Run(async () =>
        {
            try
            {
                await writeTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Fallback WriteTask timed out (10s) for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}. Re-queuing for later retry",
                                  steamId,
                                  style,
                                  track,
                                  stage,
                                  attemptId);

                var reinsertKey = new ReplayMatchKey(mapId, steamId, style, track, stage, attemptId);

                var reinsertRecord = new FallbackReplayRecord
                {
                    TempFilePath = tempPath, WriteTask = writeTask, CreatedAt = createdAt,
                };

                _ = bridge.ModSharp.InvokeFrameActionAsync(() => { _fallbackRecords[reinsertKey] = reinsertRecord; });

                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                                "Fallback WriteTask faulted for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}. Aborting rename and upload",
                                steamId,
                                style,
                                track,
                                stage,
                                attemptId);

                return;
            }

            try
            {
                await RetryOnIOException(() =>
                                         {
                                             File.Move(tempPath, finalPath, true);

                                             return Task.CompletedTask;
                                         },
                                         logger,
                                         "File.Move",
                                         tempPath).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.LogError(ex,
                                "File.Move failed after retries for {SteamId} style={Style} track={Track} stage={Stage}: {TempPath} → {FinalPath}",
                                steamId,
                                style,
                                track,
                                stage,
                                tempPath,
                                finalPath);

                return;
            }

            byte[]? replayBytes = null;

            try
            {
                replayBytes = await RetryOnIOException(() => File.ReadAllBytesAsync(finalPath),
                                                       logger,
                                                       "File.ReadAllBytes",
                                                       finalPath).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.LogError(ex,
                                "File.ReadAllBytes failed after retries for {SteamId} style={Style} track={Track} stage={Stage}: {FinalPath}",
                                steamId,
                                style,
                                track,
                                stage,
                                finalPath);
            }

            var context = new ReplaySaveContext
            {
                SteamId = recordSteamId, FinishTime = recordEvent.Time, AttemptResult = attemptResult,
            };

            var isNewBest = false;

            await bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (replayBytes is not null)
                {
                    var loadResult = ReplayShared.DeserializeReplay(replayBytes, style, track, stage, logger);

                    if (loadResult is { } loaded)
                    {
                        isNewBest = stage == 0
                            ? playbackModule.OnNewMainReplaySaved(style, track, loaded.Content, context)
                            : playbackModule.OnNewStageReplaySaved(style, track, stage, loaded.Content, context);
                    }
                }
            }).ConfigureAwait(false);

            if (replayBytes is not null
                && uploadEnabled
                && replayProviderProxy.IsAvailable)
            {
                var shouldUpload = false;

                await bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    shouldUpload = playbackModule.ShouldUploadReplay(recordSteamId,
                                                                     style,
                                                                     track,
                                                                     stage,
                                                                     recordEvent.Time,
                                                                     isNewBest);
                }).ConfigureAwait(false);

                if (shouldUpload)
                {
                    try
                    {
                        if (stage == 0)
                        {
                            await replayProviderProxy.UploadReplayAsync(mapName,
                                                                        style,
                                                                        track,
                                                                        recordSteamId,
                                                                        (ulong) runId,
                                                                        replayBytes).ConfigureAwait(false);
                        }
                        else
                        {
                            await replayProviderProxy.UploadStageReplayAsync(mapName,
                                                                             style,
                                                                             track,
                                                                             stage,
                                                                             recordSteamId,
                                                                             (ulong) runId,
                                                                             replayBytes).ConfigureAwait(false);
                        }
                    }
                    catch (Exception uploadEx)
                    {
                        logger.LogError(uploadEx,
                                        "Failed to upload fallback replay remotely for style={Style} track={Track} stage={Stage}",
                                        style,
                                        track,
                                        stage);
                    }
                }
            }

            logger.LogInformation("Successfully processed fallback record for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}: renamed {TempPath} → {FinalPath}",
                                  steamId,
                                  style,
                                  track,
                                  stage,
                                  attemptId,
                                  tempPath,
                                  finalPath);
        });
    }

    private static async Task RetryOnIOException(Func<Task> action, ILogger logger, string operationName, string path)
    {
        const int maxRetries = 3;
        const int delayMs    = 100;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);

                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                logger.LogWarning("{Operation} failed on attempt {Attempt}/{MaxRetries} for {Path}, retrying in {Delay}ms",
                                  operationName,
                                  attempt + 1,
                                  maxRetries,
                                  path,
                                  delayMs);

                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> RetryOnIOException<T>(Func<Task<T>> action, ILogger logger, string operationName, string path)
    {
        const int maxRetries = 3;
        const int delayMs    = 100;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxRetries)
            {
                logger.LogWarning("{Operation} failed on attempt {Attempt}/{MaxRetries} for {Path}, retrying in {Delay}ms",
                                  operationName,
                                  attempt + 1,
                                  maxRetries,
                                  path,
                                  delayMs);

                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }

        // This should never be reached due to the when clause, but satisfies the compiler.
        throw new InvalidOperationException("Unreachable");
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams arg, HookReturnValue<EmptyHookReturn> hook)
    {
        var client = arg.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var pawn = arg.Pawn;

        if (!pawn.IsAlive)
        {
            return;
        }

        var slot = client.Slot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        // Skip recording frames while the timer is paused
        if (_timerModule.GetTimerInfo(slot) is { Status: ETimerStatus.Paused })
        {
            return;
        }

        var angles  = pawn.GetEyeAngles();
        var service = arg.Service;

        frameData.Frames.Add(new ()
        {
            Origin         = pawn.GetAbsOrigin(),
            Angles         = new (angles.X, angles.Y),
            PressedButtons = service.KeyButtons,
            ChangedButtons = service.KeyChangedButtons,
            ScrollButtons  = service.ScrollButtons,
            MoveType       = pawn.MoveType,
            Velocity       = pawn.GetAbsVelocity(),
        });
    }
}
