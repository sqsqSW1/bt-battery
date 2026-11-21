using BtBattery.Core;

namespace BtBattery.Core.Tests;

public class SettingsAndTrayTests
{
    [Fact]
    public void Unknown_battery_can_stay_on_the_list_while_leaving_the_tray()
    {
        var settings = new AppSettings { ShowWhenBatteryUnknown = false };
        var devices = new[]
        {
            Device("鼠标甲", DeviceKind.Mouse, 80),
            Device("键盘乙", DeviceKind.Keyboard, null),
            Device("手机", DeviceKind.Excluded, 90),
        };

        var tray = TrayPolicy.SelectForTray(devices, settings);
        var list = TrayPolicy.SelectForList(devices, settings);

        Assert.Equal("鼠标甲", Assert.Single(tray).Name);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, item => item.Name == "键盘乙");
    }

    [Fact]
    public void Combo_shows_when_either_mouse_or_keyboard_is_enabled()
    {
        var settings = new AppSettings { ShowMouse = false, ShowKeyboard = true };
        Assert.True(settings.Allows(DeviceKind.Combo));
        settings.ShowKeyboard = false;
        Assert.False(settings.Allows(DeviceKind.Combo));
    }

    [Fact]
    public void Tooltip_uses_the_unknown_wording()
    {
        var device = Device("Keychron K3", DeviceKind.Keyboard, null);
        Assert.Equal("电量未知", DeviceText.BatteryLine(device));
        Assert.Equal("—", DeviceText.Badge(device.Battery));
        Assert.Contains("Keychron K3", DeviceText.Tooltip(device));
        Assert.Contains("键盘", DeviceText.Tooltip(device));
        Assert.Contains("电量未知", DeviceText.Tooltip(device));
    }

    [Fact]
    public void Compact_tooltip_keeps_the_battery_reading_for_a_long_device_name()
    {
        var device = Device(new string('蓝', 100), DeviceKind.Headset, 42);
        var text = DeviceText.Tooltip(device, 63);

        Assert.Equal(63, text.Length);
        Assert.EndsWith("…\n" + device.KindLabel + "\n42%", text);
    }

    [Fact]
    public void Settings_roundtrip_and_reject_a_broken_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "btbattery-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new AppSettings
            {
                StartWithWindows = true,
                ShowHeadset = false,
                PollIntervalSeconds = 5,
                IconTheme = IconThemePreference.DarkGlyphs,
                ShowWhenBatteryUnknown = false,
            };
            SettingsFile.Save(path, settings);

            var loaded = SettingsFile.Load(path);
            Assert.True(loaded.StartWithWindows);
            Assert.False(loaded.ShowHeadset);
            Assert.Equal(15, loaded.PollIntervalSeconds);
            Assert.Equal(IconThemePreference.DarkGlyphs, loaded.IconTheme);
            Assert.False(loaded.ShowWhenBatteryUnknown);

            loaded.PollIntervalSeconds = 120;
            SettingsFile.Save(path, loaded);
            Assert.Equal(120, SettingsFile.Load(path).PollIntervalSeconds);

            File.WriteAllText(path, "{ this is not json");
            var fallback = SettingsFile.Load(path);
            Assert.True(fallback.ShowMouse);
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Generic_service_names_are_not_used_as_product_names()
    {
        Assert.True(DeviceNames.IsGeneric("Hands-Free AG"));
        Assert.True(DeviceNames.IsGeneric("AVRCP Transport"));
        Assert.False(DeviceNames.IsGeneric("Xbox Wireless Headset"));
        Assert.Equal(
            "蓝牙耳机",
            DeviceNames.Choose(DeviceKind.Headset, new (string?, int)[] { ("Headset", 10) }));
    }

    private static TrackedDevice Device(string name, DeviceKind kind, int? percent) => new()
    {
        Address = "AA:BB:CC:DD:EE:10",
        Name = name,
        Kind = kind,
        KindLabel = DeviceKindLabels.For(kind),
        ClassificationSource = "测试",
        IsConnected = true,
        Battery = percent is int value ? BatteryReading.Known(value, BatterySource.DeviceProperty) : BatteryReading.Unknown,
    };
}
