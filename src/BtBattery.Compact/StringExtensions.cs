namespace BtBattery.Core;

internal static class StringExtensions
{
    public static bool Contains(this string text, string value, StringComparison comparison) =>
        text.IndexOf(value, comparison) >= 0;
}
