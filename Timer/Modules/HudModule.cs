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
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal interface IHudModule
{
}

internal class HudModule : IModule, IHudModule, ITimerModuleListener, IZoneModuleListener
{
    private const float HudUpdateInterval = 0.10f;

    private readonly InterfaceBridge _bridge;

    private readonly ITimerModule  _timerModule;
    private readonly IReplayModule _replayModule;
    private readonly IRecordModule _recordModule;
    private readonly IZoneModule   _zoneModule;

    private readonly ILogger<HudModule> _logger;

    private static readonly float[] NextHudUpdateTime = new float[PlayerSlot.MaxPlayerCount];

    // Cached WRCP diff from the last completed stage, shown inline after the main timer
    private readonly float?[] _lastStageDelta = new float?[PlayerSlot.MaxPlayerCount];

    // ReSharper disable InconsistentNaming
    private readonly IGameEvent show_survival_respawn_status_event;

    // ReSharper restore InconsistentNaming

    public HudModule(InterfaceBridge    bridge,
                     ITimerModule       timerModule,
                     IReplayModule      replayModule,
                     IRecordModule      recordModule,
                     IZoneModule        zoneModule,
                     ILogger<HudModule> logger)
    {
        _bridge       = bridge;
        _timerModule  = timerModule;
        _replayModule = replayModule;
        _recordModule = recordModule;
        _zoneModule   = zoneModule;

        _logger = logger;

        show_survival_respawn_status_event = bridge.EventManager.CreateEvent("show_survival_respawn_status", true)
                                             ?? throw new
                                                 NullReferenceException("Failed to create show_survival_respawn_status event, this should never happen?!?!?!");

        show_survival_respawn_status_event.SetInt("duration", 1);
        show_survival_respawn_status_event.SetInt("userid", -1);
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);

        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);

        _timerModule.RegisterListener(this);
        _zoneModule.RegisterListener(this);

        return true;
    }

    public void Shutdown()
    {
        show_survival_respawn_status_event.Dispose();
        _timerModule.UnregisterListener(this);
        _zoneModule.UnregisterListener(this);

        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        _lastStageDelta[controller.PlayerSlot] = null;
    }

    public void OnZoneStartTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (info.ZoneType == EZoneType.Start)
        {
            _lastStageDelta[controller.PlayerSlot] = null;
        }
    }

    public void OnPlayerStageTimerFinish(IPlayerController controller, IPlayerPawn pawn, IStageTimerInfo stageTimerInfo)
    {
        var stage = stageTimerInfo.Stage;

        if (_recordModule.GetWR(stageTimerInfo.Style, stageTimerInfo.Track, stage) is { } stageWr)
        {
            _lastStageDelta[controller.PlayerSlot] = stageTimerInfo.Time - stageWr.Time;
        }
        else
        {
            _lastStageDelta[controller.PlayerSlot] = null;
        }
    }

    private void OnGameFramePost(bool arg1, bool arg2, bool arg3)
    {
        var gameRules = _bridge.GameRules;

        if (!gameRules.IsWarmupPeriod)
        {
            gameRules.IsGameRestart = gameRules.RestartRoundTime < _bridge.GlobalVars.CurTime;
        }
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var slot = client.Slot;
        var now  = _bridge.GlobalVars.CurTime;
        var next = NextHudUpdateTime[slot];

        if (now < next && next - now <= TimerConstants.TickInterval * 2)
        {
            return;
        }

        NextHudUpdateTime[slot] = now + HudUpdateInterval;

        var pawn = param.Pawn;

        if (pawn.AsObserver() is { } observer && observer.GetObserverService() is { } observerService)
        {
            if (observerService.ObserverMode is ObserverMode.None or ObserverMode.Roaming)
            {
                return;
            }

            var observerTarget = observerService.ObserverTarget;

            if (!observerTarget.IsValid()
                || _bridge.EntityManager.FindEntityByHandle(observerTarget)?.AsPlayerPawn() is not { } targetPawn
                || targetPawn.GetController() is not { } targetController)
            {
                return;
            }

            if (targetController.IsFakeClient)
            {
                if (_replayModule.GetReplayBotData(targetController.PlayerSlot) is not ReplayBotData replayData)
                {
                    return;
                }

                PrintReplayHud(client, targetPawn, replayData);

                return;
            }

            if (_timerModule.GetTimerInfo(targetController.PlayerSlot) is not { } targetTimerInfo)
            {
                return;
            }

            PrintPlayerHud(client, targetController.PlayerSlot, targetPawn, targetTimerInfo);

            return;
        }

        if (_timerModule.GetTimerInfo(slot) is not { } timerInfo)
        {
            return;
        }

        PrintPlayerHud(client, slot, pawn, timerInfo);
    }

    private void PrintPlayerHud(IGameClient client, PlayerSlot slot, IBasePlayerPawn pawn, ITimerInfo timerInfo)
    {
        var velocity = pawn.GetAbsVelocity();

        var sb = ZString.CreateStringBuilder(true);

        try
        {
            // Compute WR diff: prefer checkpoint-based, fall back to last completed stage WRCP diff
            var    wrCheckpoints = _recordModule.GetWRCheckpoints(timerInfo.Style, timerInfo.Track);
            var    cpIndex       = timerInfo.Checkpoint;
            float? wrDelta;

            if (wrCheckpoints is { Count: > 0 } && cpIndex >= 1 && cpIndex <= wrCheckpoints.Count)
            {
                var wrCpTime = wrCheckpoints[cpIndex - 1].Time;

                var playerCpTime = timerInfo.Checkpoints.Count >= cpIndex
                    ? timerInfo.Checkpoints[cpIndex - 1].Time
                    : timerInfo.Time;

                wrDelta = playerCpTime - wrCpTime;
            }
            else
            {
                wrDelta = _lastStageDelta[slot];
            }

            // Timer color based on status: running=green, paused=yellow, stopped=white
            var timeColor = timerInfo.Status switch
            {
                ETimerStatus.Running => "#00FF00",
                ETimerStatus.Paused  => "#FFD700",
                _                    => "#FFFFFF",
            };

            sb.AppendFormat("<span color='{0}'>", timeColor);
            Utils.FormatTime(ref sb, timerInfo.Time);

            if (timerInfo.Status == ETimerStatus.Paused)
            {
                sb.Append(" ‖ PAUSED");
            }

            sb.Append("</span>");

            // WR diff inline after time
            if (wrDelta is { } delta)
            {
                if (delta >= 0f)
                {
                    sb.Append(" <span color='#FF4444'>(WR +");
                    Utils.FormatTime(ref sb, delta, true);
                }
                else
                {
                    sb.Append(" <span color='#44FF44'>(WR -");
                    Utils.FormatTime(ref sb, MathF.Abs(delta), true);
                }

                sb.Append(")</span>");
            }

            sb.Append("<br>");

            sb.AppendFormat("<span color='#FFFFFF'>{0}&nbsp;&nbsp;&nbsp;&nbsp;",
                            (int) velocity.Length2D());

            sb.Append("Sync: ");
            AppendFixedPoint1(ref sb, timerInfo.Sync * 100);
            sb.Append("%</span>");

            sb.Append("<br>");

            sb.Append("<span color='#808080'>PB: ");

            if (_recordModule.GetPlayerRecord(slot, timerInfo.Style, timerInfo.Track) is { } pb)
            {
                Utils.FormatTime(ref sb, pb.Time, true);
            }
            else
            {
                sb.Append("N/A");
            }

            sb.Append(" ‖ WR: ");

            if (_recordModule.GetWRTime(timerInfo.Style, timerInfo.Track) is { } wr)
            {
                Utils.FormatTime(ref sb, wr, true);
            }
            else
            {
                sb.Append("N/A");
            }

            sb.Append("</span>");

            PrintHtmlToPlayer(client, sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    private void PrintReplayHud(IGameClient client, IPlayerPawn pawn, ReplayBotData bot)
    {
        if (bot.Status == EReplayBotStatus.Idle)
        {
            var idleSb = ZString.CreateStringBuilder(true);
            try
            {
                idleSb.Append("<span class='fontSize-xl' color='");
                AppendRainbowHex(ref idleSb, _bridge.GlobalVars.CurTime);
                idleSb.Append("'>IDLE</span>");
                PrintHtmlToPlayer(client, idleSb.ToString());
            }
            finally
            {
                idleSb.Dispose();
            }

            return;
        }

        var          sb              = ZString.CreateStringBuilder(true);
        const string colorLabel      = "#AAAAAA";
        const string colorData       = "#E0E0E0";
        const string colorStageBot   = "#2196F3";
        const string colorFullRunBot = "#FFD700";
        const string colorSubtleInfo = "#B0B0B0";

        try
        {
            if (bot.Stage > 0)
            {
                sb.AppendFormat("<span color='{0}'>Stage {1} Replay Bot</span>", colorStageBot, bot.Stage);
            }
            else
            {
                sb.AppendFormat("<span color='{0}'>Replay Bot</span>", colorFullRunBot);

                var currentStage = bot.GetCurrentStage();

                if (currentStage > 0)
                {
                    sb.AppendFormat(" <span color='{0}'>(Stage {1})</span>", colorSubtleInfo, currentStage);
                }
            }

            sb.Append("<br>");

            var header = bot.Header!;

            sb.AppendFormat("<span color='{0}'>Player:</span> <span color='{1}'>{2}</span>",
                            colorLabel,
                            colorData,
                            header.PlayerName);

            sb.Append("<br>");

            sb.AppendFormat("<span color='{0}'>Time: </span>", colorLabel);
            var timedFrame = Math.Clamp(bot.CurrentFrame, header.PreFrame, header.PostFrame);

            sb.AppendFormat("<span color='{0}'>", colorData);
            Utils.FormatTime(ref sb, TimerConstants.TickInterval * (timedFrame - header.PreFrame));
            sb.Append('/');
            Utils.FormatTime(ref sb, bot.Time);
            sb.Append("</span>");

            sb.Append("<br>");

            sb.AppendFormat("<span color='{0}'>Speed:</span> <span color='{1}'>{2}</span>",
                            colorLabel,
                            colorData,
                            (int) MathF.Round(pawn.GetAbsVelocity().Length2D()));

            PrintHtmlToPlayer(client, sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    private static void AppendRainbowHex(ref Utf16ValueStringBuilder sb, float curtime)
    {
        const float frequency = 3.5f;
        const float amplitude = 127f;
        const float center    = 128f;

        const float sin120 = 0.86602540f;
        const float cos120 = -0.5f;

        var (sin, cos) = MathF.SinCos(frequency * curtime);

        var rBase = sin;
        var gBase = (sin * cos120) + (cos * sin120);
        var bBase = (sin * cos120) - (cos * sin120);
        var r     = (int) ((rBase * amplitude) + center);
        var g     = (int) ((gBase * amplitude) + center);
        var b     = (int) ((bBase * amplitude) + center);

        sb.Append('#');
        AppendHex2(ref sb, r);
        AppendHex2(ref sb, g);
        AppendHex2(ref sb, b);
    }

    private static void AppendHex2(ref Utf16ValueStringBuilder sb, int value)
    {
        var h1 = (value >> 4) & 0xF;
        var h2 = value        & 0xF;

        sb.Append((char) (h1 < 10 ? h1 + '0' : h1 + ('A' - 10)));
        sb.Append((char) (h2 < 10 ? h2 + '0' : h2 + ('A' - 10)));
    }

    private static void AppendFixedPoint1(ref Utf16ValueStringBuilder sb, float value)
    {
        var intPart      = (int) value;
        var decimalDigit = (int) ((value - intPart) * 10);

        sb.Append(intPart);
        sb.Append('.');
        sb.Append((char) ('0' + Math.Abs(decimalDigit)));
    }

    private void PrintHtmlToPlayer(IGameClient client, string html)
    {
        show_survival_respawn_status_event.SetString("loc_token", html);

        show_survival_respawn_status_event.FireToClient(client);
    }
}
