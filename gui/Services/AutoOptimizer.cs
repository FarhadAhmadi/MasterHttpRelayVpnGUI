using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using MasterRelayVPN.Models;

namespace MasterRelayVPN.Services;

public sealed record OptimizerChoice(int FragmentSize, int ChunkSize, int MaxParallel);

public sealed class AutoOptimizer
{
    readonly Func<StatsSnapshot> _getSnap;
    readonly Action<OptimizerChoice> _apply;
    readonly Dictionary<string, OptimizerChoice> _bestByNetwork = new();
    CancellationTokenSource? _cts;

    static readonly OptimizerChoice[] Candidates =
    {
        new(8 * 1024, 32 * 1024, 1),
        new(16 * 1024, 96 * 1024, 3),
        new(16 * 1024, 128 * 1024, 4),
        new(32 * 1024, 192 * 1024, 6),
        new(32 * 1024, 256 * 1024, 8),
    };

    public AutoOptimizer(Func<StatsSnapshot> getSnap, Action<OptimizerChoice> apply)
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
                var networkKey = CurrentNetworkKey();
                if (_bestByNetwork.TryGetValue(networkKey, out var cached))
                    _apply(cached);

                await Task.Delay(TimeSpan.FromSeconds(4), ct);

                var results = new List<(OptimizerChoice Choice, double Score)>();
                foreach (var candidate in Candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    _apply(candidate);
                    await Task.Delay(TimeSpan.FromSeconds(12), ct);

                    var s = _getSnap();
                    var score =
                        (s.SpeedDown / 1024.0 / 1024.0 * 35.0) +
                        (Math.Clamp(s.SuccessRate, 0, 1) * 50.0) -
                        (Math.Min(s.LatencyMs, 3000) / 100.0);
                    results.Add((candidate, score));
                }

                var best = results
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => r.Choice.MaxParallel)
                    .First().Choice;
                _bestByNetwork[networkKey] = best;
                _apply(best);
            }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    static string CurrentNetworkKey()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();
            return active == null ? "default" : $"{active.NetworkInterfaceType}:{active.Name}";
        }
        catch { return "default"; }
    }
}
