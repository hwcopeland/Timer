using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Timer.RequestManager.Scheduling;

/// <summary>
/// Score recalculation request.
/// </summary>
internal readonly record struct RecalcRequest(ulong MapId, int Style, ushort Track, int Tier, int BasePot, double StyleFactor);

/// <summary>
/// Debounced score recalculation scheduler.
/// Uses a Channel with a single consumer for background processing, 5-second debounce delay, deduplicating by (MapId, Style, Track).
/// </summary>
internal sealed class ScoreRecalcScheduler : IDisposable
{
    private readonly Channel<RecalcRequest> _channel = Channel.CreateBounded<RecalcRequest>(256);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(5);
    private readonly ILogger? _logger;

    public ScoreRecalcScheduler(Func<RecalcRequest, Task> recalcAction, ILogger? logger = null)
    {
        _logger = logger;
        _consumer = Task.Run(() => ConsumeLoop(recalcAction, _cts.Token));
    }

    /// <summary>
    /// Enqueue a recalculation request (fire-and-forget).
    /// </summary>
    public void Enqueue(RecalcRequest request)
    {
        _channel.Writer.TryWrite(request);
    }

    private async Task ConsumeLoop(Func<RecalcRequest, Task> recalcAction, CancellationToken ct)
    {
        var pending = new Dictionary<(ulong, int, ushort), RecalcRequest>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Block until the first message arrives
                if (!await _channel.Reader.WaitToReadAsync(ct))
                {
                    break;
                }

                // Drain all queued messages, deduplicating by key
                while (_channel.Reader.TryRead(out var req))
                {
                    pending[(req.MapId, req.Style, req.Track)] = req;
                }

                // Wait to allow subsequent requests to coalesce
                await Task.Delay(_debounceDelay, ct);

                // Drain again (new arrivals within the debounce window)
                while (_channel.Reader.TryRead(out var req))
                {
                    pending[(req.MapId, req.Style, req.Track)] = req;
                }

                // Execute each deduplicated recalculation
                foreach (var req in pending.Values)
                {
                    try
                    {
                        await recalcAction(req);
                    }
                    catch (Exception ex)
                    {
                        // Log and continue; don't block other tracks
                        _logger?.LogError(ex, "Error recalculating scores for MapId={MapId}, Style={Style}, Track={Track}",
                            req.MapId, req.Style, req.Track);
                    }
                }

                pending.Clear();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error in score recalc consumer loop");
            }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _consumer.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore timeout or cancellation exceptions during shutdown
        }
        _cts.Dispose();
    }
}
