using System.Globalization;
using System.Resources;

namespace ThorMeter.Infrastructure;

internal static class CaptureText
{
    private static readonly ResourceManager Resources = new("ThorMeter.Infrastructure.CaptureText", typeof(CaptureText).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";
    public static string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
