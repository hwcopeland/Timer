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
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Replay;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Source2Surf.Timer.Modules.Replay;

internal static class ReplayShared
{
    public const char HeaderFrameSeparator = '\n';
    public static readonly byte[] HeaderFrameSeparatorBytes = [(byte)HeaderFrameSeparator];

    public static string BuildMainReplayPath(string replayDirectory, string mapName, int style, int track, long? runId)
    {
        var fileName = runId is null
            ? $"{mapName}_{track}.replay.{Guid.NewGuid()}"
            : $"{mapName}_{track}_{runId.Value}.replay";

        return Path.Combine(replayDirectory,
                            $"style_{style}",
                            fileName);
    }

    public static string BuildStageReplayPath(string replayDirectory, string mapName, int style, int track, int stage, long? runId)
    {
        var fileName = runId is null
            ? $"{mapName}_{track}_{stage}.replay.{Guid.NewGuid()}"
            : $"{mapName}_{track}_{stage}_{runId.Value}.replay";

        return Path.Combine(replayDirectory,
                            $"style_{style}",
                            "stage",
                            fileName);
    }

    /// <summary>
    /// Parse a main replay filename: {mapname}_{track}.replay -> track
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="mapName">Current map name</param>
    /// <param name="track">Parsed track number</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParseTrackFromFileName(string path, string mapName, out int track)
    {
        track = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        var prefix = mapName + "_";
        if (!name.StartsWith(prefix)) return false;

        return int.TryParse(name.AsSpan()[prefix.Length..], out track)
               && track is >= 0 and < TimerConstants.MAX_TRACK;
    }

    /// <summary>
    /// Parse a stage replay filename: {mapname}_{track}_{stage}.replay -> (track, stage)
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="mapName">Current map name</param>
    /// <param name="track">Parsed track number</param>
    /// <param name="stage">Parsed stage number</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParseTrackStageFromFileName(string path, string mapName, out int track, out int stage)
    {
        track = stage = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        var prefix = mapName + "_";
        if (!name.StartsWith(prefix)) return false;

        var suffix = name.AsSpan()[prefix.Length..];
        var idx = suffix.IndexOf('_');
        if (idx == -1) return false;

        return int.TryParse(suffix[..idx], out track)
               && track is >= 0 and < TimerConstants.MAX_TRACK
               && int.TryParse(suffix[(idx + 1)..], out stage)
               && stage is >= 1 and < TimerConstants.MAX_STAGE;
    }

    /// <summary>
    /// Serialize replay data in-memory (JSON header + \n separator + Zstd-compressed MemoryPack frame data) for remote upload.
    /// </summary>
    public static byte[] SerializeReplay(ReplayFileHeader header, IReadOnlyList<ReplayFrameData> frames)
    {
        // Serialize frames via MemoryPack
        byte[] serializedFrames;

        switch (frames)
        {
            case ReplayFrameData[] arr:
                serializedFrames = MemoryPackSerializer.Serialize(arr);
                break;
            case List<ReplayFrameData> list:
                serializedFrames = MemoryPackSerializer.Serialize(list);
                break;
            default:
                var rented = ArrayPool<ReplayFrameData>.Shared.Rent(frames.Count);
                try
                {
                    for (var i = 0; i < frames.Count; i++)
                        rented[i] = frames[i];

                    serializedFrames = MemoryPackSerializer.Serialize(rented.AsMemory(0, frames.Count));
                }
                finally
                {
                    ArrayPool<ReplayFrameData>.Shared.Return(rented);
                }
                break;
        }

        // Zstd compress
        using var compressor = new Compressor();
        var compressed = compressor.Wrap(serializedFrames);

        // Assemble: JSON header + \n separator + compressed frames
        using var ms = new MemoryStream(compressed.Length + 4096);
        JsonSerializer.Serialize(ms, header);
        ms.WriteByte((byte)HeaderFrameSeparator);
        ms.Write(compressed);

        return ms.ToArray();
    }

    /// <summary>
    /// Deserialize replay from raw bytes (JSON header + \n + Zstd-compressed MemoryPack frame data).
    /// Used for remote replay data deserialization.
    /// </summary>
    public static ReplayLoadResult? DeserializeReplay(ReadOnlySpan<byte> bytes, int style, int track, int stage, ILogger logger)
    {
        try
        {
            var split = bytes.IndexOf((byte)HeaderFrameSeparator);

            if (split == -1)
            {
                logger.LogError("Invalid replay data: missing header separator for style={style} track={track} stage={stage}", style, track, stage);
                return null;
            }

            var header = JsonSerializer.Deserialize<ReplayFileHeader>(bytes[..split]);
            if (header == null)
            {
                logger.LogError("Failed to deserialize replay header for style={style} track={track} stage={stage}", style, track, stage);
                return null;
            }

            using var decompressor = new Decompressor();
            var frames = DeserializeReplayFrames(bytes[(split + 1)..], decompressor);
            if (frames == null)
            {
                logger.LogError("Failed to deserialize replay frames for style={style} track={track} stage={stage}", style, track, stage);
                return null;
            }

            return new ReplayLoadResult(style, track, stage, new ReplayContent { Header = header, Frames = frames });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error deserializing replay for style={style} track={track} stage={stage}", style, track, stage);
            return null;
        }
    }

    /// <summary>
    /// Load a replay file from the given path, reusing a Decompressor instance.
    /// Corrupted files (missing HeaderFrameSeparator) are renamed with a .corrupt suffix as backup.
    /// </summary>
    public static ReplayLoadResult? LoadReplayFromPath(string path, int style, int track, int stage, Decompressor decompressor, ILogger logger)
    {
        try
        {
            var bytes = File.ReadAllBytes(path).AsSpan();
            var split = bytes.IndexOf((byte)HeaderFrameSeparator);

            if (split == -1)
            {
                logger.LogError("Invalid replay file at {path}, renaming to .corrupt", path);

                try
                {
                    File.Move(path, path + ".corrupt", true);
                }
                catch (Exception renameEx)
                {
                    logger.LogError(renameEx, "Failed to rename corrupt replay file: {path}", path);
                }

                return null;
            }

            var header = JsonSerializer.Deserialize<ReplayFileHeader>(bytes[..split]);
            if (header == null)
            {
                logger.LogError("Failed to deserialize header: {p}", path);
                return null;
            }

            var frames = DeserializeReplayFrames(bytes[(split + 1)..], decompressor);
            if (frames == null)
            {
                logger.LogError("Failed to deserialize frames: {p}", path);
                return null;
            }

            return new ReplayLoadResult(style, track, stage, new ReplayContent { Header = header, Frames = frames });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading replay: {p}", path);
            return null;
        }
    }

    /// <summary>
    /// Create a main replay snapshot from PlayerFrameData.
    /// Resets the frame buffer and timer state on the source frame data.
    /// </summary>
    public static ReplaySaveSnapshot CreateMainReplaySnapshot(PlayerFrameData frame)
    {
        var framesBuffer = frame.Frames;

        // Inherit the old buffer's Capacity to avoid repeated exponential resizing on long maps.
        // ReplayFrameBuffer doubles via EnsureCapacity when full (same as List<T>).
        // Initial capacity (Tickrate*60*5 ≈ 38400 at 128tick) only covers ~5 min;
        // long maps (20+ min) would re-climb the same doubling chain without this.
        var inheritedCapacity = Math.Max(framesBuffer.Capacity, TimerConstants.Tickrate * 60 * 5);
        frame.Frames = new ReplayFrameBuffer(inheritedCapacity);

        var header = new ReplayFileHeader
        {
            SteamId     = frame.SteamId,
            TotalFrames = framesBuffer.Count,
            PreFrame    = frame.TimerStartFrame,
            PostFrame   = frame.TimerFinishFrame,
            Time        = frame.FinishTime,
            StageTicks  = [..frame.NewStageTicks],
            PlayerName  = frame.Name,
        };

        frame.NewStageTicks.Clear();
        frame.StageTimerStartTicks.Clear();
        frame.TimerStartFrame  = 0;
        frame.TimerFinishFrame = 0;
        frame.FinishTime       = 0;

        return new ReplaySaveSnapshot(header, framesBuffer);
    }

    /// <summary>
    /// Create a stage replay snapshot from PlayerFrameData.
    /// Uses ReplayFrameSlice for zero-copy frame slicing.
    /// </summary>
    public static ReplaySaveSnapshot CreateStageReplaySnapshot(PlayerFrameData frame,
                                                               int             startTick,
                                                               int             stageStartFrame,
                                                               int             stageFinishFrame,
                                                               int             postRunFrameCount,
                                                               float           finishTime)
    {
        var finalFrame = Math.Min(frame.Frames.Count, stageFinishFrame + postRunFrameCount);
        var length     = Math.Max(0, finalFrame                        - startTick);

        IReadOnlyList<ReplayFrameData> framesToWrite = length == 0
            ? []
            : new ReplayFrameSlice(frame.Frames, startTick, length);

        var header = new ReplayFileHeader
        {
            SteamId     = frame.SteamId,
            TotalFrames = framesToWrite.Count,
            PreFrame    = stageStartFrame  - startTick,
            PostFrame   = stageFinishFrame - startTick,
            Time        = finishTime,
            PlayerName  = frame.Name,
        };

        return new ReplaySaveSnapshot(header, framesToWrite);
    }

    /// <summary>
    /// Trim pre-run frame data, keeping only the most recent maxPreFrame frames.
    /// </summary>
    public static void TrimPreRunFrames(PlayerFrameData frameData, int maxPreFrame)
    {
        if (maxPreFrame <= 0)
        {
            frameData.Frames.Clear();

            return;
        }

        var excess = frameData.Frames.Count - maxPreFrame;

        if (excess > 0)
        {
            frameData.Frames.RemoveOldest(excess);
        }
    }

    /// <summary>
    /// Ensure the replay directory structure exists (style and stage subdirectories).
    /// Creates style_0 through style_{MAX_STYLE-1} directories, each with a stage subdirectory.
    /// </summary>
    public static void EnsureReplayDirectories(string replayDirectory)
    {
        if (!Directory.Exists(replayDirectory))
        {
            Directory.CreateDirectory(replayDirectory);
        }

        // path/data/surftimer/replays/style_id/mapname_tracknum.replay
        // path/data/surftimer/replays/style_id/stage/mapname_tracknum_stagenum.replay
        for (var i = 0; i < TimerConstants.MAX_STYLE; i++)
        {
            var stylePath = Path.Combine(replayDirectory, $"style_{i}");

            if (!Directory.Exists(stylePath))
            {
                Directory.CreateDirectory(stylePath);
            }

            var stagePath = Path.Combine(stylePath, "stage");

            if (!Directory.Exists(stagePath))
            {
                Directory.CreateDirectory(stagePath);
            }
        }
    }

    /// <summary>
    /// Load ReplayBotConfig array from a JSON file.
    /// Generates a default config file if it doesn't exist.
    /// Falls back to defaults and logs a warning if deserialization fails.
    /// </summary>
    public static ReplayBotConfig[] LoadReplayBotConfigs(string configPath, ILogger logger)
    {
        var defaultConfigs = new ReplayBotConfig[] { new() };

        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfigs, Utils.SerializerOptions));

            logger.LogWarning("Failed to find replay config at {path}, generating the default one...", configPath);

            return defaultConfigs;
        }

        try
        {
            var configs
                = JsonSerializer.Deserialize<ReplayBotConfig[]>(File.ReadAllText(configPath),
                                                                Utils.DeserializerOptions);

            if (configs == null || configs.Length == 0)
            {
                logger.LogWarning("Failed to deserialize replay config, regenerate with default config");
                File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfigs, Utils.SerializerOptions));

                return defaultConfigs;
            }

            return configs;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to deserialize replay config, regenerate with default config");
            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfigs, Utils.SerializerOptions));

            return defaultConfigs;
        }
    }

    /// <summary>
    /// Asynchronously write a replay file (JSON header + \n separator + MemoryPack frame data).
    /// If compressionLevel &lt;= 0, writes uncompressed frame data.
    /// If compressionWorkers &lt;= 0, uses single-threaded compression.
    /// </summary>
    public static async Task<bool> WriteReplayToFileAsync(
        ReplayFileHeader header,
        string path,
        IReadOnlyList<ReplayFrameData> framesToWrite,
        int compressionLevel,
        int compressionWorkers,
        ILogger logger)
    {
        try
        {
            await using var fileStream
                = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);

            var headerBuffer = ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                using var       memoryStream = new MemoryStream(headerBuffer);
                await using var jsonWriter   = new Utf8JsonWriter(memoryStream);

                JsonSerializer.Serialize(jsonWriter, header);

                await jsonWriter.FlushAsync();

                await fileStream.WriteAsync(new ReadOnlyMemory<byte>(headerBuffer, 0, (int) memoryStream.Position));
                await fileStream.WriteAsync(HeaderFrameSeparatorBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }

            if (compressionLevel <= 0)
            {
                await SerializeFramesToStreamAsync(fileStream, framesToWrite);
            }
            else
            {
                await using var compressionStream = new CompressionStream(fileStream, compressionLevel);

                compressionStream.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers,
                                               Math.Max(compressionWorkers, 0));

                await SerializeFramesToStreamAsync(compressionStream, framesToWrite);
            }
        }
        catch (Exception e)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            logger.LogError(e, "Error when trying to write replay file to {p}", path);

            return false;
        }

        return true;
    }

    private static async Task SerializeFramesToStreamAsync(Stream stream, IReadOnlyList<ReplayFrameData> framesToWrite)
    {
        switch (framesToWrite)
        {
            case ReplayFrameData[] arr:
                await MemoryPackSerializer.SerializeAsync(stream, arr);

                break;
            case List<ReplayFrameData> list:
                await MemoryPackSerializer.SerializeAsync(stream, list);

                break;
            default:
                var rented = ArrayPool<ReplayFrameData>.Shared.Rent(framesToWrite.Count);

                try
                {
                    for (var i = 0; i < framesToWrite.Count; i++)
                    {
                        rented[i] = framesToWrite[i];
                    }

                    await MemoryPackSerializer.SerializeAsync(stream, rented.AsMemory(0, framesToWrite.Count));
                }
                finally
                {
                    ArrayPool<ReplayFrameData>.Shared.Return(rented);
                }

                break;
        }
    }

    private static ReplayFrameData[]? DeserializeReplayFrames(ReadOnlySpan<byte> payload, Decompressor decompressor)
    {
        try
        {
            var decompressed = decompressor.Unwrap(payload);
            var frames = MemoryPackSerializer.Deserialize<ReplayFrameData[]>(decompressed);

            if (frames is not null)
            {
                return frames;
            }
        }
        catch
        {
        }

        return MemoryPackSerializer.Deserialize<ReplayFrameData[]>(payload);
    }

}
