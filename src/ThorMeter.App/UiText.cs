using System.Globalization;
using System.Resources;

namespace ThorMeter.App;

public static class UiText
{
    private static readonly ResourceManager Resources = new("ThorMeter.App.UiText", typeof(UiText).Assembly);

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    public static string WindowTitle => Get(nameof(WindowTitle));
    public static string AlwaysOnTop => Get(nameof(AlwaysOnTop));
    public static string Tagline => Get(nameof(Tagline));
    public static string Start => Get(nameof(Start));
    public static string Stop => Get(nameof(Stop));
    public static string ResetTotals => Get(nameof(ResetTotals));
    public static string OpenReplay => Get(nameof(OpenReplay));
    public static string Adapter => Get(nameof(Adapter));
    public static string Refresh => Get(nameof(Refresh));
    public static string Connection => Get(nameof(Connection));
    public static string UseConnection => Get(nameof(UseConnection));
    public static string RecordTraffic => Get(nameof(RecordTraffic));
    public static string ObservedDamage => Get(nameof(ObservedDamage));
    public static string Player => Get(nameof(Player));
    public static string Class => Get(nameof(Class));
    public static string TotalDamage => Get(nameof(TotalDamage));
    public static string Dps => Get(nameof(Dps));
    public static string ShowHps => Get(nameof(ShowHps));
    public static string TotalHealing => Get(nameof(TotalHealing));
    public static string Hps => Get(nameof(Hps));
    public static string HealingCaution => Get(nameof(HealingCaution));
    public static string CopyHealDiagnostics => Get(nameof(CopyHealDiagnostics));
    public static string HealDiagnosticsNote => Get(nameof(HealDiagnosticsNote));
    public static string Pause => Get(nameof(Pause));
    public static string Resume => Get(nameof(Resume));
    public static string Target => Get(nameof(Target));
    public static string TargetCategory => Get(nameof(TargetCategory));
    public static string TargetSearch => Get(nameof(TargetSearch));
    public static string TargetAlias => Get(nameof(TargetAlias));
    public static string SetTargetAlias => Get(nameof(SetTargetAlias));
    public static string ClearTargetAlias => Get(nameof(ClearTargetAlias));
    public static string TargetTag => Get(nameof(TargetTag));
    public static string SelectMainTarget => Get(nameof(SelectMainTarget));
    public static string MainTargetHint => Get(nameof(MainTargetHint));
    public static string AliasSelected => Get(nameof(AliasSelected));
    public static string SetAlias => Get(nameof(SetAlias));
    public static string ClearAlias => Get(nameof(ClearAlias));
    public static string Rank => Get(nameof(Rank));
    public static string Pin => Get(nameof(Pin));
    public static string Footer => Get(nameof(Footer));

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
