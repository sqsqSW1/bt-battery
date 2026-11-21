namespace BtBattery.Core;

/// <summary>蓝牙地址统一成 AA:BB:CC:DD:EE:FF（高字节在前，与 Windows 设备管理器一致）。</summary>
public static class BluetoothAddress
{
    public static string? Normalize(string? raw)
    {
        if (raw is null)
            return null;

        ulong address = 0;
        var count = 0;
        foreach (var ch in raw)
        {
            if (IsHex(ch))
            {
                if (count >= 12)
                    return null;
                address = (address << 4) | HexValue(ch);
                count++;
            }
        }

        return count == 12 ? Format(address) : null;
    }

    /// <summary>
    /// WinRT 的 BluetoothAddress 以及经典 API 的 BLUETOOTH_ADDRESS.ullLong
    /// 都是低 48 位，最低字节对应地址最后一组（显示时的最右边）。
    /// </summary>
    public static string? Format(ulong address)
    {
        if (address == 0 || address > 0xFFFF_FFFF_FFFF)
            return null;

        var digits = new char[17];
        for (var i = 0; i < 6; i++)
        {
            var shift = (5 - i) * 8;
            var b = (int)((address >> shift) & 0xFF);
            digits[i * 3] = ToHex(b >> 4);
            digits[i * 3 + 1] = ToHex(b & 0xF);
            if (i < 5)
                digits[i * 3 + 2] = ':';
        }

        return new string(digits);
    }

    public static ulong? ToUInt64(string? normalized)
    {
        var text = Normalize(normalized);
        if (text is null)
            return null;

        ulong value = 0;
        foreach (var ch in text)
        {
            if (ch == ':')
                continue;
            value = (value << 4) | HexValue(ch);
        }

        return value;
    }

    /// <summary>从实例 ID 里抽出 Dev_AABBCCDDEEFF 这种地址。</summary>
    public static string? ExtractFromInstanceId(string? instanceId)
    {
        if (instanceId is null)
            return null;

        var marker = instanceId.IndexOf("Dev_", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var parsed = Normalize(SliceHex(instanceId, marker + 4));
            if (parsed is not null)
                return parsed;
        }

        return null;
    }

    private static string SliceHex(string text, int start)
    {
        var end = start;
        while (end < text.Length && IsHex(text[end]) && end - start < 12)
            end++;
        return text[start..end];
    }

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static char ToHex(int value) =>
        (char)(value < 10 ? '0' + value : 'A' + (value - 10));

    private static ulong HexValue(char ch) => ch switch
    {
        >= '0' and <= '9' => (ulong)(ch - '0'),
        >= 'A' and <= 'F' => (ulong)(ch - 'A' + 10),
        >= 'a' and <= 'f' => (ulong)(ch - 'a' + 10),
        _ => 0,
    };
}
