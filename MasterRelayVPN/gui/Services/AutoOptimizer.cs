using System;
using System.Threading;
using System.Threading.Tasks;
using MasterRelayVPN.Models;

namespace MasterRelayVPN.Services;

/// <summary>
/// Auto Optimize: samples download speed for ~10s after Start, then nudges
/// chunk_size/max_parallel up or down on the next save. Keeps things bounded
/// so a bad sample can't push values into pathological territory.
/// </summary>
public class AutoOptimizer
{
    readonly Func<StatsSnapshot> _getSnap;
    readonly Action<int, int> _apply;       // (chunkSize, parallel)
    CancellationTokenSource? _cts;

    public AutoOptimizer(Func<StatsSnapshot> getSnap, Action<int, int> apply)
    {
        _getSnap = getSnap;
        _apply = apply;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait 5s for the connection to warm up.
                await Task.Delay(5000, ct);

                var s1 = _getSnap();
                await Task.Delay(10000, ct);   // 10s sampling window
                var s2 = _getSnap();

                if (ct.IsCancellationRequested) return;

                long deltaDown = s2.BytesDown - s1.BytesDown;
                double mbps = deltaDown / 10.0 / (1024.0 * 1024.0);

                // Pick chunk_size and parallel based on observed throughput.
                int chunk;
                int parallel;
                if (mbps < 0.25)        { chunk =  64 * 1024; parallel = 2; }   // very slow link
                else if (mbps < 1.0)    { chunk =  96 * 1024; parallel = 3; }
                else if (mbps < 4.0)    { chunk = 128 * 1024; parallel = 4; }   // balanced default
                else if (mbps < 12.0)   { chunk = 192 * 1024; parallel = 6; }
                else                    { chunk = 256 * 1024; parallel = 8; }

                _apply(chunk, parallel);
            }
            catch (OperationCanceledException) { }
            catch { /* swallow — auto-tune is best-effort */ }
        }, ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }
}
