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
using System.Runtime.CompilerServices;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Timer;

internal class TimerInfo : ITimerInfo
{
    public uint TimerTick { get; set; }

    public int TotalMeasures { get; set; }
    public int GoodSync      { get; set; }

    public float LastForwardMove { get; set; }
    public float LastLeftMove    { get; set; }
    public float LastYaw         { get; set; }

    public bool WasOnGround  { get; set; }
    public int  OnGroundTick { get; set; }

    public int Jumps   { get; set; }
    public int Strafes { get; set; }

    public ETimerStatus Status { get; private set; } = ETimerStatus.Stopped;

    public float Time => TimerTick * TimerConstants.TickInterval;

    public Vector AvgVelocity   { get; set; }
    public Vector EndVelocity   { get; set; }
    public Vector StartVelocity { get; private set; }

    public Vector MaxVelocity { get; set; }

    public float Sync => TotalMeasures > 0 ? GoodSync / (float) TotalMeasures : 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reset(bool resetJumps)
    {
        TimerTick = 0;

        if (resetJumps)
        {
            Jumps = 0;
        }

        Strafes       = 0;
        TotalMeasures = 0;
        GoodSync      = 0;
        MaxVelocity   = new ();
        AvgVelocity   = new ();
        StartVelocity = new ();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Start(Vector velocity)
    {
        Reset(Math.Abs(velocity.Z) < float.Epsilon || Jumps > 1);
        Status        = ETimerStatus.Running;
        StartVelocity = velocity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Stop()
    {
        Reset(true);
        Status = ETimerStatus.Stopped;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool IsTimerRunning()
        => Status == ETimerStatus.Running;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool IsTimerPaused()
        => Status == ETimerStatus.Paused;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool PauseTimer()
    {
        if (Status != ETimerStatus.Running)
            return false;

        Status = ETimerStatus.Paused;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool ResumeTimer()
    {
        if (Status != ETimerStatus.Paused)
            return false;

        Status = ETimerStatus.Running;
        return true;
    }

    public TimerInfo()
    {
        Reset(true);
    }

    private List<CheckpointInfo> CheckpointInfoInternal { get; } = [];

    // ITimerInfo
    public int Checkpoint { get; set; } = 0;

    public IReadOnlyList<CheckpointInfo> Checkpoints => CheckpointInfoInternal;

    public void ChangeStyle(int style)
    {
        StopTimer();
        Style = Math.Max(style, 0);
    }

    public void ChangeTrack(int track)
    {
        StopTimer();
        Track = Math.Max(track, 0);
    }

    public EZoneType InZone { get; private set; } = EZoneType.Invalid;
    public int       Style  { get; private set; } = 0;
    public int       Track  { get; private set; }

    public CheckpointInfo? CurrentCheckpointInfo { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateInZone(EZoneType type)
        => InZone = type;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StopTimer()
    {
        Stop();
        CheckpointInfoInternal.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StartTimer(int track, Vector velocity)
    {
        Start(velocity);

        Track      = track;
        Checkpoint = 0;
        CheckpointInfoInternal.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddCheckpoint(CheckpointInfo info)
    {
        CheckpointInfoInternal.Add(info);
    }
}

internal class StageTimerInfo : TimerInfo, IStageTimerInfo
{
    public int Stage { get; set; } = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StartTimer(int track, Vector velocity, int stage)
    {
        base.StartTimer(track, velocity);
        Stage = stage;
    }
}
