using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Scheduling;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    /// <summary>
    /// Full recalculation of all player scores for a given track.
    /// 1. Single SELECT: uses ROW_NUMBER() + COUNT() window functions to get ranked list and total count
    /// 2. In-memory ScoreCalculator pass to compute each player's score
    /// 3. Batch UPSERT: update PlayerTrackScoreEntity (changed records only)
    /// 4. Batch UPDATE: aggregate and update PlayerEntity.Points via subquery
    /// </summary>
    internal async Task RecalculateTrackScoresAsync(ulong mapId, int style, ushort track, int tier, int basePot, double styleFactor)
    {
        var isBonus = ScoreCalculator.IsBonus(track);
        var trackPool = ScoreCalculator.CalculateTrackPool(tier, isBonus, basePot, styleFactor);

        // 1. Use window functions to get the ranked player list and total count
        var rankedPlayers = await GetRankedPlayersAsync(mapId, style, track);

        if (rankedPlayers.Count == 0)
        {
            return;
        }

        var total = rankedPlayers.Count;

        // 2. Compute scores in memory
        var calculatedScores = rankedPlayers.Select((p, index) => new
        {
            p.SteamId,
            Points = (uint)Math.Round(ScoreCalculator.CalculatePlayerTrackScore(trackPool, index + 1, total))
        }).ToList();

        // 3. Query existing scores for delta comparison
        var existingScores = await _db.Queryable<PlayerTrackScoreEntity>()
                                      .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                      .Select(x => new ExistingTrackScoreRow
                                      {
                                          SteamId = x.SteamId,
                                          Points = x.Points,
                                      })
                                      .ToListAsync();

        var existingDict = existingScores.ToDictionary(x => x.SteamId, x => x.Points);

        // 4. Delta filter: keep only records whose score actually changed
        var now = DateTime.UtcNow;
        var changedScores = calculatedScores
            .Where(c => !existingDict.TryGetValue(c.SteamId, out var oldPoints) || oldPoints != c.Points)
            .Select(c => new PlayerTrackScoreEntity
            {
                SteamId = c.SteamId,
                MapId = mapId,
                Style = style,
                Track = track,
                Points = c.Points,
                UpdatedAt = now
            })
            .ToList();

        if (changedScores.Count == 0)
        {
            return; // No changes, skip write
        }

        // 5. Batch UPSERT only changed records (insert new, update existing)
        var storage = _db.Storageable(changedScores)
            .WhereColumns(x => new { x.SteamId, x.MapId, x.Style, x.Track })
            .ToStorage();

        if (storage.InsertList.Count > 0)
        {
            await storage.AsInsertable.ExecuteCommandAsync();
        }

        if (storage.UpdateList.Count > 0)
        {
            await storage.AsUpdateable
                .UpdateColumns(x => new { x.Points, x.UpdatedAt })
                .ExecuteCommandAsync();
        }

        // 6. Aggregate and update PlayerEntity.Points for affected players
        await UpdatePlayerTotalPointsAsync(changedScores.Select(x => x.SteamId));
    }


    /// <summary>
    /// Aggregate all track scores for the given players and batch-update PlayerEntity.Points.
    /// Uses a single GROUP BY query to aggregate in SQL, then batch update.
    /// </summary>
    private async Task UpdatePlayerTotalPointsAsync(IEnumerable<SteamID> steamIds)
    {
        var idList = steamIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return;
        }

        // SQL-level aggregation: SELECT SteamId, SUM(Points) FROM ... GROUP BY SteamId
        var aggregated = await _db.Queryable<PlayerTrackScoreEntity>()
            .Where(x => idList.Contains(x.SteamId))
            .GroupBy(x => x.SteamId)
            .Select(x => new
            {
                x.SteamId,
                TotalPoints = SqlFunc.AggregateSum(x.Points),
            })
            .ToListAsync();

        var newTotals = aggregated.ToDictionary(x => x.SteamId, x => (uint) x.TotalPoints);

        var now = DateTime.UtcNow;

        var updates = idList.Select(id => new PlayerEntity
        {
            SteamId = id,
            Points = newTotals.TryGetValue(id, out var pts) ? pts : 0,
            UpdatedAt = now
        }).ToList();

        await _db.Updateable(updates)
            .UpdateColumns(x => new { x.Points, x.UpdatedAt })
            .WhereColumns(x => x.SteamId)
            .ExecuteCommandAsync();
    }

    /// <summary>
    /// Get the tier for a given track from MapTrackEntity.
    /// For the main track (track=0), reads from MapEntity instead.
    /// </summary>
    internal async Task<int> GetTrackTierAsync(ulong mapId, ushort track)
    {
        var (tier, _) = await GetTrackScoreConfigAsync(mapId, track);

        return tier;
    }

    /// <summary>
    /// Get the score configuration (Tier and BasePot) for a given track.
    /// </summary>
    internal async Task<(int Tier, int BasePot)> GetTrackScoreConfigAsync(ulong mapId, ushort track)
    {
        var key = (mapId, track);

        if (_trackScoreConfigCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var row = await _db.Queryable<MapEntity>()
                           .LeftJoin<MapTrackEntity>((map, trackTier) => map.MapId == trackTier.MapId
                                                                          && trackTier.Track == track)
                           .Where(map => map.MapId == mapId)
                           .Select((map, trackTier) => new
                           {
                               map.Tier,
                               map.BasePot,
                               TrackTier = trackTier.Tier,
                           })
                           .FirstAsync();

        var config = ((int)(track == 0 ? row?.Tier ?? 1 : row?.TrackTier ?? 1),
                      (int)(row?.BasePot ?? 0));

        _trackScoreConfigCache[key] = config;

        return config;
    }

    /// <summary>
    /// Get the ranked player list for a given track (sorted by time, best per player).
    /// </summary>
    private async Task<List<RankedPlayerRow>> GetRankedPlayersAsync(ulong mapId, int style, ushort track)
    {
        const ushort stage = 0;

        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, stage);

        var players = await QueryBestRuns()
            .Where(r => r.MapId == mapId
                        && r.RunType == RunType.Main
                        && r.Style == style
                        && r.Track == track
                        && r.Stage == stage)
            .OrderBy(r => r.BestTime)
            .OrderBy(r => r.RunId)
            .Select(r => new RankedPlayerRow
            {
                SteamId = r.SteamId,
                BestTime = r.BestTime,
            })
            .ToListAsync();

        return players;
    }

    private sealed class RankedPlayerRow
    {
        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }
        public float BestTime { get; set; }
    }

    private sealed class ExistingTrackScoreRow
    {
        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }

        public uint Points { get; set; }
    }

    /// <summary>
    /// Manually trigger score recalculation for all tracks on a given map.
    /// </summary>
    public async Task<int> RecalculateMapScoresAsync(string mapName, IReadOnlyDictionary<int, double>? styleFactors = null)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);
        if (mapId is null)
        {
            return 0;
        }

        // Get map configuration
        var mapEntity = await _db.Queryable<MapEntity>()
            .Where(x => x.MapId == mapId.Value)
            .FirstAsync();

        if (mapEntity is null)
        {
            return 0;
        }

        var basePot = mapEntity.BasePot;
        var mainTier = mapEntity.Tier;

        // Query all (style, track) combinations with optional bonus track tier.
        var trackCombinations = await _db.Queryable<RunEntity>()
            .LeftJoin<MapTrackEntity>((run, bonusTier) => run.MapId == bonusTier.MapId && run.Track == bonusTier.Track)
            .Where((run, bonusTier) => run.MapId == mapId.Value && run.RunType == RunType.Main && run.Stage == 0)
            .GroupBy((run, bonusTier) => new { run.Style, run.Track, bonusTier.Tier })
            .Select((run, bonusTier) => new RecalcTrackCombinationRow
            {
                Style = run.Style,
                Track = run.Track,
                BonusTier = bonusTier.Tier,
            })
            .ToListAsync();

        if (trackCombinations.Count == 0)
        {
            return 0;
        }

        // Enqueue a recalculation request for each (style, track) combination
        foreach (var combo in trackCombinations)
        {
            var track = combo.Track;
            var tier = track == 0
                ? mainTier
                : combo.BonusTier > 0 ? combo.BonusTier : (byte)1;

            _trackScoreConfigCache[(mapId.Value, track)] = (tier, basePot);

            // Look up the style factor from the dictionary; default to 1.0 if not found
            var styleFactor = styleFactors?.TryGetValue(combo.Style, out var factor) == true ? factor : 1.0;

            _scoreRecalcScheduler.Enqueue(new RecalcRequest(mapId.Value, combo.Style, track, tier, basePot, styleFactor));
        }

        return trackCombinations.Count;
    }

    private sealed class RecalcTrackCombinationRow
    {
        public int Style { get; set; }

        public ushort Track { get; set; }

        public byte BonusTier { get; set; }
    }
}
