using System;
using Sharp.Shared.Units;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_player_track_scores")]
[SugarIndex("idx_player_track_scores_steam_map_style_track",
            nameof(SteamId), OrderByType.Asc,
            nameof(MapId), OrderByType.Asc,
            nameof(Style), OrderByType.Asc,
            nameof(Track), OrderByType.Asc,
            true)]  // Unique index
internal sealed class PlayerTrackScoreEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
    public SteamID SteamId { get; set; }

    public ulong MapId { get; set; }
    public int Style { get; set; }
    public ushort Track { get; set; }
    public uint Points { get; set; }
    public DateTime UpdatedAt { get; set; }
}
