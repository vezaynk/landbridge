namespace Landbridge.ControlPlane;

/// <summary>
/// Where a dollar figure in the §12 measured view came from. Rendered, not merely stored:
/// a number Landbridge invented must never look like one a harness stated (§2 principle 2).
/// Nothing derives cost today — only <see cref="Reported"/> and <see cref="None"/> exist.
/// </summary>
public enum UsageCostProvenance
{
    /// <summary>The harness computed and reported this cost.</summary>
    Reported,

    /// <summary>No cost is known — the harness reported none. An absence of measurement,
    /// not a measurement of zero.</summary>
    None,
}
