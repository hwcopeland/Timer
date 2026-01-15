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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Request.Models;

namespace Source2Surf.Timer.Modules;

internal interface IMessageModule
{
}

internal class MessageModule : IModule, IMessageModule
{
    private readonly InterfaceBridge _bridge;
    private readonly IPlayerManager  _playerManager;
    private readonly IRecordModule   _recordModule;

    private readonly ILogger<MessageModule> _logger;

    public MessageModule(InterfaceBridge        bridge,
                         IPlayerManager         playerManager,
                         IRecordModule          recordModule,
                         ILogger<MessageModule> logger)
    {
        _bridge        = bridge;
        _playerManager = playerManager;
        _recordModule  = recordModule;

        _logger = logger;
    }

    public bool Init()
    {
        _recordModule.OnPlayerRecordSaved      += OnPlayerRecordSaved;
        _recordModule.OnPlayerStageRecordSaved += OnPlayerStageRecordSaved;

        return true;
    }

    public void Shutdown()
    {
        _recordModule.OnPlayerRecordSaved      -= OnPlayerRecordSaved;
        _recordModule.OnPlayerStageRecordSaved -= OnPlayerStageRecordSaved;
    }

    private void OnPlayerRecordSaved(SteamID        playerSteamId,
                                     string         playerName,
                                     EAttemptResult recordType,
                                     RunRecord      savedRecord,
                                     RunRecord?     wrRecord,
                                     RunRecord?     pbRecord)
    {
        switch (recordType)
        {
            case EAttemptResult.NewPersonalRecord:
            {
                PrintNewPersonalBestMessage(playerName, savedRecord, pbRecord);

                break;
            }
            case EAttemptResult.NewServerRecord:
            {
                PrintNewServerRecordMessage();

                break;
            }
            case EAttemptResult.NoNewRecord:
            {
                PrintNoNewRecordMessage(playerSteamId, savedRecord, pbRecord);

                break;
            }
            default:
                throw new NotImplementedException($"Type {recordType} is not implemented");
        }
    }

    private void PrintNewPersonalBestMessage(string playerName, RunRecord savedRecord, RunRecord? pbRecord)
    {
        // placeholder
        _bridge.ModSharp.PrintToChatWithPrefix(playerName);
    }

    private void PrintNewServerRecordMessage()
    {
        // placeholder
        _bridge.ModSharp.PrintToChatWithPrefix("playerName");
    }

    private void PrintNoNewRecordMessage(SteamID steamId, RunRecord savedRecord, RunRecord? pbRecord)
    {
        if (_playerManager.GetPlayer(steamId) is not { } player || player.Controller is not { IsValidEntity: true } controller)
        {
            return;
        }
    }

    private void OnPlayerStageRecordSaved(SteamID        playerSteamId,
                                          string         playerName,
                                          EAttemptResult recordType,
                                          RunRecord      savedRecord,
                                          RunRecord?     wrRecord,
                                          RunRecord?     pbRecord)
    {
        switch (recordType)
        {
            case EAttemptResult.NewPersonalRecord:
            {
                break;
            }
            case EAttemptResult.NewServerRecord:
                break;
            case EAttemptResult.NoNewRecord:
                break;
            default:
                throw new NotImplementedException($"Type {recordType} is not implemented");
        }
    }
}
