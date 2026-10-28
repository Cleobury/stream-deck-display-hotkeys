using System.Text.Json;

namespace DisplayHotkeys;

public class DisplayProfile
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<TargetSettings> Targets { get; set; } = new();
    public Dictionary<string, string> Adapters { get; set; } = new();
    public string Paths { get; set; } = "";
    public string Modes { get; set; } = "";

    public string Describe() => string.Join(", ", Targets.Select(t => t.Describe()));
}

public class TargetSettings
{
    public string Monitor { get; set; } = "";
    public string MonitorDevicePath { get; set; } = "";
    public string Adapter { get; set; } = "";
    public uint TargetId { get; set; }
    public uint SourceId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public double RefreshHz { get; set; }
    public int Rotation { get; set; }
    public bool? Hdr { get; set; }
    public int? DpiScaleRel { get; set; }
    public int? ScalePercent { get; set; }

    public string Describe() =>
        $"{Monitor} {Width}x{Height} @{RefreshHz:0.##}Hz" +
        (ScalePercent is int s ? $" {s}%" : "") +
        (Hdr == true ? " HDR" : "") +
        (Rotation != 0 ? $" {Rotation}°" : "") +
        (PositionX == 0 && PositionY == 0 ? " (main)" : "");
}

public class AppConfig
{
    /// <summary>Profile name → hotkey, e.g. "TV" → "Ctrl+Alt+2".</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    /// <summary>Optional hotkey that steps through every profile in turn.</summary>
    public string? CycleHotkey { get; set; }
    public string? LastApplied { get; set; }
}

static class ProfileStore
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DisplayHotkeys");
    static string ProfileDir => Path.Combine(Root, "profiles");
    static string ConfigFile => Path.Combine(Root, "config.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Last successful read of each profile file, used if a later read fails (e.g. file briefly locked).
    static readonly Dictionary<string, DisplayProfile> lastGood = new(StringComparer.OrdinalIgnoreCase);

    public static List<DisplayProfile> LoadAll()
    {
        if (!Directory.Exists(ProfileDir)) return new();
        var profiles = new List<DisplayProfile>();
        lock (lastGood)
        {
            foreach (var file in Directory.GetFiles(ProfileDir, "*.json"))
            {
                var p = Read<DisplayProfile>(file);
                if (p != null) lastGood[file] = p;
                else if (!lastGood.TryGetValue(file, out p)) continue;
                profiles.Add(p);
            }
        }
        return profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static DisplayProfile? Load(string name) =>
        LoadAll().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Save(DisplayProfile profile)
    {
        Directory.CreateDirectory(ProfileDir);
        File.WriteAllText(FileFor(profile.Name), JsonSerializer.Serialize(profile, Json));
    }

    public static void Delete(string name)
    {
        var file = FileFor(name);
        if (File.Exists(file)) File.Delete(file);
    }

    public static AppConfig LoadConfig() => Read<AppConfig>(ConfigFile) ?? new AppConfig();

    public static void SaveConfig(AppConfig config)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, Json));
    }

    static string FileFor(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(ProfileDir, name + ".json");
    }

    /// <summary>The most recent problem reading a profile or config file, for the plugin log.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Where profiles are stored and what's there, for the plugin log.</summary>
    public static string Describe()
    {
        if (!Directory.Exists(ProfileDir)) return $"{ProfileDir} does not exist";
        var files = Directory.GetFiles(ProfileDir, "*.json").Select(Path.GetFileName);
        return $"{ProfileDir}: [{string.Join(", ", files)}]" + (LastError != null ? $" last error: {LastError}" : "");
    }

    static T? Read<T>(string file) where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(200); // another program (antivirus, sync) may have it open for a moment
            }
            catch (Exception ex)
            {
                LastError = $"{Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }
    }
}
