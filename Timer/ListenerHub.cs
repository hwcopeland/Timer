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
using Microsoft.Extensions.Logging;

namespace Source2Surf.Timer;

internal sealed class ListenerHub<T> where T : class
{
    private readonly List<T> _listeners = [];
    private readonly ILogger _logger;

    public T[] Snapshot { get; private set; } = [];

    public ListenerHub(ILogger logger)
    {
        _logger = logger;
    }

    public void Register(T listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        if (!_listeners.Contains(listener))
        {
            _listeners.Add(listener);
            Snapshot = [.. _listeners];
        }
    }

    public void Unregister(T? listener)
    {
        if (listener is null)
        {
            return;
        }

        if (_listeners.Remove(listener))
        {
            Snapshot = [.. _listeners];
        }
    }

    public void Clear()
    {
        _listeners.Clear();
        Snapshot = [];
    }
}
