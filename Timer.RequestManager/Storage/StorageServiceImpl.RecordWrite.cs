using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Scheduling;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<(EAttemptResult, RunRecord, int rank)> AddPlayerRecord(SteamID steamId, string mapName, RecordRequest recordRequest)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);

        var styleValue = recordRequest.Style;
        var trackValue = ToUInt16(recordRequest.Track);
        var bestTimes = await QueryMainBestTimesAsync(steamId, mapId, styleValue, trackValue);

        await _db.Ado.BeginTranAsync();

        try
        {
            var run = CreateRunEntity(steamId, mapId, recordRequest, DateTime.UtcNow);
            run.RunType = RunType.Main;
            run.Stage   = 0;

            var newRunId = await _db.Insertable(run).ExecuteReturnBigIdentityAsync();
            run.Id = unchecked((ulong) newRunId);

            if (recordRequest.Checkpoints.Count > 0)
            {
                var stageSegments = CreateRunSegmentsFromCheckpoints(run.Id, recordRequest, run.Date);

                if (stageSegments.Count > 0)
                {
                    await _db.Insertable(stageSegments).ExecuteCommandAsync();
                }
            }

            var result = ResolveAttemptResult(recordRequest.Time,
                                              bestTimes?.ServerBestTime,
                                              bestTimes?.PlayerBestTime);

            await UpsertPlayerBestRunAsync(run);

            var rank = 0;

            if (result == EAttemptResult.NewServerRecord)
            {
                rank = 1;
            }
            else if (result == EAttemptResult.NewPersonalRecord)
            {
                rank = await QueryMainRunRankAsync(mapId, styleValue, trackValue, run.Time);
            }

            await _db.Ado.CommitTranAsync();

            // After commit, trigger score recalculation based on EAttemptResult (fire-and-forget)
            if (result != EAttemptResult.NoNewRecord)
            {
                var (tier, basePot) = await GetTrackScoreConfigAsync(mapId, trackValue);
                _scoreRecalcScheduler.Enqueue(new RecalcRequest(mapId, styleValue, trackValue, tier, basePot, recordRequest.StyleFactor));
            }

            return (result, ToRunRecord(run), rank);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();

            throw;
        }
    }

    public async Task<(EAttemptResult, RunRecord, int rank)> AddPlayerStageRecord(SteamID steamId, string mapName, RecordRequest newRunRecord)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);

        var styleValue = newRunRecord.Style;
        var trackValue = ToUInt16(newRunRecord.Track);
        var stageValue = ToUInt16(newRunRecord.Stage);
        var bestTimes = await QueryStageBestTimesAsync(steamId, mapId, styleValue, trackValue, stageValue);

        await _db.Ado.BeginTranAsync();

        try
        {
            var now = DateTime.UtcNow;
            var run = CreateRunEntity(steamId, mapId, newRunRecord, now);
            run.RunType = RunType.Stage;
            run.Stage   = stageValue;

            var runId = await _db.Insertable(run).ExecuteReturnBigIdentityAsync();
            run.Id = unchecked((ulong) runId);

            var result = ResolveAttemptResult(newRunRecord.Time,
                                              bestTimes?.ServerBestTime,
                                              bestTimes?.PlayerBestTime);

            await UpsertPlayerBestRunAsync(run);

            var rank = 0;

            if (result == EAttemptResult.NewServerRecord)
            {
                rank = 1;
            }
            else if (result == EAttemptResult.NewPersonalRecord)
            {
                rank = await QueryStageRunRankAsync(mapId, styleValue, trackValue, stageValue, run.Time);
            }

            await _db.Ado.CommitTranAsync();

            return (result, ToRunRecord(run), rank);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();

            throw;
        }
    }

    public async Task RemoveMapRecords(string mapName)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return;
        }

        await _db.Ado.BeginTranAsync();

        try
        {
            await _db.Deleteable<RunSegmentEntity>()
                     .Where(segment => SqlFunc.Subqueryable<RunEntity>()
                                        .Where(run => run.MapId == mapId.Value && run.Id == segment.RunId)
                                        .Any())
                     .ExecuteCommandAsync();

            await _db.Deleteable<RunEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync();

            await _db.Deleteable<ReplayEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync();

            await _db.Deleteable<PlayerBestRunEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync();

            await _db.Ado.CommitTranAsync();

            RemoveBestRunSeedCacheForMap(mapId.Value);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();

            throw;
        }

    }

    private async Task<AttemptBestTimesRow?> QueryMainBestTimesAsync(SteamID steamId,
                                                                      ulong   mapId,
                                                                      int     style,
                                                                      ushort  track)
    {
        const ushort stage = 0;

        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, stage);

        return await QueryBestRuns().Where(x => x.MapId == mapId
                                                && x.RunType == RunType.Main
                                                && x.Style == style
                                                && x.Track == track
                                                && x.Stage == stage)
                                    .Select(x => new AttemptBestTimesRow
                                    {
                                        ServerBestTime = SqlFunc.AggregateMin(x.BestTime),
                                        PlayerBestTime = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamId,
                                                                                           (float?) x.BestTime,
                                                                                           null)),
                                    })
                                    .FirstAsync();
    }

    private async Task<int> QueryMainRunRankAsync(ulong mapId, int style, ushort track, float runTime)
    {
        const ushort stage = 0;

        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, stage);

        var quickerCount = await QueryBestRuns().Where(run => run.MapId == mapId
                                                              && run.RunType == RunType.Main
                                                              && run.Style == style
                                                              && run.Track == track
                                                              && run.Stage == stage
                                                              && run.BestTime < runTime)
                                        .CountAsync();

        return quickerCount + 1;
    }

    private async Task<AttemptBestTimesRow?> QueryStageBestTimesAsync(SteamID steamId,
                                                                       ulong   mapId,
                                                                       int     style,
                                                                       ushort  track,
                                                                       ushort  stage)
    {
        await EnsureBestRunsSeededAsync(mapId, RunType.Stage, style, track, stage);

        return await QueryBestRuns().Where(x => x.MapId == mapId
                                                && x.RunType == RunType.Stage
                                                && x.Style == style
                                                && x.Track == track
                                                && x.Stage == stage)
                                    .Select(x => new AttemptBestTimesRow
                                    {
                                        ServerBestTime = SqlFunc.AggregateMin(x.BestTime),
                                        PlayerBestTime = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamId,
                                                                                           (float?) x.BestTime,
                                                                                           null)),
                                    })
                                    .FirstAsync();
    }

    private async Task<int> QueryStageRunRankAsync(ulong    mapId,
                                                    int      style,
                                                    ushort   track,
                                                    ushort   stage,
                                                    float    runTime)
    {
        await EnsureBestRunsSeededAsync(mapId, RunType.Stage, style, track, stage);

        var quickerCount = await QueryBestRuns().Where(run => run.MapId == mapId
                                                              && run.RunType == RunType.Stage
                                                              && run.Style == style
                                                              && run.Track == track
                                                              && run.Stage == stage
                                                              && run.BestTime < runTime)
                                        .CountAsync();

        return quickerCount + 1;
    }

    private static RunEntity CreateRunEntity(SteamID steamId, ulong mapId, RecordRequest request, DateTime now)
        => new ()
        {
            SteamId        = steamId,
            MapId          = mapId,
            RunType        = request.Stage > 0 ? RunType.Stage : RunType.Main,
            Stage          = ToUInt16(request.Stage),
            Style          = request.Style,
            Track          = ToUInt16(request.Track),
            Time           = request.Time,
            Jumps          = ToUInt32(request.Jumps),
            Strafes        = ToUInt32(request.Strafes),
            Sync           = request.Sync,
            VelocityStartX = request.VelocityStartX,
            VelocityStartY = request.VelocityStartY,
            VelocityStartZ = request.VelocityStartZ,
            VelocityEndX   = request.VelocityEndX,
            VelocityEndY   = request.VelocityEndY,
            VelocityEndZ   = request.VelocityEndZ,
            VelocityMaxX   = request.VelocityMaxX,
            VelocityMaxY   = request.VelocityMaxY,
            VelocityMaxZ   = request.VelocityMaxZ,
            VelocityAvgX   = request.VelocityAvgX,
            VelocityAvgY   = request.VelocityAvgY,
            VelocityAvgZ   = request.VelocityAvgZ,
            Date           = now,
        };

    private static List<RunSegmentEntity> CreateRunSegmentsFromCheckpoints(ulong runId, RecordRequest request, DateTime now)
    {
        if (request.Checkpoints.Count == 0)
        {
            return [];
        }

        var segments = new List<RunSegmentEntity>(request.Checkpoints.Count);

        foreach (var checkpoint in request.Checkpoints)
        {
            segments.Add(new ()
            {
                RunId          = runId,
                Stage          = ToUInt16(checkpoint.CheckpointIndex),
                Time           = checkpoint.Time,
                Jumps          = ToUInt32(request.Jumps),
                Strafes        = ToUInt32(request.Strafes),
                Sync           = checkpoint.Sync,
                VelocityStartX = checkpoint.VelocityStartX,
                VelocityStartY = checkpoint.VelocityStartY,
                VelocityStartZ = checkpoint.VelocityStartZ,
                VelocityEndX   = checkpoint.VelocityEndX,
                VelocityEndY   = checkpoint.VelocityEndY,
                VelocityEndZ   = checkpoint.VelocityEndZ,
                VelocityMaxX   = checkpoint.VelocityMaxX,
                VelocityMaxY   = checkpoint.VelocityMaxY,
                VelocityMaxZ   = checkpoint.VelocityMaxZ,
                VelocityAvgX   = checkpoint.VelocityAvgX,
                VelocityAvgY   = checkpoint.VelocityAvgY,
                VelocityAvgZ   = checkpoint.VelocityAvgZ,
                Date           = now,
            });
        }

        return segments;
    }

    private static EAttemptResult ResolveAttemptResult(float newTime, float? serverBestTime, float? playerBestTime)
    {
        if (serverBestTime is null || newTime < serverBestTime.Value)
        {
            return EAttemptResult.NewServerRecord;
        }

        if (playerBestTime is null || newTime < playerBestTime.Value)
        {
            return EAttemptResult.NewPersonalRecord;
        }

        return EAttemptResult.NoNewRecord;
    }
}
