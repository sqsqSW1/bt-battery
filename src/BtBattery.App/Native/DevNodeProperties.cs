namespace BtBattery.App;

internal readonly record struct PropBlob(uint Type, byte[] Data);

/// <summary>读取一个即插即用节点上的 DEVPROPKEY。属性不存在时返回 null，不抛异常。</summary>
internal static class DevNodeProperties
{
    public static PropBlob? Read(uint devInst, DEVPROPKEY key)
    {
        uint size = 0;
        var status = NativeMethods.CM_Get_DevNode_PropertyW(devInst, ref key, out var type, null, ref size, 0);
        if (size == 0 || size > 8192)
            return null;
        if (status != NativeMethods.CR_SUCCESS && status != NativeMethods.CR_BUFFER_SMALL)
            return null;

        var buffer = new byte[size];
        status = NativeMethods.CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0);
        if (status != NativeMethods.CR_SUCCESS || size == 0 || size > buffer.Length)
            return null;

        if (size != buffer.Length)
            Array.Resize(ref buffer, (int)size);

        return new PropBlob(type, buffer);
    }

    public static string? AsString(PropBlob? blob)
    {
        if (blob is not { Type: NativeMethods.DEVPROP_TYPE_STRING } value || value.Data.Length < 2)
            return null;

        return Encoding.Unicode.GetString(value.Data).TrimEnd('\0').Trim();
    }

    public static IReadOnlyList<string> AsStringList(PropBlob? blob)
    {
        if (blob is not { } value)
            return Array.Empty<string>();

        if (value.Type == NativeMethods.DEVPROP_TYPE_STRING)
        {
            var single = AsString(value);
            return single is { Length: > 0 } ? new[] { single } : Array.Empty<string>();
        }

        if (value.Type != NativeMethods.DEVPROP_TYPE_STRING_LIST)
            return Array.Empty<string>();

        return Encoding.Unicode.GetString(value.Data).Split('\0')
            .Select(item => item.Trim())
            .Where(item => item.Length != 0)
            .ToArray();
    }

    public static byte? AsByte(PropBlob? blob) =>
        blob is { Type: NativeMethods.DEVPROP_TYPE_BYTE, Data.Length: > 0 } value ? value.Data[0] : null;

    public static uint? AsUInt32(PropBlob? blob) =>
        blob is { Type: NativeMethods.DEVPROP_TYPE_UINT32, Data.Length: >= 4 } value
            ? BitConverter.ToUInt32(value.Data, 0)
            : null;

    public static Guid? AsGuid(PropBlob? blob)
    {
        if (blob is not { Type: NativeMethods.DEVPROP_TYPE_GUID, Data.Length: >= 16 } value)
            return null;

        var bytes = value.Data;
        if (bytes.Length != 16)
        {
            bytes = new byte[16];
            Buffer.BlockCopy(value.Data, 0, bytes, 0, 16);
        }

        return new Guid(bytes);
    }
}
