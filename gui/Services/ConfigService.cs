using System.IO;
using System.Text.Json;
using MasterRelayVPN.Models;

namespace MasterRelayVPN.Services;

public class ConfigService
{
    static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path => Paths.ConfigFile;

    public AppConfig Load()
    {
        if (!File.Exists(Path)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path)) ?? new AppConfig();
        }
        catch
        {
            // Don't crash on a corrupt config; reset to defaults.
            return new AppConfig();
        }
    }

    public void Save(AppConfig cfg)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(Path, JsonSerializer.Serialize(cfg, WriteOpts));
    }
}
