using BtBattery.Core;

namespace BtBattery.Core.Tests;

public class GattRefreshPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, DateTimeOffset> NoReads = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Fresh_cache_hits_do_not_spend_the_live_read_budget()
    {
        var devices = Accessories(8);
        var lastRead = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 5; i++)
            lastRead[devices[i].Address] = Now.AddMinutes(-1);

        var plan = GattRefreshPlan.Select(devices, lastRead, Now, gattBlocked: false);

        Assert.Equal(Addresses(devices.Take(5)), Chosen(plan, GattRefreshAction.UseCache));
        Assert.Equal(Addresses(devices.Skip(5)), Chosen(plan, GattRefreshAction.ReadLive));
    }

    [Fact]
    public void Later_accessories_get_a_live_read_within_a_few_refreshes()
    {
        var devices = Accessories(6);
        var lastRead = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var now = Now;

        var first = Live(devices, lastRead, now);
        Assert.Equal(Addresses(devices.Take(4)), first);
        Remember(lastRead, first, now);

        now = now.AddSeconds(30);
        var second = Live(devices, lastRead, now);
        Assert.Equal(Addresses(devices.Skip(4)), second);
        Assert.Equal(Addresses(devices.Take(4)), Cache(devices, lastRead, now));
        Remember(lastRead, second, now);

        now = now.AddSeconds(30);
        Assert.Empty(Live(devices, lastRead, now));
        Assert.Equal(Addresses(devices), Cache(devices, lastRead, now));
    }

    [Fact]
    public void Never_tried_devices_are_read_before_an_expired_cache()
    {
        var devices = Accessories(6);
        var lastRead = Reads(
            (devices[0].Address, Now.AddMinutes(-30)),
            (devices[1].Address, Now.AddMinutes(-20)),
            (devices[2].Address, Now.AddMinutes(-15)),
            (devices[3].Address, Now.AddMinutes(-11)));

        var plan = GattRefreshPlan.Select(devices, lastRead, Now, gattBlocked: false);

        Assert.Equal(
            new[] { devices[4].Address, devices[5].Address, devices[0].Address, devices[1].Address },
            Chosen(plan, GattRefreshAction.ReadLive));
        Assert.Empty(Chosen(plan, GattRefreshAction.UseCache));
        Assert.DoesNotContain(plan, item => item.Address == devices[2].Address || item.Address == devices[3].Address);
    }

    [Fact]
    public void Oldest_last_read_is_chosen_once_every_device_has_been_tried()
    {
        var devices = Accessories(6);
        var lastRead = Reads(
            (devices[0].Address, Now.AddMinutes(-12)),
            (devices[1].Address, Now.AddMinutes(-40)),
            (devices[2].Address, Now.AddMinutes(-11)),
            (devices[3].Address, Now.AddMinutes(-30)),
            (devices[4].Address, Now.AddMinutes(-20)),
            (devices[5].Address, Now.AddMinutes(-50)));

        var plan = GattRefreshPlan.Select(devices, lastRead, Now, gattBlocked: false);

        Assert.Equal(
            new[] { devices[5].Address, devices[1].Address, devices[3].Address, devices[4].Address },
            Chosen(plan, GattRefreshAction.ReadLive));
        Assert.Empty(Chosen(plan, GattRefreshAction.UseCache));
    }

    [Fact]
    public void Equal_last_read_times_keep_the_incoming_order()
    {
        var devices = Accessories(5);
        var stale = Now - GattRefreshPlan.CacheWindow;
        var lastRead = Reads(devices.Select(device => (device.Address, stale)).ToArray());

        var plan = GattRefreshPlan.Select(devices, lastRead, Now, gattBlocked: false);

        Assert.Equal(Addresses(devices.Take(4)), Chosen(plan, GattRefreshAction.ReadLive));
        Assert.DoesNotContain(plan, item => item.Address == devices[4].Address);
    }

    [Fact]
    public void A_refresh_schedules_at_most_four_live_reads()
    {
        Assert.Equal(4, GattRefreshPlan.MaxLiveReadsPerRefresh);
        var devices = Accessories(6);

        var plan = GattRefreshPlan.Select(devices, NoReads, Now, gattBlocked: false);

        Assert.Equal(Addresses(devices.Take(4)), Chosen(plan, GattRefreshAction.ReadLive));
        Assert.Empty(Chosen(plan, GattRefreshAction.UseCache));
    }

    [Fact]
    public void Blocked_run_replays_every_cached_device_and_does_not_read()
    {
        var devices = Accessories(6);
        var lastRead = Reads(
            (devices[0].Address, Now.AddMinutes(-1)),
            (devices[1].Address, Now.AddMinutes(-30)),
            (devices[3].Address, Now.AddMinutes(-11)));

        var plan = GattRefreshPlan.Select(devices, lastRead, Now, gattBlocked: true);

        Assert.Equal(
            new[] { devices[0].Address, devices[1].Address, devices[3].Address },
            Chosen(plan, GattRefreshAction.UseCache));
        Assert.Empty(Chosen(plan, GattRefreshAction.ReadLive));
    }

    [Fact]
    public void Cache_is_fresh_until_the_ten_minute_mark()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), GattRefreshPlan.CacheWindow);
        var devices = Accessories(2);
        var lastRead = Reads(
            (devices[0].Address, Now - GattRefreshPlan.CacheWindow + TimeSpan.FromTicks(1)),
            (devices[1].Address, Now - GattRefreshPlan.CacheWindow));

        var plan = GattRefreshPlan.Select(devices, lastRead, Now, gattBlocked: false);

        Assert.Equal(new[] { devices[0].Address }, Chosen(plan, GattRefreshAction.UseCache));
        Assert.Equal(new[] { devices[1].Address }, Chosen(plan, GattRefreshAction.ReadLive));
    }

    [Fact]
    public void Only_connected_accessories_with_unknown_battery_are_scheduled()
    {
        var headset = Accessory("AA:BB:CC:DD:EE:01", DeviceKind.Headset);
        var known = Accessory("AA:BB:CC:DD:EE:02", DeviceKind.Mouse, percent: 40);
        var disconnected = Accessory("AA:BB:CC:DD:EE:03", DeviceKind.Keyboard, connected: false);
        var phone = Accessory("AA:BB:CC:DD:EE:04", DeviceKind.Excluded);
        var unknownKind = Accessory("AA:BB:CC:DD:EE:05", DeviceKind.Unknown);
        var combo = Accessory("AA:BB:CC:DD:EE:06", DeviceKind.Combo);

        var plan = GattRefreshPlan.Select(
            new[] { known, disconnected, phone, unknownKind, headset, combo },
            NoReads,
            Now,
            gattBlocked: false);

        Assert.Equal(new[] { headset.Address, combo.Address }, Chosen(plan, GattRefreshAction.ReadLive));
    }

    [Fact]
    public void Duplicate_addresses_are_scheduled_once_and_cache_lookup_ignores_case()
    {
        var lower = Accessory("aa:bb:cc:dd:ee:0a");
        var again = Accessory("AA:BB:CC:DD:EE:0A");
        var other = Accessory("AA:BB:CC:DD:EE:0B");
        var lastRead = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["AA:BB:CC:DD:EE:0A"] = Now.AddMinutes(-2),
        };

        var plan = GattRefreshPlan.Select(new[] { lower, again, other }, lastRead, Now, gattBlocked: false);

        Assert.Equal(new[] { lower.Address }, Chosen(plan, GattRefreshAction.UseCache));
        Assert.Equal(new[] { other.Address }, Chosen(plan, GattRefreshAction.ReadLive));
    }

    private static IReadOnlyList<string> Live(
        IReadOnlyList<TrackedDevice> devices,
        IReadOnlyDictionary<string, DateTimeOffset> lastRead,
        DateTimeOffset now) =>
        Chosen(GattRefreshPlan.Select(devices, lastRead, now, gattBlocked: false), GattRefreshAction.ReadLive);

    private static IReadOnlyList<string> Cache(
        IReadOnlyList<TrackedDevice> devices,
        IReadOnlyDictionary<string, DateTimeOffset> lastRead,
        DateTimeOffset now) =>
        Chosen(GattRefreshPlan.Select(devices, lastRead, now, gattBlocked: false), GattRefreshAction.UseCache);

    private static void Remember(
        Dictionary<string, DateTimeOffset> lastRead,
        IReadOnlyList<string> addresses,
        DateTimeOffset at)
    {
        foreach (var address in addresses)
            lastRead[address] = at;
    }

    private static IReadOnlyList<string> Chosen(IReadOnlyList<GattRefreshChoice> plan, GattRefreshAction action) =>
        plan.Where(item => item.Action == action).Select(item => item.Address).ToList();

    private static IEnumerable<string> Addresses(IEnumerable<TrackedDevice> devices) =>
        devices.Select(device => device.Address);

    private static List<TrackedDevice> Accessories(int count)
    {
        var devices = new List<TrackedDevice>(count);
        for (var i = 1; i <= count; i++)
            devices.Add(Accessory($"AA:BB:CC:DD:EE:{i:X2}"));
        return devices;
    }

    private static Dictionary<string, DateTimeOffset> Reads(params (string Address, DateTimeOffset At)[] entries)
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, at) in entries)
            map[address] = at;
        return map;
    }

    private static TrackedDevice Accessory(
        string address,
        DeviceKind kind = DeviceKind.Mouse,
        bool connected = true,
        int? percent = null) => new()
    {
        Address = address,
        Name = address,
        Kind = kind,
        KindLabel = DeviceKindLabels.For(kind),
        ClassificationSource = "测试",
        IsConnected = connected,
        Battery = percent is int value
            ? BatteryReading.Known(value, BatterySource.DeviceProperty)
            : BatteryReading.Unknown,
    };
}
