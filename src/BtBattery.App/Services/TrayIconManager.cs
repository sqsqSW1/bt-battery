using System.Drawing;
using System.Windows.Forms;

namespace BtBattery.App;

/// <summary>
/// 始终留一个程序图标，否则全部设备断开后就没法打开设置或退出。
/// 每个正在显示的配件再各有一个图标。右键菜单各自持有，避免释放一个图标时把共享菜单关掉。
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
#if NETFRAMEWORK
    private const int MaxTooltipLength = 63;
#else
    private const int MaxTooltipLength = 127;
#endif
    private readonly NotifyIcon _appIcon;
    private readonly Dictionary<string, NotifyIcon> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _showFlyout;
    private readonly Action _showSettings;
    private readonly Action _refresh;
    private readonly Action _exit;
    private Icon? _appImage;

    public TrayIconManager(Action showFlyout, Action showSettings, Action refresh, Action exit)
    {
        _showFlyout = showFlyout;
        _showSettings = showSettings;
        _refresh = refresh;
        _exit = exit;
        _appIcon = CreateIcon();
    }

    public void Apply(IReadOnlyList<TrackedDevice> devices, string appTooltip, bool lightGlyph, int pixels)
    {
        ReplaceImage(_appIcon, ref _appImage, IconFactory.CreateAppIcon(lightGlyph, pixels));
        _appIcon.Text = Limit(appTooltip);
        _appIcon.Visible = true;

        var live = new HashSet<string>(devices.Select(device => device.Address), StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _devices.Keys.Where(id => !live.Contains(id)).ToList())
            Remove(stale);

        foreach (var device in devices)
        {
            if (!_devices.TryGetValue(device.Address, out var icon))
            {
                icon = CreateIcon();
                _devices.Add(device.Address, icon);
            }

            var image = IconFactory.CreateDeviceIcon(device.Kind, device.Battery.IsKnown ? device.Battery.Percent : null, lightGlyph, pixels);
            if (_images.TryGetValue(device.Address, out var previous))
            {
                icon.Icon = image;
                _images[device.Address] = image;
                previous.Dispose();
            }
            else
            {
                icon.Icon = image;
                _images[device.Address] = image;
            }

            icon.Text = DeviceText.Tooltip(device, MaxTooltipLength);
            icon.Visible = true;
        }
    }

    public void Dispose()
    {
        foreach (var address in _devices.Keys.ToList())
            Remove(address);

        _appIcon.Visible = false;
        _appIcon.Dispose();
        _appImage?.Dispose();
    }

    private NotifyIcon CreateIcon()
    {
        var icon = new NotifyIcon
        {
            Visible = false,
            ContextMenuStrip = BuildMenu(),
        };
        icon.MouseUp += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                _showFlyout();
        };
        return icon;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        try
        {
            menu.Font = new Font("Segoe UI", 9f);
        }
        catch (ArgumentException)
        {
            menu.Font = SystemFonts.MenuFont;
        }
        menu.Items.Add("刷新", null, (_, _) => _refresh());
        menu.Items.Add("设备列表", null, (_, _) => _showFlyout());
        menu.Items.Add("设置…", null, (_, _) => _showSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _exit());
        return menu;
    }

    private void Remove(string address)
    {
        if (_devices.TryGetValue(address, out var icon))
        {
            icon.Visible = false;
            icon.Dispose();
            _devices.Remove(address);
        }

        if (_images.TryGetValue(address, out var image))
        {
            image.Dispose();
            _images.Remove(address);
        }
    }

    private static void ReplaceImage(NotifyIcon icon, ref Icon? slot, Icon next)
    {
        var previous = slot;
        slot = next;
        icon.Icon = next;
        previous?.Dispose();
    }

    private static string Limit(string text) => text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
}
