using Microsoft.Win32;

namespace BtBattery.App;

internal static class TaskbarTheme
{
    public static bool UseLightGlyphs(IconThemePreference preference) => preference switch
    {
        IconThemePreference.LightGlyphs => true,
        IconThemePreference.DarkGlyphs => false,
        _ => !IsLightTaskbar(),
    };

    public static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    /// <summary>窗口跟随应用主题。读不到键时按 Windows 11 的默认浅色应用主题。</summary>
    public static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "蓝牙电量";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的启动项。");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule;
        var path = module?.FileName;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("无法确定程序自己的路径，开机启动没有写入。");

        key.SetValue(ValueName, $"\"{path}\"");
    }
}
