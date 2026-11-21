namespace BtBattery.Core;

/// <summary>
/// Windows 给同一台设备的节点经常叫「Headset」「Hands-Free AG」「AVRCP Transport」。
/// 这些不能拿来当产品名，否则托盘上会叠好几个无名图标。
/// </summary>
public static class DeviceNames
{
    private static readonly string[] ExactGeneric =
    {
        "headset",
        "headphones",
        "headphone",
        "earbuds",
        "earbud",
        "hands-free",
        "hands-free ag",
        "handsfree",
        "handsfree ag",
        "avrcp transport",
        "avrcp",
        "audio sink",
        "audio source",
        "bluetooth",
        "bluetooth device",
        "bluetooth peripheral device",
        "bluetooth peripheral",
        "hid device",
        "hid-compliant device",
        "hid-compliant mouse",
        "hid-compliant keyboard",
        "hid-compliant game controller",
        "personal area network",
        "device identification service",
        "generic access",
        "unknown",
        "unknown device",
        "耳机",
        "头戴式耳机",
        "免提",
        "免提音频",
        "免提音频网关",
        "蓝牙",
        "蓝牙设备",
        "蓝牙外设",
        "蓝牙外围设备",
        "未知设备",
    };

    public static bool IsGeneric(string? name)
    {
        var trimmed = name?.Trim();
        if (trimmed is null || trimmed.Length == 0)
            return true;

        var lower = trimmed.ToLowerInvariant();
        if (ExactGeneric.Contains(lower, StringComparer.Ordinal))
            return true;

        // 服务节点的名字经常带这些词，但真正的产品名（例如 “Xbox Wireless Headset”）不会只剩这些。
        if (lower.Contains("avrcp", StringComparison.Ordinal)
            || lower.Contains("hands-free", StringComparison.Ordinal)
            || lower.Contains("handsfree", StringComparison.Ordinal)
            || lower.Contains("audio gateway", StringComparison.Ordinal)
            || lower.Contains("免提", StringComparison.Ordinal))
            return true;

        return false;
    }

    public static string Choose(DeviceKind kind, IEnumerable<(string? Name, int Rank)> candidates)
    {
        string? best = null;
        var bestRank = int.MinValue;
        foreach (var (name, rank) in candidates)
        {
            if (IsGeneric(name))
                continue;
            if (rank > bestRank)
            {
                bestRank = rank;
                best = name!.Trim();
            }
        }

        return best ?? DeviceKindLabels.FallbackName(kind);
    }
}
