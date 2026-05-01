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
        var path = Path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(cfg, WriteOpts);
        var tmp = path + ".tmp";
        try
        {
            if (File.Exists(path))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Copy(tmp, path, overwrite: true);
            else
                File.Move(tmp, path);
            try { File.Delete(tmp); } catch { }
        }
        catch
        {
            // Last resort plain write if atomic replace path fails on this filesystem.
            File.WriteAllText(path, json);
        }
    }
}
