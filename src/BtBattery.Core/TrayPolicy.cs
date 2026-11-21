namespace BtBattery.Core;

public static class TrayPolicy
{
    public static IReadOnlyList<TrackedDevice> SelectForTray(
        IEnumerable<TrackedDevice> devices,
        AppSettings settings)
    {
        return SelectForList(devices, settings)
            .Where(device => settings.ShowWhenBatteryUnknown || device.Battery.IsKnown)
            .ToList();
    }

    /// <summary>设备列表比托盘多显示「电量未知但用户选择不放图标」的配件。</summary>
    public static IReadOnlyList<TrackedDevice> SelectForList(
        IEnumerable<TrackedDevice> devices,
        AppSettings settings)
    {
        return devices
            .Where(device => device.IsConnected && settings.Allows(device.Kind))
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

public static class DeviceText
{
    public static string BatteryLine(TrackedDevice device)
    {
        if (!device.Battery.IsKnown)
            return "电量未知";

        var text = device.HasMultipleBatteries
            ? $"{device.Battery.Percent}%（多块电池，显示最低）"
            : $"{device.Battery.Percent}%";

        if (device.Battery.IsCharging == true)
            text += " · 充电中";

        return text;
    }

    public static string Badge(BatteryReading battery) =>
        battery.IsKnown ? battery.Percent!.Value.ToString() : "—";

    public static string Tooltip(TrackedDevice device) => Tooltip(device, 127);

    public static string Tooltip(TrackedDevice device, int maxLength)
    {
        if (maxLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        var details = $"\n{device.KindLabel}\n{BatteryLine(device)}";
        var nameLength = Math.Min(60, Math.Max(1, maxLength - details.Length));
        return Truncate(Truncate(device.Name, nameLength) + details, maxLength);
    }

    public static string AppTooltip(int shownDevices, bool demo)
    {
        if (demo)
            return $"{AppInfo.Name}\n演示模式\n示例图标，不是这台电脑上的设备";

        return shownDevices switch
        {
            0 => $"{AppInfo.Name}\n没有已连接的鼠标、键盘、耳机或手柄",
            _ => $"{AppInfo.Name}\n已连接 {shownDevices} 个设备",
        };
    }

    public static string SourceLabel(BatterySource source) => source switch
    {
        BatterySource.DeviceProperty => "系统蓝牙电量属性",
        BatterySource.WindowsPowerApi => "Windows 电池接口",
        BatterySource.GattBatteryService => "GATT 电池服务 0x180F",
        _ => "无",
    };

    private static string Truncate(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..(max - 1)] + "…";
    }
}
