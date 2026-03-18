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

using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Shared.Interfaces.Modules;

/// <summary>
/// Public interface for the replay module, providing bot data access and listener management.
/// </summary>
public interface IReplayModule
{
    IReplayBotData? GetReplayBotData(PlayerSlot slot);
    IReplayBotData? GetReplayBotByIndex(int index);
    int GetReplayBotCount();

    void RegisterListener(IReplayModuleListener listener);
    void UnregisterListener(IReplayModuleListener listener);
}
