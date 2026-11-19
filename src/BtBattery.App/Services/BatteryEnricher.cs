using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;
using Windows.Storage.Streams;

namespace BtBattery.App;

/// <summary>
/// 设备属性没有百分比时的后两层。Windows 电池接口按地址或容器对上；
/// GATT 0x180F 只补仍然未知的配件，读完就释放。哪几台现场读由 <see cref="GattRefreshPlan"/> 安排：
/// 缓存命中不占名额，需要新读的按没读过优先、其次最久没读来轮换，每轮最多四台。
/// </summary>
internal sealed class BatteryEnricher
{
    private readonly Dictionary<string, GattCacheEntry> _gattCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _gattBlocked;

    public async Task AddPowerReadingsAsync(List<DeviceObservation> observations, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var selector = Battery.GetDeviceSelector();
            var infos = await DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.ContainerId" })
                .AsTask(timeout.Token)
                .ConfigureAwait(false);

            foreach (var info in infos)
            {
                var reading = await ReadBatteryAsync(info, timeout.Token).ConfigureAwait(false);
                if (reading is null)
                    continue;

                var address = BluetoothAddress.ExtractFromInstanceId(info.Id) ?? "";
                var container = PropertyValues.AsGuid(info.Properties.TryGetValue("System.Devices.ContainerId", out var raw) ? raw : null);
                if (address.Length == 0 && container is null)
                    continue;

                observations.Add(new DeviceObservation
                {
                    Address = address,
                    Source = ObservationSource.WindowsPowerApi,
                    ContainerId = container,
                    Battery = reading,
                    Note = reading.IsCharging == true
                        ? $"Windows 电池接口：{reading.Percent}%，充电中"
                        : $"Windows 电池接口：{reading.Percent}%",
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or OperationCanceledException or IOException)
        {
            AppLog.Error("Windows 电池接口不可用。", ex);
        }
    }

    public async Task AddGattReadingsAsync(
        IReadOnlyList<TrackedDevice> devices,
        List<DeviceObservation> observations,
        CancellationToken cancellationToken)
    {
        var lastRead = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _gattCache)
            lastRead[pair.Key] = pair.Value.At;

        var plan = GattRefreshPlan.Select(devices, lastRead, DateTimeOffset.UtcNow, _gattBlocked);
        foreach (var choice in plan)
        {
            if (choice.Action != GattRefreshAction.UseCache)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            if (_gattCache.TryGetValue(choice.Address, out var cached))
                observations.Add(cached.Observation);
        }

        if (_gattBlocked)
            return;

        foreach (var choice in plan)
        {
            if (choice.Action != GattRefreshAction.ReadLive)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var observation = await ReadGattAsync(choice.Address, cancellationToken).ConfigureAwait(false);
            _gattCache[choice.Address] = new GattCacheEntry(DateTimeOffset.UtcNow, observation);
            observations.Add(observation);
            if (observation.Note?.Contains("拒绝访问", StringComparison.Ordinal) == true)
            {
                _gattBlocked = true;
                AppLog.Info("GATT 电池服务拒绝访问。这一次运行不再重复尝试。未打包的程序有时没有 bluetooth 设备能力。");
                break;
            }
        }
    }

    private static async Task<BatteryReading?> ReadBatteryAsync(DeviceInformation info, CancellationToken cancellationToken)
    {
        var battery = await Battery.FromIdAsync(info.Id).AsTask(cancellationToken).ConfigureAwait(false);
        if (battery is null)
            return null;

        var report = battery.GetReport();
        var percent = BatteryMath.FromCapacities(report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);
        if (percent is not int value)
            return null;

        bool? charging = report.Status switch
        {
            Windows.System.Power.BatteryStatus.Charging => true,
            Windows.System.Power.BatteryStatus.Discharging => false,
            _ => (bool?)null,
        };
        return BatteryReading.Known(value, BatterySource.WindowsPowerApi, charging);
    }

    private static async Task<DeviceObservation> ReadGattAsync(string address, CancellationToken cancellationToken)
    {
        var numeric = BluetoothAddress.ToUInt64(address);
        if (numeric is null)
            return NoteOnly(address, "GATT 电池服务：地址无效");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        BluetoothLEDevice? device = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(numeric.Value).AsTask(timeout.Token).ConfigureAwait(false);
            if (device is null)
                return NoteOnly(address, "GATT 电池服务：不是 BLE 设备或当前不可达");

            var percents = new List<int>();
            var status = await ReadServiceAsync(device, BluetoothCacheMode.Cached, percents, timeout.Token).ConfigureAwait(false);
            if (percents.Count == 0 && status != GattCommunicationStatus.AccessDenied)
                status = await ReadServiceAsync(device, BluetoothCacheMode.Uncached, percents, timeout.Token).ConfigureAwait(false);

            if (status == GattCommunicationStatus.AccessDenied)
                return NoteOnly(address, "GATT 电池服务：拒绝访问");

            if (percents.Count == 0)
                return NoteOnly(address, "GATT 电池服务：没有读到 0x2A19");

            var lowest = percents.Min();
            return new DeviceObservation
            {
                Address = address,
                Source = ObservationSource.Gatt,
                Battery = BatteryReading.Known(lowest, BatterySource.GattBatteryService),
                Note = percents.Count == 1
                    ? $"GATT 电池服务：{lowest}%"
                    : $"GATT 电池服务：{string.Join(" / ", percents.OrderBy(item => item))}%，采用最低值",
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException)
        {
            return NoteOnly(address, "GATT 电池服务：拒绝访问");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            AppLog.Error("读取 GATT 电量失败：" + address, ex);
            return NoteOnly(address, "GATT 电池服务：读取失败");
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static async Task<GattCommunicationStatus> ReadServiceAsync(
        BluetoothLEDevice device,
        BluetoothCacheMode cacheMode,
        List<int> percents,
        CancellationToken cancellationToken)
    {
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, cacheMode)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (services.Status != GattCommunicationStatus.Success)
            return services.Status;

        foreach (var service in services.Services)
        {
            try
            {
                var characteristics = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, cacheMode)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (characteristics.Status != GattCommunicationStatus.Success)
                    continue;

                foreach (var characteristic in characteristics.Characteristics)
                {
                    var read = await characteristic.ReadValueAsync(cacheMode).AsTask(cancellationToken).ConfigureAwait(false);
                    if (read.Status != GattCommunicationStatus.Success || read.Value is null || read.Value.Length == 0)
                        continue;

                    var reader = DataReader.FromBuffer(read.Value);
                    var level = BatteryMath.NormalizeStoredLevel(reader.ReadByte());
                    if (level is int value)
                        percents.Add(value);
                }
            }
            finally
            {
                service.Dispose();
            }
        }

        return GattCommunicationStatus.Success;
    }

    private static DeviceObservation NoteOnly(string address, string note) => new()
    {
        Address = address,
        Source = ObservationSource.Gatt,
        Note = note,
    };

    private sealed record GattCacheEntry(DateTimeOffset At, DeviceObservation Observation);
}
