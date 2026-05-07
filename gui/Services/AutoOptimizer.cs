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
    int _currentCandidateIndex = 2;
    CancellationTokenSource? _cts;

    static readonly OptimizerChoice[] Candidates =
    {
        new(8 * 1024, 32 * 1024, 1),
        new(16 * 1024, 96 * 1024, 2),
        new(16 * 1024, 128 * 1024, 3),
        new(12 * 1024, 224 * 1024, 5),
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
                {
                    _apply(cached);
                    _currentCandidateIndex = FindCandidateIndex(cached);
                }

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
                _currentCandidateIndex = FindCandidateIndex(best);

                // Continuous adaptation loop (runtime optimizer).
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(18), ct);
                    var s = _getSnap();
                    var next = SelectAdaptiveCandidate(s);
                    var current = Candidates[_currentCandidateIndex];
                    if (!SameChoice(current, next))
                    {
                        _currentCandidateIndex = FindCandidateIndex(next);
                        _apply(next);
                        _bestByNetwork[networkKey] = next;
                    }
                }
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

    OptimizerChoice SelectAdaptiveCandidate(StatsSnapshot s)
    {
        var idx = _currentCandidateIndex;
        var success = Math.Clamp(s.SuccessRate, 0, 1);
        var windowSuccess = Math.Clamp(s.WindowSuccessRate, 0, 1);
        var effectiveSuccess = Math.Min(success, windowSuccess);
        var latency = s.LatencyMs;
        var rps = s.RequestsPerSec;

        // Reliability guard: quickly step down aggressiveness if quality drops.
        if (effectiveSuccess < 0.90 || (s.WindowRequests >= 12 && s.WindowErrors >= 4))
            idx = Math.Max(0, idx - 1);
        if (effectiveSuccess < 0.82 || (latency > 2800 && s.WindowRequests >= 8))
            idx = Math.Max(0, idx - 1);

        // Performance ramp-up only when quality is very strong.
        if (effectiveSuccess >= 0.985 && latency > 0 && latency < 1300 && rps >= 0.4)
            idx = Math.Min(Candidates.Length - 1, idx + 1);

        return Candidates[idx];
    }

    static bool SameChoice(OptimizerChoice a, OptimizerChoice b)
        => a.FragmentSize == b.FragmentSize
        && a.ChunkSize == b.ChunkSize
        && a.MaxParallel == b.MaxParallel;

    static int FindCandidateIndex(OptimizerChoice choice)
    {
        for (var i = 0; i < Candidates.Length; i++)
        {
            if (SameChoice(Candidates[i], choice)) return i;
        }
        return 2;
    }
}
