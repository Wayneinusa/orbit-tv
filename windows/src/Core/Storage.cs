using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperiorIptv;

/// <summary>Where the app keeps its data, and small JSON helpers.</summary>
public static class Storage
{
    public const string AppName = "Superior IPTV Player";

    private static string dataFolder;

    /// <summary>Settings, playlists and caches. Tests and the self-test point this somewhere else.</summary>
    public static string DataFolder
    {
        get
        {
            if (dataFolder == null)
            {
                dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuperiorIPTVPlayer");
            }
            Directory.CreateDirectory(dataFolder);
            return dataFolder;
        }
        set => dataFolder = value;
    }

    public static string CacheFolder
    {
        get
        {
            string folder = Path.Combine(DataFolder, "cache");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string DefaultMediaFolder(string kind)
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(videos, AppName, kind);
    }

    public static string File(string name) => Path.Combine(DataFolder, name);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        IncludeFields = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Load<T>(string path) where T : class
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            using FileStream stream = System.IO.File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly object WriteLock = new();

    /// <summary>Writes to a temporary file first so a crash never leaves a half-written file behind.</summary>
    public static void Save<T>(string path, T value)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                using (FileStream stream = System.IO.File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, value, Options);
                }
                System.IO.File.Move(temp, path, true);
            }
        }
        catch (Exception)
        {
            // Saving is best effort; the app keeps working from memory.
        }
    }

    /// <summary>Turns a title into something safe to use as a Windows file name.</summary>
    public static string SafeFileName(string text, int maxLength = 90)
    {
        var builder = new System.Text.StringBuilder();
        foreach (char c in text ?? "")
        {
            if (c < 32 || "<>:\"/\\|?*".Contains(c)) builder.Append(' ');
            else builder.Append(c);
        }
        string cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength].Trim('.', ' ');
        return cleaned.Length == 0 ? "video" : cleaned;
    }

    /// <summary>Returns a path in <paramref name="folder"/> that does not exist yet (adds " (2)", " (3)"…).</summary>
    public static string UniquePath(string folder, string baseName, string extension, Func<string, bool> taken = null)
    {
        Directory.CreateDirectory(folder);
        string candidate = Path.Combine(folder, baseName + extension);
        int suffix = 2;
        while (System.IO.File.Exists(candidate) || System.IO.File.Exists(candidate + ".part") || (taken != null && taken(candidate)))
        {
            candidate = Path.Combine(folder, $"{baseName} ({suffix}){extension}");
            suffix++;
        }
        return candidate;
    }

    public static long FreeSpace(string folder)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return -1;
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return -1;   // network share: unknown
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}

/// <summary>User preferences, saved as settings.json.</summary>
public sealed class Settings
{
    public int BufferMs { get; set; } = 1500;
    public string LiveFormat { get; set; } = "ts";
    public bool AutoReconnect { get; set; } = true;
    public string UserAgent { get; set; } = "";
    public int EpgPastDays { get; set; } = 1;
    public int EpgFutureDays { get; set; } = 3;
    public int EpgRefreshHours { get; set; } = 12;
    public int PlaylistRefreshHours { get; set; } = 24;
    public bool AutoConvertMp4 { get; set; } = true;
    public bool KeepTs { get; set; } = false;
    public int PadBeforeMinutes { get; set; } = 1;
    public int PadAfterMinutes { get; set; } = 5;
    public string RecordingsFolder { get; set; } = "";
    public string DownloadsFolder { get; set; } = "";
    public int MaxDownloads { get; set; } = 2;
    public bool HardwareDecoding { get; set; } = true;
    public List<string> HiddenGroups { get; set; } = new();
    public int Volume { get; set; } = 100;

    [JsonIgnore]
    public string EffectiveRecordingsFolder => string.IsNullOrWhiteSpace(RecordingsFolder) ? Storage.DefaultMediaFolder("Recordings") : RecordingsFolder;

    [JsonIgnore]
    public string EffectiveDownloadsFolder => string.IsNullOrWhiteSpace(DownloadsFolder) ? Storage.DefaultMediaFolder("Downloads") : DownloadsFolder;

    [JsonIgnore]
    public string EffectiveUserAgent => string.IsNullOrWhiteSpace(UserAgent) ? Net.DefaultUserAgent : UserAgent;

    public static Settings Load() => Storage.Load<Settings>(Storage.File("settings.json")) ?? new Settings();

    public void Save() => Storage.Save(Storage.File("settings.json"), this);
}

/// <summary>Remembers where the viewer stopped in movies, episodes, recordings and downloads.</summary>
public sealed class ResumeStore
{
    private readonly Dictionary<string, long> positions;
    private readonly object gate = new();

    public ResumeStore()
    {
        positions = Storage.Load<Dictionary<string, long>>(Storage.File("resume.json")) ?? new Dictionary<string, long>();
    }

    public long Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        lock (gate) return positions.GetValueOrDefault(key, 0);
    }

    public void Set(string key, long milliseconds)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (gate)
        {
            if (milliseconds <= 0) positions.Remove(key);
            else positions[key] = milliseconds;
            Storage.Save(Storage.File("resume.json"), positions);
        }
    }
}
