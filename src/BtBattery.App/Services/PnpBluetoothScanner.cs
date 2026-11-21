namespace BtBattery.App;

/// <summary>
/// 走一遍当前存在的蓝牙节点，读取 DEVPKEY_Bluetooth_BatteryLevel。
/// 耳机的百分比经常写在免提 / 媒体节点上，而不是蓝牙节点本身，所以按地址合并，而不是按节点各画一个图标。
/// </summary>
internal static class PnpBluetoothScanner
{
    private const string MediaClass = "{4d36e96c-e325-11ce-bfc1-08002be10318}";

    private static readonly DEVPROPKEY BatteryKey = new(BluetoothProperty.BatteryLevelFormat, BluetoothProperty.BatteryLevelPid);
    private static readonly DEVPROPKEY AddressKey = new(BluetoothProperty.BluetoothInfoFormat, BluetoothProperty.DeviceAddressPid);
    private static readonly DEVPROPKEY ClassKey = new(BluetoothProperty.BluetoothInfoFormat, BluetoothProperty.ClassOfDevicePid);
    private static readonly DEVPROPKEY FriendlyNameKey = new(BluetoothProperty.DevicePropertyFormat, BluetoothProperty.FriendlyNamePid);
    private static readonly DEVPROPKEY DescriptionKey = new(BluetoothProperty.DevicePropertyFormat, BluetoothProperty.DeviceDescPid);
    private static readonly DEVPROPKEY ClassNameKey = new(BluetoothProperty.DevicePropertyFormat, BluetoothProperty.DeviceClassPid);
    private static readonly DEVPROPKEY CompatibleIdsKey = new(BluetoothProperty.DevicePropertyFormat, BluetoothProperty.CompatibleIdsPid);
    private static readonly DEVPROPKEY ContainerKey = new(BluetoothProperty.DeviceContainerFormat, BluetoothProperty.ContainerIdPid);

    public static ScanLayerResult Read()
    {
        var warnings = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var id in Enumerate(null, NativeMethods.CM_GETIDLIST_FILTER_PRESENT))
            {
                if (LooksLikeBluetooth(id))
                    ids.Add(id);
            }

            foreach (var id in Enumerate(MediaClass, NativeMethods.CM_GETIDLIST_FILTER_PRESENT | NativeMethods.CM_GETIDLIST_FILTER_CLASS))
                ids.Add(id);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            warnings.Add("无法调用 cfgmgr32，读不到系统里的蓝牙电量属性。");
            return new ScanLayerResult(Array.Empty<DeviceObservation>(), warnings);
        }

        var observations = new List<DeviceObservation>();
        foreach (var id in ids)
        {
            try
            {
                var observation = ReadNode(id);
                if (observation is not null)
                    observations.Add(observation);
            }
            catch (Exception ex)
            {
                AppLog.Error("读取设备节点失败：" + id, ex);
            }
        }

        return new ScanLayerResult(observations, warnings);
    }

    private static DeviceObservation? ReadNode(string instanceId)
    {
        if (NativeMethods.CM_Locate_DevNodeW(out var devInst, instanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL) != NativeMethods.CR_SUCCESS)
            return null;

        var address = BluetoothAddress.Normalize(DevNodeProperties.AsString(DevNodeProperties.Read(devInst, AddressKey)))
            ?? BluetoothAddress.ExtractFromInstanceId(instanceId);
        var batteryByte = DevNodeProperties.AsByte(DevNodeProperties.Read(devInst, BatteryKey));
        var percent = batteryByte is byte raw ? BatteryMath.NormalizeStoredLevel(raw) : null;
        var container = DevNodeProperties.AsGuid(DevNodeProperties.Read(devInst, ContainerKey));

        // 没有地址的媒体节点只在它确实带着电量时才有用，用来按容器挂回耳机。
        if (address is null && (percent is null || container is null))
            return null;

        if (address is null && !LooksLikeBluetooth(instanceId) && percent is null)
            return null;

        var friendly = DevNodeProperties.AsString(DevNodeProperties.Read(devInst, FriendlyNameKey));
        var description = DevNodeProperties.AsString(DevNodeProperties.Read(devInst, DescriptionKey));
        var name = string.IsNullOrWhiteSpace(friendly) ? description : friendly;
        var cod = DevNodeProperties.AsUInt32(DevNodeProperties.Read(devInst, ClassKey));
        var connected = ReadStarted(devInst);
        var battery = percent is int level ? BatteryReading.Known(level, BatterySource.DeviceProperty) : null;

        return new DeviceObservation
        {
            Address = address ?? "",
            Source = ObservationSource.Pnp,
            Name = name,
            NameRank = string.IsNullOrWhiteSpace(friendly) ? 40 : 50,
            IsConnected = connected,
            Signals = new ClassificationInput
            {
                Name = name,
                ClassOfDevice = cod is 0 or null ? null : cod,
                PnpClass = DevNodeProperties.AsString(DevNodeProperties.Read(devInst, ClassNameKey)),
                CompatibleIds = DevNodeProperties.AsStringList(DevNodeProperties.Read(devInst, CompatibleIdsKey)),
            },
            Battery = battery,
            InstanceId = instanceId,
            ContainerId = container,
            Note = battery is null ? null : $"系统蓝牙电量属性：{battery.Percent}%（{ShortId(instanceId)}）",
        };
    }

    private static bool? ReadStarted(uint devInst)
    {
        if (NativeMethods.CM_Get_DevNode_Status(out var status, out _, devInst, 0) != NativeMethods.CR_SUCCESS)
            return null;

        return (status & NativeMethods.DN_STARTED) != 0 && (status & NativeMethods.DN_HAS_PROBLEM) == 0;
    }

    private static bool LooksLikeBluetooth(string instanceId)
    {
        if (instanceId.Contains("BTH", StringComparison.OrdinalIgnoreCase))
            return true;

        // HID、免提、AVRCP、音频接收这些服务 UUID 会出现在经典蓝牙的实例 ID 里。
        return instanceId.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)
            || instanceId.Contains("0000111e-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)
            || instanceId.Contains("0000110e-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)
            || instanceId.Contains("0000110b-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)
            || instanceId.Contains("00001108-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Enumerate(string? filter, uint flags)
    {
        var status = NativeMethods.CM_Get_Device_ID_List_SizeW(out var length, filter, flags);
        if (status != NativeMethods.CR_SUCCESS || length <= 1)
            return Array.Empty<string>();

        var buffer = new char[length];
        status = NativeMethods.CM_Get_Device_ID_ListW(filter, buffer, length, flags);
        if (status != NativeMethods.CR_SUCCESS)
            return Array.Empty<string>();

        return new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ShortId(string instanceId)
    {
        var slash = instanceId.IndexOf('\\');
        return slash > 0 ? instanceId[..slash] : instanceId;
    }
}
