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
using System.Threading.Tasks;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class RecordModule
{

    private ECommandAction OnCommandStageWR(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;

        var stage = command.TryGet<byte>(1) is { } s ? (int)s : 0;

        if (stage < 1)
        {
            controller.PrintToChat("Usage: !swr <stage>");
            return ECommandAction.Handled;
        }

        var wr = _mapCache.GetWR(style, track, stage);

        if (wr is null)
        {
            controller.PrintToChat($"No SR found for stage {stage}.");
            return ECommandAction.Handled;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Stage ");
            sb.Append(stage);
            sb.Append(" SR: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, wr.Time, true);
            sb.Append(ChatColor.White);
            sb.Append(" by ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(wr.PlayerName);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusTop(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;

        var bonus = command.TryGet<byte>(1) is { } b ? (int)b : 1;

        if (bonus < 1)
        {
            controller.PrintToChat("Usage: !btop [bonus]");
            return ECommandAction.Handled;
        }

        var records = _mapCache.GetRecords(style, bonus);

        if (records.Count == 0)
        {
            controller.PrintToChat($"No records found for bonus {bonus}.");
            return ECommandAction.Handled;
        }

        controller.PrintToChat($"Top records for Bonus {bonus}:");

        var count = Math.Min(records.Count, 10);

        for (var i = 0; i < count; i++)
        {
            var rec = records[i];
            var sb  = ZString.CreateStringBuilder(true);
            try
            {
                sb.Append('#');
                sb.Append(i + 1);
                sb.Append(": ");
                sb.Append(ChatColor.LightGreen);
                Utils.FormatTime(ref sb, rec.Time, true);
                sb.Append(ChatColor.White);
                sb.Append(" - ");
                sb.Append(ChatColor.LightGreen);
                sb.Append(rec.PlayerName);
                sb.Append(ChatColor.White);

                controller.PrintToChat(sb.ToString());
            }
            finally
            {
                sb.Dispose();
            }
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusWR(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;

        var bonus = command.TryGet<byte>(1) is { } b ? (int)b : 1;

        if (bonus < 1)
        {
            controller.PrintToChat("Usage: !bwr [bonus]");
            return ECommandAction.Handled;
        }

        var wr = GetWR(style, bonus);

        if (wr is null)
        {
            controller.PrintToChat($"No SR found for bonus {bonus}.");
            return ECommandAction.Handled;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Bonus ");
            sb.Append(bonus);
            sb.Append(" SR: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, wr.Time, true);
            sb.Append(ChatColor.White);
            sb.Append(" by ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(wr.PlayerName);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusPB(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;

        var bonus = command.TryGet<byte>(1) is { } b ? (int)b : 1;

        if (bonus < 1)
        {
            controller.PrintToChat("Usage: !bpb [bonus]");
            return ECommandAction.Handled;
        }

        var pb = GetPlayerRecord(slot, style, bonus);

        if (pb is null)
        {
            controller.PrintToChat($"No PB found for bonus {bonus}.");
            return ECommandAction.Handled;
        }

        var records = _mapCache.GetRecords(style, bonus);
        var rank    = 1;
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Time < pb.Time)
                rank = i + 2;
            else
                break;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Bonus ");
            sb.Append(bonus);
            sb.Append(" PB: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, pb.Time, true);
            sb.Append(ChatColor.White);
            sb.Append(" (#");
            sb.Append(rank);
            sb.Append('/');
            sb.Append(records.Count);
            sb.Append(')');

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandStagePB(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;

        var stage = command.TryGet<byte>(1) is { } s ? (int)s : 0;

        if (stage < 1)
        {
            controller.PrintToChat("Usage: !spb <stage>");
            return ECommandAction.Handled;
        }

        var pb = GetPlayerRecord(slot, style, track, stage);

        if (pb is null)
        {
            controller.PrintToChat($"No PB found for stage {stage}.");
            return ECommandAction.Handled;
        }

        var wr = _mapCache.GetWR(style, track, stage);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Stage ");
            sb.Append(stage);
            sb.Append(" PB: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, pb.Time, true);
            sb.Append(ChatColor.White);

            if (wr is not null)
            {
                var delta = pb.Time - wr.Time;
                sb.Append(" (SR ");
                if (delta >= 0f)
                {
                    sb.Append(ChatColor.Red);
                    sb.Append('+');
                }
                else
                {
                    sb.Append(ChatColor.LightGreen);
                    sb.Append('-');
                }
                Utils.FormatTime(ref sb, MathF.Abs(delta), true);
                sb.Append(ChatColor.White);
                sb.Append(')');
            }

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandClearRecords(StringCommand stringCommand)
    {
        _request.RemoveMapRecords(_bridge.GlobalVars.MapName);

        _mapCache.Clear();
        _playerCache.ClearAll();

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandWR(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;

        var wr = GetWR(style, track);

        if (wr is null)
        {
            controller.PrintToChat($"No SR found for this track.");
            return ECommandAction.Handled;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("SR: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, wr.Time, true);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandPB(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat($"No personal best found for this track.");
            return ECommandAction.Handled;
        }

        var rank  = GetRankForTime(style, track, pb.Time);
        var total = GetTotalRecordCount(style, track);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("PB: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, pb.Time, true);
            sb.Append(ChatColor.White);
            sb.Append(" (#");
            sb.Append(rank);
            sb.Append('/');
            sb.Append(total);
            sb.Append(')');

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandRank(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat($"No record found. Complete the map first.");
            return ECommandAction.Handled;
        }

        var rank  = GetRankForTime(style, track, pb.Time);
        var total = GetTotalRecordCount(style, track);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Rank: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append('#');
            sb.Append(rank);
            sb.Append(ChatColor.White);
            sb.Append('/');
            sb.Append(total);
            sb.Append(" | PB: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, pb.Time, true);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandTop(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;

        var wr = GetWR(style, track);

        if (wr is null)
        {
            controller.PrintToChat($"No records found for this track.");
            return ECommandAction.Handled;
        }

        var total = GetTotalRecordCount(style, track);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("#1: ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, wr.Time, true);
            sb.Append(ChatColor.White);
            sb.Append(" (");
            sb.Append(total);
            sb.Append(" records)");

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandCpr(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat("No personal best found for this track.");
            return ECommandAction.Handled;
        }

        var wrCheckpoints = _mapCache.GetWRCheckpoints(style, track);

        if (wrCheckpoints is not { Count: > 0 })
        {
            controller.PrintToChat("No SR checkpoints available.");
            return ECommandAction.Handled;
        }

        // Load PB checkpoints from DB
        Task.Run(async () =>
        {
            try
            {
                var pbCheckpoints = await RetryHelper.RetryAsync(
                    () => _request.GetRecordCheckpoints(pb.Id),
                    RetryHelper.IsTransient, _logger, "GetRecordCheckpoints"
                ).ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (pbCheckpoints.Count == 0)
                    {
                        controller.PrintToChat("No checkpoint data for your PB.");
                        return;
                    }

                    var count = Math.Min(pbCheckpoints.Count, wrCheckpoints.Count);

                    controller.PrintToChat("PB vs SR checkpoints:");

                    for (var i = 0; i < count; i++)
                    {
                        var pbCp  = pbCheckpoints[i];
                        var wrCp  = wrCheckpoints[i];
                        var delta = pbCp.Time - wrCp.Time;

                        var sb = ZString.CreateStringBuilder(true);
                        try
                        {
                            sb.Append("CP");
                            sb.Append(i + 1);
                            sb.Append(": ");
                            sb.Append(ChatColor.LightGreen);
                            Utils.FormatTime(ref sb, pbCp.Time, true);
                            sb.Append(ChatColor.White);
                            sb.Append(" | SR ");

                            if (delta >= 0f)
                            {
                                sb.Append(ChatColor.Red);
                                sb.Append('+');
                            }
                            else
                            {
                                sb.Append(ChatColor.LightGreen);
                                sb.Append('-');
                            }

                            Utils.FormatTime(ref sb, MathF.Abs(delta), true);
                            sb.Append(ChatColor.White);

                            controller.PrintToChat(sb.ToString());
                        }
                        finally
                        {
                            sb.Dispose();
                        }
                    }

                    // Final time diff
                    var wr = GetWR(style, track);

                    if (wr is not null)
                    {
                        var finalDelta = pb.Time - wr.Time;
                        var sb2 = ZString.CreateStringBuilder(true);
                        try
                        {
                            sb2.Append("Final: ");
                            sb2.Append(ChatColor.LightGreen);
                            Utils.FormatTime(ref sb2, pb.Time, true);
                            sb2.Append(ChatColor.White);
                            sb2.Append(" | SR ");

                            if (finalDelta >= 0f)
                            {
                                sb2.Append(ChatColor.Red);
                                sb2.Append('+');
                            }
                            else
                            {
                                sb2.Append(ChatColor.LightGreen);
                                sb2.Append('-');
                            }

                            Utils.FormatTime(ref sb2, MathF.Abs(finalDelta), true);
                            sb2.Append(ChatColor.White);

                            controller.PrintToChat(sb2.ToString());
                        }
                        finally
                        {
                            sb2.Dispose();
                        }
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when fetching checkpoint comparison");
            }
        }, _bridge.CancellationToken);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandProfile(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var profile = _playerManager.GetPlayerProfile(slot);

        if (profile is null)
        {
            controller.PrintToChat("Profile not loaded yet.");
            return ECommandAction.Handled;
        }

        var timerInfo = _timerModule.GetTimerInfo(slot);
        var style     = timerInfo?.Style ?? 0;
        var track     = timerInfo?.Track ?? 0;
        var steamId   = client.SteamId;

        // PB + rank (sync, from cache)
        var pb    = GetPlayerRecord(slot, style, track);
        var pbStr = "";

        {
            var sb = ZString.CreateStringBuilder(true);
            try
            {
                sb.Append("Map PB: ");

                if (pb is not null)
                {
                    var rank  = GetRankForTime(style, track, pb.Time);
                    var total = GetTotalRecordCount(style, track);

                    sb.Append(ChatColor.LightGreen);
                    Utils.FormatTime(ref sb, pb.Time, true);
                    sb.Append(ChatColor.White);
                    sb.Append(" (#");
                    sb.Append(rank);
                    sb.Append('/');
                    sb.Append(total);
                    sb.Append(')');
                }
                else
                {
                    sb.Append(ChatColor.Grey);
                    sb.Append("None");
                    sb.Append(ChatColor.White);
                }

                pbStr = sb.ToString();
            }
            finally
            {
                sb.Dispose();
            }
        }

        // Fetch points rank from DB
        Task.Run(async () =>
        {
            try
            {
                var (pointsRank, totalRanked) = await RetryHelper.RetryAsync(
                    () => _request.GetPlayerPointsRank(steamId),
                    RetryHelper.IsTransient, _logger, "GetPlayerPointsRank"
                ).ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    // Line 1: Player name + Points + Rank
                    var sb1 = ZString.CreateStringBuilder(true);
                    try
                    {
                        sb1.Append("Player: ");
                        sb1.Append(ChatColor.LightGreen);
                        sb1.Append(profile.Name);
                        sb1.Append(ChatColor.White);
                        sb1.Append(" | Points: ");
                        sb1.Append(ChatColor.LightGreen);
                        sb1.Append(profile.Points);
                        sb1.Append(ChatColor.White);

                        if (pointsRank > 0)
                        {
                            sb1.Append(" (#");
                            sb1.Append(pointsRank);
                            sb1.Append('/');
                            sb1.Append(totalRanked);
                            sb1.Append(')');
                        }

                        controller.PrintToChat(sb1.ToString());
                    }
                    finally
                    {
                        sb1.Dispose();
                    }

                    // Line 2: Map PB + Rank
                    controller.PrintToChat(pbStr);

                    // Line 3: Join date + Last seen
                    var sb3 = ZString.CreateStringBuilder(true);
                    try
                    {
                        sb3.Append("Joined: ");
                        sb3.Append(ChatColor.Grey);
                        sb3.Append(profile.JoinDate.ToString("yyyy-MM-dd"));
                        sb3.Append(ChatColor.White);
                        sb3.Append(" | Last seen: ");
                        sb3.Append(ChatColor.Grey);
                        sb3.Append(profile.LastSeenDate.ToString("yyyy-MM-dd"));
                        sb3.Append(ChatColor.White);

                        controller.PrintToChat(sb3.ToString());
                    }
                    finally
                    {
                        sb3.Dispose();
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when fetching player profile rank");
            }
        }, _bridge.CancellationToken);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandRecent(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var mapName = _bridge.GlobalVars.MapName;
        var steamId = client.SteamId;

        Task.Run(async () =>
        {
            try
            {
                var records = await RetryHelper.RetryAsync(
                    () => _request.GetRecentRecords(mapName, steamId),
                    RetryHelper.IsTransient, _logger, "GetRecentRecords"
                ).ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (records.Count == 0)
                    {
                        controller.PrintToChat("No recent records.");
                        return;
                    }

                    controller.PrintToChat("Recent records:");

                    foreach (var record in records)
                    {
                        var sb = ZString.CreateStringBuilder(true);
                        try
                        {
                            sb.Append(ChatColor.LightGreen);
                            Utils.FormatTime(ref sb, record.Time, true);
                            sb.Append(ChatColor.White);

                            if (record.Track > 0)
                            {
                                sb.Append(" B");
                                sb.Append(record.Track);
                            }

                            sb.Append(" | ");
                            sb.Append(ChatColor.Grey);
                            sb.Append(record.RunDate.ToString("MM-dd HH:mm"));
                            sb.Append(ChatColor.White);

                            controller.PrintToChat(sb.ToString());
                        }
                        finally
                        {
                            sb.Dispose();
                        }
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when fetching recent records");
            }
        }, _bridge.CancellationToken);

        return ECommandAction.Handled;
    }
}
