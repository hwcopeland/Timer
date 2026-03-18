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
using System.Collections;
using System.Collections.Generic;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules.Replay;

internal sealed class ReplayFrameSlice : IReadOnlyList<ReplayFrameData>
{
    private readonly IReadOnlyList<ReplayFrameData> _source;
    private readonly int                            _offset;
    private readonly int                            _count;

    public ReplayFrameSlice(IReadOnlyList<ReplayFrameData> source, int offset, int count)
    {
        _source = source;
        _offset = offset;
        _count  = count;
    }

    public int Count => _count;

    public ReplayFrameData this[int index]
    {
        get
        {
            if ((uint) index >= (uint) _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _source[_offset + index];
        }
    }

    public IEnumerator<ReplayFrameData> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _source[_offset + i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}
