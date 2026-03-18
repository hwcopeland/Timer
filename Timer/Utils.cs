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
using System.Text.Json;
using Cysharp.Text;
using Source2Surf.Timer.Shared;

namespace Source2Surf.Timer;

internal static class Utils
{
    public static readonly JsonSerializerOptions SerializerOptions = new () { WriteIndented = true, IndentSize = 4 };

    public static readonly JsonSerializerOptions DeserializerOptions = new ()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string FormatTime(float totalSeconds, bool precise = false)
    {
        var negative = totalSeconds < 0;
        var total    = Math.Abs(totalSeconds);

        var totalSecondsInt = (int) total;
        var hours           = totalSecondsInt / 3600;
        var minutes         = (totalSecondsInt / 60) % 60;
        var seconds         = totalSecondsInt % 60;

        var fractional = total - totalSecondsInt;
        var ms         = precise ? (int) (fractional * 1000) : (int) (fractional * 10);

        Span<char> buf = stackalloc char[16];
        var        pos = 0;

        if (negative) buf[pos++] = '-';

        if (hours > 0)
        {
            if (hours >= 100) { buf[pos++] = (char) ('0' + (hours / 100)); }
            if (hours >= 10)  { buf[pos++] = (char) ('0' + ((hours / 10) % 10)); }
            buf[pos++] = (char) ('0' + (hours % 10));
            buf[pos++] = ':';
        }

        buf[pos++] = (char) ('0' + (minutes / 10));
        buf[pos++] = (char) ('0' + (minutes % 10));
        buf[pos++] = ':';
        buf[pos++] = (char) ('0' + (seconds / 10));
        buf[pos++] = (char) ('0' + (seconds % 10));
        buf[pos++] = '.';

        if (precise)
        {
            buf[pos++] = (char) ('0' + (ms / 100));
            buf[pos++] = (char) ('0' + ((ms / 10) % 10));
            buf[pos++] = (char) ('0' + (ms % 10));
        }
        else
        {
            buf[pos++] = (char) ('0' + ms);
        }

        return new string(buf[..pos]);
    }

    public static void FormatTime(ref Utf16ValueStringBuilder sb, float totalSeconds, bool precise = false)
    {
        var negative = totalSeconds < 0;
        var total    = Math.Abs(totalSeconds);

        var totalSecondsInt = (int) total;
        var hours           = totalSecondsInt / 3600;
        var minutes         = (totalSecondsInt / 60) % 60;
        var seconds         = totalSecondsInt % 60;

        var fractional = total - totalSecondsInt;
        var ms         = precise ? (int) (fractional * 1000) : (int) (fractional * 10);

        if (negative) sb.Append('-');

        if (hours > 0)
        {
            sb.Append(hours);
            sb.Append(':');
        }

        AppendPadded2(ref sb, minutes);

        sb.Append(':');
        AppendPadded2(ref sb, seconds);
        sb.Append('.');

        if (precise)
            AppendPadded3(ref sb, ms);
        else
            sb.Append((char) ('0' + ms));
    }

    private static void AppendPadded2(ref Utf16ValueStringBuilder sb, int value)
    {
        sb.Append((char) ('0' + (value / 10)));
        sb.Append((char) ('0' + (value % 10)));
    }

    private static void AppendPadded3(ref Utf16ValueStringBuilder sb, int value)
    {
        sb.Append((char) ('0' + (value / 100)));
        sb.Append((char) ('0' + ((value / 10) % 10)));
        sb.Append((char) ('0' + (value % 10)));
    }

    public static string GetTrackName(int track, bool ignoreNumber = false)
    {
        return track switch
        {
            < 0 or >= TimerConstants.MAX_TRACK =>
                throw new IndexOutOfRangeException($"Track out of range. [0, {TimerConstants.MAX_TRACK})"),
            0                     => "Main",
            > 0 when ignoreNumber => "Bonus",
            > 0                   => $"Bonus {track}",
        };
    }
}
