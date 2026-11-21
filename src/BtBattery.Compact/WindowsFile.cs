using System.ComponentModel;

namespace BtBattery.Core;

internal static class WindowsFile
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);

    public static void MoveReplacing(string existing, string destination)
    {
        const uint MOVEFILE_REPLACE_EXISTING = 1;
        if (!MoveFileEx(existing, destination, MOVEFILE_REPLACE_EXISTING))
            throw new IOException("无法替换设置文件。", new Win32Exception(Marshal.GetLastWin32Error()));
    }
}
