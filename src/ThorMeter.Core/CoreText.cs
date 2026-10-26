using System.Globalization;
using System.Resources;

namespace ThorMeter.Core;

internal static class CoreText
{
    private static readonly ResourceManager Resources = new("ThorMeter.Core.CoreText", typeof(CoreText).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";
    public static string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
