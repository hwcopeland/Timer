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
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Request.Models;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Modules.Timer;
using ZstdSharp;
using ZstdSharp.Unsafe;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

internal partial class ReplayModule
{
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

        using var scope = _logger.BeginScope("OnPlayerStageTimerStart");

        _logger.LogError("Attempted to add CurrentFrame to StageTimerStartTick for stage {Stage} (index {Index}) "
                         + "when current stage count is {Count}. Probable logic error elsewhere.",
                         stageNumber,
                         stageIndex,
                         count);
    }

    private void StopTimerAndNotifySaving(IPlayerController controller, PlayerSlot slot)
    {
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            _timerModule.StopTimer(slot);
            controller.PrintToChat("Timer stopped: Replay is saving.");
        });
    }

    private string BuildMainReplayPath(int style, int track, Guid? runId)
    {
        var fileName = runId is null
            ? $"{_bridge.GlobalVars.MapName}_{track}.replay.{Guid.NewGuid()}"
            : $"{_bridge.GlobalVars.MapName}_{track}_{runId.Value}.replay";

        return Path.Combine(_replayDirectory,
                            $"style_{style}",
                            fileName);
    }

    private string BuildStageReplayPath(int style, int track, int stage, Guid? runId = null)
    {
        var fileName = runId is null
            ? $"{_bridge.GlobalVars.MapName}_{track}_{stage}.replay.{Guid.NewGuid()}"
            : $"{_bridge.GlobalVars.MapName}_{track}_{stage}_{runId.Value}.replay";

        return Path.Combine(_replayDirectory,
                            $"style_{style}",
                            "stage",
                            fileName);
    }

    private readonly record struct ReplaySaveSnapshot(ReplayFileHeader Header, IReadOnlyList<ReplayFrameData> Frames);

    private sealed class ReplayFrameSlice : IReadOnlyList<ReplayFrameData>
    {
        private readonly IReadOnlyList<ReplayFrameData> _source;
        private readonly int                            _offset;
        private readonly int                            _count;

        public ReplayFrameSlice(IReadOnlyList<ReplayFrameData> source, int offset, int count)
        {
            _source = source;
            _offset = offset;
            _count  = count;
        }

        public int Count => _count;

        public ReplayFrameData this[int index]
        {
            get
            {
                if ((uint) index >= (uint) _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _source[_offset + index];
            }
        }

        public IEnumerator<ReplayFrameData> GetEnumerator()
        {
            for (var i = 0; i < _count; i++)
            {
                yield return _source[_offset + i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private static ReplaySaveSnapshot CreateMainReplaySnapshot(PlayerFrameData frame)
    {
        var framesBuffer = frame.Frames;

        frame.Frames = new ReplayFrameBuffer(Utils.Tickrate * 60 * 5);

        var header = new ReplayFileHeader
        {
            SteamId     = frame.SteamId,
            TotalFrames = framesBuffer.Count,
            PreFrame    = frame.TimerStartFrame,
            PostFrame   = frame.TimerFinishFrame,
            Time        = frame.FinishTime,
            StageTicks  = [..frame.NewStageTicks],
            PlayerName  = frame.Name,
        };

        frame.NewStageTicks.Clear();
        frame.StageTimerStartTicks.Clear();
        frame.TimerStartFrame  = 0;
        frame.TimerFinishFrame = 0;
        frame.FinishTime       = 0;

        return new ReplaySaveSnapshot(header, framesBuffer);
    }

    private static ReplaySaveSnapshot CreateStageReplaySnapshot(PlayerFrameData frame,
                                                                int             startTick,
                                                                int             stageStartFrame,
                                                                int             stageFinishFrame,
                                                                int             postRunFrameCount,
                                                                float           finishTime)
    {
        var finalFrame = Math.Min(frame.Frames.Count, stageFinishFrame + postRunFrameCount);
        var length     = Math.Max(0, finalFrame                        - startTick);

        IReadOnlyList<ReplayFrameData> framesToWrite = length == 0
            ? []
            : new ReplayFrameSlice(frame.Frames, startTick, length);

        var header = new ReplayFileHeader
        {
            SteamId     = frame.SteamId,
            TotalFrames = framesToWrite.Count,
            PreFrame    = stageStartFrame  - startTick,
            PostFrame   = stageFinishFrame - startTick,
            Time        = finishTime,
            PlayerName  = frame.Name,
        };

        return new ReplaySaveSnapshot(header, framesToWrite);
    }

    private static void TrimPreRunFrames(PlayerFrameData frameData, int maxPreFrame)
    {
        if (maxPreFrame <= 0)
        {
            frameData.Frames.Clear();

            return;
        }

        var excess = frameData.Frames.Count - maxPreFrame;

        if (excess > 0)
        {
            frameData.Frames.RemoveOldest(excess);
        }
    }

    private void OnTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        if (frameData.IsSavingReplay)
        {
            StopTimerAndNotifySaving(controller, slot);

            return;
        }

        if (frameData.GrabbingPostFrame)
        {
            frameData.GrabbingPostFrame = false;
            SaveReplayToFile(slot, frameData);

            if (frameData.PostFrameTimer is { } timer)
            {
                _bridge.ModSharp.StopTimer(timer);
            }

            frameData.PostFrameTimer = null;

            StopTimerAndNotifySaving(controller, slot);

            return;
        }

        var maxPreFrame = (int) (timer_replay_prerun_time.GetFloat() * Utils.Tickrate);
        TrimPreRunFrames(frameData, maxPreFrame);

        frameData.NewStageTicks.Clear();
        frameData.StageTimerStartTicks.Clear();

        frameData.TimerStartFrame = frameData.Frames.Count;
    }

    private void OnPlayerStageTimerStart(IPlayerController controller,
                                         IPlayerPawn       pawn,
                                         IStageTimerInfo   timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        var stage = timerInfo.Stage;
        var idx   = stage - 1;

        SetStageTimerStart(frameData, idx, frameData.Frames.Count, stage);
    }

    private void OnPlayerStageTimerFinish(IPlayerController controller,
                                          IPlayerPawn       pawn,
                                          IStageTimerInfo   timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frame))
        {
            return;
        }

        frame.NewStageTicks.Add(frame.Frames.Count);

        frame.Name = controller.PlayerName;
        var finishedStage = timerInfo.Stage;

        var lastStage = finishedStage - 1;

        if (!TryGetStageStartTick(frame, lastStage, out var timerStartTick))
        {
            return;
        }

        var time = timerInfo.Time;

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var newStageTicks  = frame.NewStageTicks[lastStage];

            var delay              = timer_replay_stage_postrun_time.GetFloat();
            var postRunFrameLength = (int) (Utils.Tickrate * delay);
            var preRunFrameLength  = (int) (Utils.Tickrate * timer_replay_stage_prerun_time.GetFloat());

            if (frame.StagePostFrameTimer is { } stageReplayTimer)
            {
                // we have ForceCallOnStop flag which forces firing the callback
                _bridge.ModSharp.StopTimer(stageReplayTimer);
            }

            frame.StagePostFrameTimer = _bridge.ModSharp.PushTimer(() =>
                                                                   {
                                                                       var startTick = Math.Max(0,
                                                                                timerStartTick - preRunFrameLength);

                                                                       SaveStageReplayToFile(frame,
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

    private void OnPlayerFinishMap(IPlayerController controller,
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
                                                              SaveReplayToFile(slot, frame);

                                                              return TimerAction.Stop;
                                                          },
                                                          timer_replay_postrun_time.GetFloat(),
                                                          GameTimerFlags.StopOnMapEnd);
    }

    private void RenameReplayFile(string path, Guid runId)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Attempted to rename a temporary replay file that no longer exists: {path}", path);

            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (directory == null)
            {
                _logger.LogError("Could not determine directory for temporary path: {path}", path);

                return;
            }

            var fileName = Path.GetFileName(path);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                _logger.LogWarning("File name is null or empty from path {p}", path);

                return;
            }

            const string strToLookUp = ".replay.";

            // $"{_bridge.GlobalVars.MapName}_{track}.replay.{Guid.NewGuid()}" to "{_bridge.GlobalVars.MapName}_{track}_{runId}.replay"
            var index = fileName.LastIndexOf(strToLookUp, StringComparison.Ordinal);

            if (index == -1)
            {
                return;
            }

            var newFileName = $"{fileName.Substring(0, index)}_{runId}.replay";

            var finalPath = Path.Combine(directory, newFileName);

            File.Move(path, finalPath, true);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to rename replay file from {tempPath} to run ID {runId}", path, runId);
        }
    }

    private void OnPlayerRecordSaved(SteamID        playerSteamId,
                                     string         playerName,
                                     EAttemptResult recordType,
                                     RunRecord      savedRecord,
                                     RunRecord?     wrRecord,
                                     RunRecord?     pbRecord)
    {
        if (_playerManager.GetPlayer(playerSteamId) is not { } player || _playerFrameData[player.Slot] is not { } frameData)
        {
            return;
        }

        var isStageRecord = savedRecord.Stage > 0;

        var runId = savedRecord.Id;

        if (isStageRecord)
        {
            var stage = savedRecord.Stage;

            if (frameData.SavedStageReplayPaths.TryGetValue(stage, out var tempPath))
            {
                RenameReplayFile(tempPath, runId);
                frameData.SavedStageReplayPaths.Remove(stage);
            }
            else
            {
                frameData.PendingStageRunIds[stage] = runId;
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(frameData.SavedMainReplayPath))
            {
                RenameReplayFile(frameData.SavedMainReplayPath, runId);
                frameData.SavedMainReplayPath = null; // Clean up
            }
            else
            {
                frameData.PendingMainRunId = runId;
            }
        }
    }

    private async Task<bool> WriteReplayToFile(ReplayFileHeader               header,
                                               string                         path,
                                               IReadOnlyList<ReplayFrameData> framesToWrite)
    {
        try
        {
            await using var fileStream
                = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);

            var headerBuffer = ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                using var       memoryStream = new MemoryStream(headerBuffer);
                await using var jsonWriter   = new Utf8JsonWriter(memoryStream);

                JsonSerializer.Serialize(jsonWriter, header);

                await jsonWriter.FlushAsync();

                await fileStream.WriteAsync(new ReadOnlyMemory<byte>(headerBuffer, 0, (int) memoryStream.Position));
                await fileStream.WriteAsync(HeaderFrameSeparatorBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }

            var compressionLevel = Math.Max(timer_replay_file_compression_level.GetInt32(), 1);

            await using var compressionStream = new CompressionStream(fileStream, compressionLevel);

            compressionStream.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers,
                                           Math.Max(timer_replay_file_compression_workers.GetInt32(), ProcessorCount));

            switch (framesToWrite)
            {
                case ReplayFrameData[] arr:
                    await MemoryPackSerializer.SerializeAsync(compressionStream, arr);

                    break;
                case List<ReplayFrameData> list:
                    await MemoryPackSerializer.SerializeAsync(compressionStream, list);

                    break;
                default:
                    var rented = ArrayPool<ReplayFrameData>.Shared.Rent(framesToWrite.Count);

                    try
                    {
                        for (var i = 0; i < framesToWrite.Count; i++)
                        {
                            rented[i] = framesToWrite[i];
                        }

                        await MemoryPackSerializer.SerializeAsync(compressionStream, rented.AsMemory(0, framesToWrite.Count));
                    }
                    finally
                    {
                        ArrayPool<ReplayFrameData>.Shared.Return(rented);
                    }

                    break;
            }
        }
        catch (Exception e)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _logger.LogError(e, "Error when trying to write temporary replay file to {p}", path);

            return false;
        }

        return true;
    }

    private void SaveReplayToFile(PlayerSlot slot, PlayerFrameData frame)
    {
        if (frame.IsSavingReplay)
        {
            _logger.LogWarning("Trying to call SaveReplayToFile when client {name} is already in the process of saving???",
                               frame.Name);

            return;
        }

        frame.IsSavingReplay = true;
        var style    = frame.Style;
        var track    = frame.Track;
        var snapshot = CreateMainReplaySnapshot(frame);

        string path;

        if (frame.PendingMainRunId is { } runId)
        {
            path = BuildMainReplayPath(style, track, runId);

            frame.PendingMainRunId = null;
        }
        else
        {
            path = BuildMainReplayPath(style, track, null);
        }

        Task.Run(async () =>
        {
            var header        = snapshot.Header;
            var framesToWrite = snapshot.Frames;

            try
            {
                if (await WriteReplayToFile(header, path, framesToWrite))
                {
                    await StartNewReplay(header, framesToWrite, style, track);

                    if (frame.PendingMainRunId is null)
                    {
                        frame.SavedMainReplayPath = path;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when trying to save replay at {p}", path);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (_playerManager.GetPlayer(frame.SteamId) is { } player
                        && player.Controller is { IsValidEntity: true } controller)
                    {
                        controller.PrintToChat($"Failed to save replay. Reason: {e.Message}");
                    }
                });
            }
            finally
            {
                frame.IsSavingReplay = false;
            }
        });
    }

    private async Task StartNewReplay(ReplayFileHeader               header,
                                      IReadOnlyList<ReplayFrameData> framesToWrite,
                                      int                            style,
                                      int                            track)
    {
        if (_replayCache.TryGetValue((style, track), out var cache) && cache.Header.Time <= header.Time)
        {
            return;
        }

        var replayContent = new ReplayContent
        {
            Header = header,
            Frames = framesToWrite,
        };

        _replayCache[(style, track)] = replayContent;

        await _bridge.ModSharp.InvokeFrameActionAsync(() => { UpdateMainReplayBots(style, track, replayContent); });
    }

    private void SaveStageReplayToFile(PlayerFrameData frame,
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

        Guid? pathRunId = frame.PendingStageRunIds.TryGetValue(stage, out var pendingRunId)
            ? pendingRunId
            : null;

        if (pathRunId is not null)
        {
            frame.PendingStageRunIds.Remove(stage);
        }

        var path = BuildStageReplayPath(style, track, stage, pathRunId);

        var (header, framesToWrite) = CreateStageReplaySnapshot(frame,
                                                                startTick,
                                                                stageStartFrame,
                                                                stageFinishFrame,
                                                                postRunFrameCount,
                                                                finishTime);

        Task.Run(async () =>
        {
            // timer_path/replays/style_id/stage/mapname_tracknum_stagenum.replay

            try
            {
                if (await WriteReplayToFile(header, path, framesToWrite).ConfigureAwait(false))
                {
                    if (pathRunId is null)
                    {
                        frame.SavedStageReplayPaths[stage] = path;
                    }

                    var cacheKey = (style, track, stage);

                    if (_stageReplayCache.TryGetValue(cacheKey, out var cache)
                        && cache.Header.Time <= header.Time)
                    {
                        return;
                    }

                    _stageReplayCache[cacheKey] = new () { Frames = framesToWrite, Header = header };

                    await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                 {
                                     UpdateStageReplayBots(style,
                                                           track,
                                                           stage,
                                                           framesToWrite,
                                                           header);
                                 })
                                 .ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when trying to save stage replay");
            }
        });
    }

    private void UpdateMainReplayBots(int style, int track, ReplayContent replayContent)
    {
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

    private void UpdateStageReplayBots(int                            style,
                                       int                            track,
                                       int                            stage,
                                       IReadOnlyList<ReplayFrameData> framesToWrite,
                                       ReplayFileHeader               header)
    {
        foreach (var bot in _replayBots)
        {
            if (!IsStageReplayBotMatch(bot, style, track, stage))
            {
                continue;
            }

            bot.Frames = framesToWrite;
            bot.Header = header;
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
