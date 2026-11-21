namespace BtBattery.App;

internal static class CompactStartup
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [STAThread]
    public static void Main()
    {
        // 在 WPF 或 WinForms 创建第一个窗口之前启用 Per-Monitor V2。
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        App.Main();
    }
}
