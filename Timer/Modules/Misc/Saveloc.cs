/*
 * Saveloc — save/teleport practice system for surf.
 *
 * !save / !cp      — save current position + angles + velocity
 * !tele / !gocp    — teleport back to saved position
 * !clearcp         — clear saved position
 *
 * Saving a loc stops the timer (practice mode). Teleporting also
 * stops it. Timer only restarts on zone entry.
 */

using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

internal unsafe partial class MiscModule
{
    // Per-player saved position. Indexed by raw slot value.
    private readonly SavedPosition?[] _savedPositions = new SavedPosition?[64];

    private record struct SavedPosition(Vector Origin, Vector Angles, Vector Velocity);

    private void AddSavelocCommands()
    {
        _commandManager.AddClientChatCommand("save",    OnCommandSave);
        _commandManager.AddClientChatCommand("cp",      OnCommandSave);
        _commandManager.AddClientChatCommand("tele",    OnCommandTele);
        _commandManager.AddClientChatCommand("gocp",    OnCommandTele);
        _commandManager.AddClientChatCommand("clearcp", OnCommandClearCp);
    }

    private void SavelocReply(PlayerSlot slot, string msg)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { } client
            && client.GetPlayerController() is { } controller)
        {
            controller.PrintToChat(msg);
        }
    }

    private ECommandAction OnCommandSave(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.EntityManager.FindPlayerPawnBySlot(slot) is not { } basePawn
            || basePawn.AsPlayer() is not { IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        var origin   = pawn.GetAbsOrigin();
        var angles   = pawn.GetEyeAngles();
        var velocity = pawn.GetAbsVelocity();

        _savedPositions[(byte)slot] = new SavedPosition(origin, angles, velocity);
        SavelocReply(slot, "[surf] Position saved.");

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandTele(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.EntityManager.FindPlayerPawnBySlot(slot) is not { } basePawn
            || basePawn.AsPlayer() is not { IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        var saved = _savedPositions[(byte)slot];
        if (saved is null)
        {
            SavelocReply(slot, "[surf] No saved position. Use !save first.");
            return ECommandAction.Handled;
        }

        var pos = saved.Value;
        pawn.Teleport(pos.Origin, pos.Angles, pos.Velocity);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandClearCp(PlayerSlot slot, StringCommand command)
    {
        _savedPositions[(byte)slot] = null;
        SavelocReply(slot, "[surf] Saved position cleared.");
        return ECommandAction.Handled;
    }
}
