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

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
/// Simple ring buffer to avoid front-shifting allocations when trimming pre-run frames.
/// </summary>
internal sealed class ReplayFrameBuffer : IReadOnlyList<ReplayFrameData>
{
    private ReplayFrameData[] _buffer;
    private int               _head;

    public ReplayFrameBuffer(int capacity = 64)
    {
        _buffer = capacity > 0 ? new ReplayFrameData[capacity] : Array.Empty<ReplayFrameData>();
    }

    public int Count { get; private set; }

    public int Capacity => _buffer.Length;

    public ReplayFrameData this[int index]
    {
        get
        {
            if ((uint) index >= (uint) Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var realIndex = (_head + index) % _buffer.Length;

            return _buffer[realIndex];
        }
    }

    public void Add(ReplayFrameData frame)
    {
        EnsureCapacity(Count + 1);

        var tail = (_head + Count) % _buffer.Length;
        _buffer[tail] = frame;
        Count++;
    }

    public void RemoveOldest(int removeCount)
    {
        if (removeCount <= 0 || Count == 0)
        {
            return;
        }

        if (removeCount >= Count)
        {
            Clear();
            return;
        }

        _head = (_head + removeCount) % _buffer.Length;
        Count -= removeCount;
    }

    public void Clear()
    {
        _head  = 0;
        Count  = 0;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _buffer.Length)
        {
            return;
        }

        var newSize = Math.Max(capacity, _buffer.Length == 0 ? 4 : _buffer.Length * 2);
        var newArr  = new ReplayFrameData[newSize];

        // copy existing frames in order
        for (var i = 0; i < Count; i++)
        {
            var realIndex = (_head + i) % _buffer.Length;
            newArr[i] = _buffer[realIndex];
        }

        _buffer = newArr;
        _head   = 0;
    }

    public IEnumerator<ReplayFrameData> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            var realIndex = (_head + i) % _buffer.Length;
            yield return _buffer[realIndex];
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}

