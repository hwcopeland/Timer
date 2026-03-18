using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_players")]
[SugarIndex("idx_surf_players_steamid", nameof(SteamId), OrderByType.Asc, true)]
internal sealed class PlayerEntity : BaseSteamIdSerialEntity
{
    [SugarColumn(Length = 192)]
    public string Name { get; set; } = string.Empty;

    public uint Points { get; set; }
    public uint Runs   { get; set; }
    public DateTime UpdatedAt { get; set; }
}
