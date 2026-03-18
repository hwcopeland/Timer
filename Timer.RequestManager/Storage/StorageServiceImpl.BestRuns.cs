using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    private ISugarQueryable<PlayerBestRunEntity> QueryBestRuns() =>
        _db.Queryable<PlayerBestRunEntity>();

    private async Task EnsureBestRunsSeededForMapAsync(ulong mapId, RunType runType)
    {
        var seedKey = (mapId, runType);

        if (!_bestRunMapSeededCache.TryAdd(seedKey, 0))
        {
            return;
        }

        try
        {
            var baseQuery = _db.Queryable<RunEntity>()
                               .Where(x => x.MapId      == mapId
                                           && x.RunType == runType);

            if (runType == RunType.Main)
            {
                baseQuery = baseQuery.Where(x => x.Stage == 0);
            }
            else
            {
                baseQuery = baseQuery.Where(x => x.Stage > 0);
            }

            var rows = await baseQuery.Select(x => new SeedBestRunRow
                                      {
                                          SteamId  = x.SteamId,
                                          Style    = x.Style,
                                          Track    = x.Track,
                                          Stage    = x.Stage,
                                          RunId    = x.Id,
                                          BestTime = x.Time,
                                          RowNum
                                              = SqlFunc.RowNumber($"{nameof(RunEntity.Time)} ASC, {nameof(RunEntity.Id)} ASC",
                                                                  $"{nameof(RunEntity.Style)}, {nameof(RunEntity.Track)}, {nameof(RunEntity.Stage)}, {nameof(RunEntity.SteamId)}"),
                                      })
                                      .MergeTable()
                                      .Where(t => t.RowNum == 1)
                                      .ToListAsync();

            // Clear RowNum helper field before upsert
            foreach (var row in rows)
            {
                row.RowNum = 0;
            }

            await UpsertSeedBestRowsAsync(mapId, runType, rows);
        }
        catch
        {
            _bestRunMapSeededCache.TryRemove(seedKey, out _);

            throw;
        }
    }

    private async Task EnsureBestRunsSeededAsync(ulong mapId, RunType runType, int style, ushort track, ushort stage)
    {
        if (_bestRunMapSeededCache.ContainsKey((mapId, runType)))
        {
            return;
        }

        var seedKey = (mapId, runType, style, track, stage);

        if (!_bestRunSeededCache.TryAdd(seedKey, 0))
        {
            return;
        }

        try
        {
            var hasBestRows = await QueryBestRuns().Where(x => x.MapId      == mapId
                                                               && x.RunType == runType
                                                               && x.Style   == style
                                                               && x.Track   == track
                                                               && x.Stage   == stage)
                                                   .AnyAsync();

            if (hasBestRows)
            {
                return;
            }

            var rows = await _db.Queryable<RunEntity>()
                                .Where(x => x.MapId      == mapId
                                            && x.RunType == runType
                                            && x.Style   == style
                                            && x.Track   == track
                                            && x.Stage   == stage)
                                .Select(x => new SeedBestRunRow
                                {
                                    SteamId  = x.SteamId,
                                    Style    = style,
                                    Track    = track,
                                    Stage    = stage,
                                    RunId    = x.Id,
                                    BestTime = x.Time,
                                    RowNum = SqlFunc.RowNumber($"{nameof(RunEntity.Time)} ASC, {nameof(RunEntity.Id)} ASC",
                                                               nameof(RunEntity.SteamId)),
                                })
                                .MergeTable()
                                .Where(t => t.RowNum == 1)
                                .ToListAsync();

            // Clear RowNum helper field before upsert
            foreach (var row in rows)
            {
                row.RowNum = 0;
            }

            await UpsertSeedBestRowsAsync(mapId, runType, rows);
        }
        catch
        {
            _bestRunSeededCache.TryRemove(seedKey, out _);

            throw;
        }
    }

    private async Task UpsertPlayerBestRunAsync(RunEntity run)
    {
        var now = DateTime.UtcNow;

        var updated = await _db.Updateable<PlayerBestRunEntity>()
                               .SetColumns(x => x.RunId     == run.Id)
                               .SetColumns(x => x.BestTime  == run.Time)
                               .SetColumns(x => x.UpdatedAt == now)
                               .Where(x => x.SteamId    == run.SteamId
                                           && x.MapId   == run.MapId
                                           && x.RunType == run.RunType
                                           && x.Style   == run.Style
                                           && x.Track   == run.Track
                                           && x.Stage   == run.Stage
                                           && (x.BestTime > run.Time
                                               || (x.BestTime == run.Time && x.RunId > run.Id)))
                               .ExecuteCommandAsync();

        if (updated > 0)
        {
            return;
        }

        var entity = new PlayerBestRunEntity
        {
            SteamId   = run.SteamId,
            MapId     = run.MapId,
            RunType   = run.RunType,
            Stage     = run.Stage,
            Style     = run.Style,
            Track     = run.Track,
            RunId     = run.Id,
            BestTime  = run.Time,
            UpdatedAt = now,
        };

        try
        {
            await _db.Insertable(entity).ExecuteCommandAsync();
        }
        catch
        {
            await _db.Updateable<PlayerBestRunEntity>()
                     .SetColumns(x => x.RunId     == run.Id)
                     .SetColumns(x => x.BestTime  == run.Time)
                     .SetColumns(x => x.UpdatedAt == now)
                     .Where(x => x.SteamId    == run.SteamId
                                 && x.MapId   == run.MapId
                                 && x.RunType == run.RunType
                                 && x.Style   == run.Style
                                 && x.Track   == run.Track
                                 && x.Stage   == run.Stage
                                 && (x.BestTime > run.Time
                                     || (x.BestTime == run.Time && x.RunId > run.Id)))
                     .ExecuteCommandAsync();
        }
    }

    private async Task UpsertSeedBestRowsAsync(ulong mapId, RunType runType, List<SeedBestRunRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var now      = DateTime.UtcNow;
        var entities = new List<PlayerBestRunEntity>(rows.Count);

        foreach (var row in rows)
        {
            entities.Add(new PlayerBestRunEntity
            {
                SteamId   = row.SteamId,
                MapId     = mapId,
                RunType   = runType,
                Stage     = row.Stage,
                Style     = row.Style,
                Track     = row.Track,
                RunId     = row.RunId,
                BestTime  = row.BestTime,
                UpdatedAt = now,
            });
        }

        var storage = _db.Storageable(entities)
                         .WhereColumns(x => new
                         {
                             x.SteamId,
                             x.MapId,
                             x.RunType,
                             x.Style,
                             x.Track,
                             x.Stage,
                         })
                         .ToStorage();

        if (storage.InsertList.Count > 0)
        {
            await storage.AsInsertable.ExecuteCommandAsync();
        }

        if (storage.UpdateList.Count > 0)
        {
            await storage.AsUpdateable
                         .UpdateColumns(x => new { x.RunId, x.BestTime, x.UpdatedAt })
                         .ExecuteCommandAsync();
        }
    }

    private void RemoveBestRunSeedCacheForMap(ulong mapId)
    {
        foreach (var key in _bestRunMapSeededCache.Keys)
        {
            if (key.mapId == mapId)
            {
                _bestRunMapSeededCache.TryRemove(key, out _);
            }
        }

        foreach (var key in _bestRunSeededCache.Keys)
        {
            if (key.mapId == mapId)
            {
                _bestRunSeededCache.TryRemove(key, out _);
            }
        }
    }

    private sealed class SeedBestRunRow
    {
        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }

        public int Style { get; set; }

        public ushort Track { get; set; }

        public ushort Stage { get; set; }

        public ulong RunId { get; set; }

        public float BestTime { get; set; }

        [SugarColumn(IsOnlyIgnoreInsert = true, IsOnlyIgnoreUpdate = true)]
        public int RowNum { get; set; }
    }
}
