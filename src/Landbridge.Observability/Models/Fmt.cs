using System.Globalization;

namespace Landbridge.Observability.Models;

/// <summary>Invariant-culture number formatting for values interpolated into inline CSS.</summary>
public static class Fmt
{
    public static string Pct(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    public static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
