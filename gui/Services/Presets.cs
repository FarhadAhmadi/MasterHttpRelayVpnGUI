using MasterRelayVPN.Models;

namespace MasterRelayVPN.Services;

public static class Presets
{
    public record Preset(string Key, int FragmentSize, int ChunkSize, int MaxParallel, bool EnableChunked);

    public static readonly Preset Stealth  = new("stealth",   8 * 1024,  96 * 1024, 1,  true);
    public static readonly Preset Balanced = new("balanced", 16 * 1024, 128 * 1024, 3,  true);
    public static readonly Preset Speed    = new("speed",    32 * 1024, 256 * 1024, 6,  true);
    public static readonly Preset God      = new("god",      12 * 1024, 224 * 1024, 5,  true);
    // 'Auto' is a runtime measurement, not a static preset — falls back to Balanced
    // numbers until the auto-optimizer collects samples.
    public static readonly Preset Auto     = new("auto",     16 * 1024, 128 * 1024, 3,  true);

    public static Preset ByKey(string key) => key switch
    {
        "stealth"  => Stealth,
        "speed"    => Speed,
        "god"      => God,
        "auto"     => Auto,
        _          => Balanced,
    };

    public static void ApplyTo(AppConfig cfg, Preset p)
    {
        cfg.FragmentSize  = p.FragmentSize;
        cfg.ChunkSize     = p.ChunkSize;
        cfg.MaxParallel   = p.MaxParallel;
        cfg.EnableChunked = p.EnableChunked;
        cfg.Preset        = p.Key;
    }
}
