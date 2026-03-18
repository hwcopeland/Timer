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

using Microsoft.Extensions.DependencyInjection;
using Source2Surf.Timer.Managers.Command;
using Source2Surf.Timer.Managers.Patch;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers;

internal static class ManagerDi
{
    public static void AddManagerService(this IServiceCollection services)
    {
        services.AddSingleton<RequestManagerLiteDB>();
        services.ImplSingleton<IRequestManager, IManager, RequestManagerProxy>();

        services.ImplSingleton<IInlineHookManager, IManager, InlineHookManager>();
        services.ImplSingleton<IPatchManager, IManager, PatchManager>();
        services.ImplSingleton<IEventHookManager, IManager, EventHookManager>();

        services.ImplSingleton<IPlayerManager, IManager, PlayerManager>();

        services.AddSingleton<CommandManager>();
        services.ImplSingleton<ICommandManager, IManager, CommandManagerProxy>();

        services.AddSingleton<ReplayProviderProxy>();
    }
}
