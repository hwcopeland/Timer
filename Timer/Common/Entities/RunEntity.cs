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
using Source2Surf.Timer.Common.Enums;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_runs")]
internal sealed class RunEntity : BaseSteamIdSerialEntity
{
    public Guid     MapId   { get; set; }
    public RunStyle Style   { get; set; }
    public ushort   Track   { get; set; }
    public double   Time    { get; set; }
    public uint     Jumps   { get; set; }
    public uint     Strafes { get; set; }
    public double   Sync    { get; set; }

    [SugarColumn(ColumnName = "velStartX")]
    public double VelocityStartX { get; set; }

    [SugarColumn(ColumnName = "velStartY")]
    public double VelocityStartY { get; set; }

    [SugarColumn(ColumnName = "velStartZ")]
    public double VelocityStartZ { get; set; }

    [SugarColumn(ColumnName = "velEndX")]
    public double VelocityEndX { get; set; }

    [SugarColumn(ColumnName = "velEndY")]
    public double VelocityEndY { get; set; }

    [SugarColumn(ColumnName = "velEndZ")]
    public double VelocityEndZ { get; set; }

    [SugarColumn(ColumnName = "velMaxX")]
    public double VelocityMaxX { get; set; }

    [SugarColumn(ColumnName = "velMaxY")]
    public double VelocityMaxY { get; set; }

    [SugarColumn(ColumnName = "velMaxZ")]
    public double VelocityMaxZ { get; set; }

    [SugarColumn(ColumnName = "velAvgX")]
    public double VelocityAvgX { get; set; }

    [SugarColumn(ColumnName = "velAvgY")]
    public double VelocityAvgY { get; set; }

    [SugarColumn(ColumnName = "velAvgZ")]
    public double VelocityAvgZ { get; set; }

    public DateTime Date { get; set; }
}
