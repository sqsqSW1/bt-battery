using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtBattery.Core;

public enum IconThemePreference
{
    FollowSystem = 0,
    LightGlyphs = 1,
    DarkGlyphs = 2,
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public bool StartWithWindows { get; set; }

    public bool ShowMouse { get; set; } = true;

    public bool ShowKeyboard { get; set; } = true;

    public bool ShowHeadset { get; set; } = true;

    public bool ShowGamepad { get; set; } = true;

    /// <summary>读不到电量时仍在托盘放图标，电池外形留空。</summary>
    public bool ShowWhenBatteryUnknown { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 30;

    public IconThemePreference IconTheme { get; set; } = IconThemePreference.FollowSystem;

    public bool Allows(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => ShowMouse,
        DeviceKind.Keyboard => ShowKeyboard,
        DeviceKind.Combo => ShowMouse || ShowKeyboard,
        DeviceKind.Headset => ShowHeadset,
        DeviceKind.Gamepad => ShowGamepad,
        _ => false,
    };

    public static int NormalizePollInterval(int seconds) => Math.Min(300, Math.Max(15, seconds));

    public AppSettings Normalized()
    {
        PollIntervalSeconds = NormalizePollInterval(PollIntervalSeconds);
        if (!Enum.IsDefined(typeof(IconThemePreference), IconTheme))
            IconTheme = IconThemePreference.FollowSystem;
        Version = 1;
        return this;
    }
}

public static class SettingsFile
{
    private static readonly object ReplaceLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings().Normalized();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return settings.Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            TryBackup(path);
            return new AppSettings().Normalized();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        settings.Normalized();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            lock (ReplaceLock)
            {
#if NETFRAMEWORK
                WindowsFile.MoveReplacing(temporary, path);
#else
                File.Move(temporary, path, overwrite: true);
#endif
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (IOException)
        {
            // 设置坏了也不该挡住托盘。
        }
    }
}
