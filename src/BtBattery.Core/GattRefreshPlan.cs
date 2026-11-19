namespace BtBattery.Core;

/// <summary>一轮刷新里，某台配件的 GATT 电量该沿用缓存，还是现场再读一次。</summary>
public enum GattRefreshAction
{
    /// <summary>缓存仍在有效期内，或这一次运行已经被拒绝访问。不发起新的连接。</summary>
    UseCache = 0,

    /// <summary>本轮名额内的现场读取。读完即释放，不保留连接。</summary>
    ReadLive = 1,
}

/// <summary>一台配件在本轮 GATT 刷新中的安排。</summary>
public readonly record struct GattRefreshChoice(string Address, GattRefreshAction Action);

/// <summary>
/// 安排仍然没有电量的已连接配件：哪些沿用 GATT 缓存，哪些这一轮现场读。
/// 缓存还在有效期内就不占名额。需要新读的按「没读过优先，其次最久没读」轮换，
/// 每轮最多现场读 <see cref="MaxLiveReadsPerRefresh"/> 台。没有安排的配件不要编百分比。
/// </summary>
public static class GattRefreshPlan
{
    /// <summary>每一轮刷新最多现场读取的台数。</summary>
    public const int MaxLiveReadsPerRefresh = 4;

    /// <summary>同一台设备在这个时间内不重复现场读取。</summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 选出本轮安排。<paramref name="lastReadUtc"/> 是上次现场读取的时间，地址比较不区分大小写。
    /// <paramref name="gattBlocked"/> 为真时只回放已有缓存，不再发起读取。
    /// </summary>
    public static IReadOnlyList<GattRefreshChoice> Select(
        IEnumerable<TrackedDevice> devices,
        IReadOnlyDictionary<string, DateTimeOffset> lastReadUtc,
        DateTimeOffset utcNow,
        bool gattBlocked)
    {
        var lastRead = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in lastReadUtc)
            lastRead[pair.Key] = pair.Value;

        var useCache = new List<GattRefreshChoice>();
        var pending = new List<PendingRead>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;

        foreach (var device in devices)
        {
            var order = position++;
            if (!IsCandidate(device) || !seen.Add(device.Address))
                continue;

            var hasRead = lastRead.TryGetValue(device.Address, out var readAt);
            var fresh = hasRead && utcNow - readAt < CacheWindow;
            if (fresh || (gattBlocked && hasRead))
            {
                useCache.Add(new GattRefreshChoice(device.Address, GattRefreshAction.UseCache));
                continue;
            }

            if (gattBlocked)
                continue;

            pending.Add(new PendingRead(device.Address, hasRead ? readAt : DateTimeOffset.MinValue, order));
        }

        var result = new List<GattRefreshChoice>(useCache.Count + MaxLiveReadsPerRefresh);
        result.AddRange(useCache);
        result.AddRange(pending
            .OrderBy(item => item.ReadAt)
            .ThenBy(item => item.Order)
            .Take(MaxLiveReadsPerRefresh)
            .Select(item => new GattRefreshChoice(item.Address, GattRefreshAction.ReadLive)));
        return result;
    }

    private static bool IsCandidate(TrackedDevice device) =>
        !string.IsNullOrEmpty(device.Address)
        && device.IsConnected
        && DeviceKindLabels.IsAccessory(device.Kind)
        && !device.Battery.IsKnown;

    private readonly record struct PendingRead(string Address, DateTimeOffset ReadAt, int Order);
}
