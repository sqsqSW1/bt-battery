using System.Windows.Threading;
using Microsoft.Win32;

namespace BtBattery.App;

internal sealed class TrayApplication : IAppController
{
    private readonly bool _demo;
    private readonly SynchronizationContext _ui;
    private readonly SingleInstance _single;
    private readonly ShellHookWindow _hook = new();
    private TrayIconManager? _icons;
    private DeviceMonitor? _monitor;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private DateTime _flyoutClosedUtc = DateTime.MinValue;
    private bool _ready;
    private bool _exiting;

    public TrayApplication(string[] args, SynchronizationContext ui)
    {
        _demo = args.Any(arg => string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase));
        _ui = ui;
        _single = new SingleInstance(ui);
        Settings = SettingsFile.Load(AppPaths.SettingsPath);
        Current = MonitorSnapshot.Empty;
    }

    public AppSettings Settings { get; private set; }

    public MonitorSnapshot Current { get; private set; }

    public bool IsDemo => _demo;

    public event Action? SnapshotChanged;

    public bool Start()
    {
        if (Environment.OSVersion.Version < new Version(10, 0, 22000))
        {
            System.Windows.MessageBox.Show("蓝牙电量需要 Windows 11。", AppInfo.Name);
            return false;
        }

        if (!_single.IsFirst)
        {
            _single.Signal();
            return false;
        }

        try
        {
            if (Settings.StartWithWindows)
                Autostart.Apply(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("同步开机启动项失败。", ex);
        }

        _icons = new TrayIconManager(ShowFlyout, ShowSettings, Refresh, Exit);
        ApplyIcons();
        _hook.DevicesChanged += (_, _) => _monitor?.Poke();
        _hook.DpiChanged += (_, _) => _ui.Post(_ => ApplyIcons(), null);
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        _ready = true;
        _single.Listen(ShowFlyout);

        if (_demo)
        {
            Accept(DemoSnapshot());
        }
        else
        {
            _monitor = new DeviceMonitor(_ui, () => Settings);
            _monitor.Updated += (_, snapshot) => Accept(snapshot);
            _monitor.Start();
        }

        AppLog.Info(_demo ? "演示模式已启动。" : "已启动。");
        return true;
    }

    public void UpdateSettings(AppSettings settings)
    {
        settings.Normalized();
        Settings = settings;
        try
        {
            SettingsFile.Save(AppPaths.SettingsPath, settings);
            Autostart.Apply(settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            AppLog.Error("保存设置失败。", ex);
            System.Windows.MessageBox.Show("设置没有保存：" + ex.Message, AppInfo.Name);
        }

        ApplyIcons();
        _monitor?.Poke();
        SnapshotChanged?.Invoke();
    }

    public void Refresh()
    {
        if (_demo)
            return;
        _monitor?.Poke();
    }

    public void ShowFlyout()
    {
        if (!_ready || _exiting)
            return;
        if (_flyout is { IsVisible: true })
        {
            _flyout.Close();
            return;
        }

        if (DateTime.UtcNow - _flyoutClosedUtc < TimeSpan.FromMilliseconds(250))
            return;

        _flyout = new FlyoutWindow(this);
        _flyout.Closed += (_, _) =>
        {
            _flyoutClosedUtc = DateTime.UtcNow;
            _flyout = null;
        };
        _flyout.Show();
        _flyout.Activate();
    }

    public void ShowSettings()
    {
        if (_exiting)
            return;
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _monitor?.Dispose();
        _icons?.Dispose();
        _hook.Dispose();
        _single.Dispose();
        _flyout?.Close();
        _settingsWindow?.Close();
        System.Windows.Application.Current?.Shutdown();
    }

    private void Accept(MonitorSnapshot snapshot)
    {
        Current = snapshot;
        ApplyIcons();
        SnapshotChanged?.Invoke();
    }

    private void ApplyIcons()
    {
        if (_icons is null)
            return;

        var shown = TrayPolicy.SelectForTray(Current.Devices, Settings);
        var waiting = ReferenceEquals(Current, MonitorSnapshot.Empty) && !_demo;
        var tooltip = waiting
            ? AppInfo.Name + "\n正在读取已连接的蓝牙设备…"
            : DeviceText.AppTooltip(shown.Count, _demo);
        _icons.Apply(
            shown,
            tooltip,
            TaskbarTheme.UseLightGlyphs(Settings.IconTheme),
            IconFactory.TrayPixels(_hook.Dpi));
    }

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        _ui.Post(_ => ApplyIcons(), null);

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) =>
        _ui.Post(_ => Exit(), null);

    private static MonitorSnapshot DemoSnapshot()
    {
        return new MonitorSnapshot(new[]
        {
            Sample("AA:BB:CC:DD:EE:01", "MX Master 3S", DeviceKind.Mouse, "鼠标", 85),
            Sample("AA:BB:CC:DD:EE:02", "Keychron K3", DeviceKind.Keyboard, "键盘", null),
            Sample("AA:BB:CC:DD:EE:03", "WH-1000XM5", DeviceKind.Headset, "耳机", 42),
            Sample("AA:BB:CC:DD:EE:04", "DualSense", DeviceKind.Gamepad, "手柄", 12),
        }, "演示模式：没有读取这台电脑的蓝牙设备。");
    }

    private static TrackedDevice Sample(string address, string name, DeviceKind kind, string label, int? percent)
    {
        var battery = percent is int level ? BatteryReading.Known(level, BatterySource.DeviceProperty) : BatteryReading.Unknown;
        return new TrackedDevice
        {
            Address = address,
            Name = name,
            Kind = kind,
            KindLabel = label,
            ClassificationSource = "演示数据",
            IsConnected = true,
            Battery = battery,
            KnownPercents = battery.Percent is int shown ? new[] { shown } : Array.Empty<int>(),
            Notes = new[] { "演示数据，不是这台电脑上的设备。" },
        };
    }
}
