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
using System.Collections.Immutable;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Command;

internal sealed class CommandManagerProxy : IManager, ICommandManager
{
    private readonly ISharedSystem                  _shared;
    private readonly CommandManager                 _fallback;
    private readonly ILogger<CommandManagerProxy>   _logger;

    private ICommandManager _current;
    private bool            _fallbackInitialized;

    public CommandManagerProxy(ISharedSystem                shared,
                               CommandManager               fallback,
                               ILogger<CommandManagerProxy>  logger)
    {
        _shared   = shared;
        _fallback = fallback;
        _current  = fallback;
        _logger   = logger;
    }

    private ICommandManager Current => Volatile.Read(ref _current);

    public bool Init()
    {
        try
        {
            RefreshManager();

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to initialize command manager proxy.");

            return false;
        }
    }

    public void Shutdown()
    {
        if (_fallbackInitialized)
        {
            _fallback.Shutdown();
            _fallbackInitialized = false;
        }
    }

    public void RefreshManager()
    {
        var external = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<ICommandManager>(ICommandManager.Identity)
                              ?.Instance;

        if (external is not null && !ReferenceEquals(external, this))
        {
            Use(external, external.GetType().FullName);

            return;
        }

        UseFallback();
    }

    public void Use(ICommandManager manager, string? providerName = null)
    {
        if (ReferenceEquals(manager, _fallback))
        {
            EnsureFallbackInitialized();
        }

        if (ReferenceEquals(Current, manager))
        {
            return;
        }

        Volatile.Write(ref _current, manager);

        if (!ReferenceEquals(manager, _fallback))
        {
            if (!string.IsNullOrWhiteSpace(providerName))
            {
                _logger.LogInformation("Using external ICommandManager from {provider}.", providerName);
            }
            else
            {
                _logger.LogInformation("Using custom ICommandManager instance.");
            }
        }
        else
        {
            _logger.LogInformation("Using built-in ICommandManager: {type}",
                                   _fallback.GetType()
                                            .FullName);
        }
    }

    public void UseFallback()
        => Use(_fallback);

    private readonly object _fallbackLock = new();

    private void EnsureFallbackInitialized()
    {
        if (Volatile.Read(ref _fallbackInitialized))
        {
            return;
        }

        lock (_fallbackLock)
        {
            if (_fallbackInitialized)
            {
                return;
            }

            if (!_fallback.Init())
            {
                throw new InvalidOperationException("Failed to initialize built-in CommandManager.");
            }

            Volatile.Write(ref _fallbackInitialized, true);
        }
    }

    public void AddClientChatCommand(string command, ICommandManager.ClientCommandDelegate handler)
        => Current.AddClientChatCommand(command, handler);

    public void AddAdminChatCommand(string command, ImmutableArray<string> permissions, ICommandManager.ClientCommandDelegate handler)
        => Current.AddAdminChatCommand(command, permissions, handler);

    public void AddServerCommand(string command, Func<StringCommand, ECommandAction> handler)
        => Current.AddServerCommand(command, handler);

    public void AddStyleCommand(string command, ICommandManager.ClientCommandDelegate handler)
        => Current.AddStyleCommand(command, handler);

    public void ClearStyleCommands()
        => Current.ClearStyleCommands();
}
