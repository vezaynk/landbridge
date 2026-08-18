namespace Landbridge.ControlPlane;

/// <summary>
/// The model → price step for turning reported TOKENS into a dollar figure, for harnesses that
/// report no cost of their own (§10, §12) — <b>a deliberate stub that returns nothing</b>.
///
/// <para><b>Why a stub rather than a table of real rates.</b> Two harnesses, two different
/// situations. Claude computes and reports its own cost per model, so its dollars are the
/// harness's own claim and this class is never consulted for them. Codex reports no cost
/// anywhere — not on its stream, not in its metrics — so a dollar figure for a Codex worker
/// could only be one Landbridge invented from a rate card it would then own, keep current, and be
/// wrong about quietly. Prices change, per-model rates differ by cache tier and by contract,
/// and a stale multiplier produces a number that looks authoritative and is not.</para>
///
/// <para><b>The empty state is the honest answer, and it is not the same as zero.</b>
/// <see cref="TryDerive"/> returns false for every model, so a Codex row renders "no cost
/// reported" rather than "$0.00". A zero would be a claim that the work was free; an absence is
/// a claim that nobody measured it, which is the true one. This is the same distinction §9.10
/// draws for an unmeasured Team's relay bytes, and the reason a 1.0 multiplier was rejected as
/// the placeholder: it would have made every Codex token cost exactly one dollar, which is
/// worse than saying nothing.</para>
///
/// <para><b>What filling this in would take.</b> A rate per (model, token bucket) — input,
/// output, cache read and cache write are priced differently, which is the whole reason those
/// four are stored separately — plus a decision about where the rates live (operator config,
/// since Landbridge must not ship a price list it cannot keep current) and an effective-date, since
/// re-deriving an old task's cost at today's rate would silently rewrite history. Every derived
/// figure must then carry <see cref="UsageCostProvenance.Derived"/> all the way to the pixel,
/// so it never renders like a reported one.</para>
/// </summary>
public sealed class ModelPricing
{
    /// <summary>
    /// The dollar cost of a reported token count, when a rate for <paramref name="model"/> is
    /// known. Always false today — see the type remarks. The signature takes the four buckets
    /// separately rather than a total precisely because they are not priced alike, so a future
    /// implementation cannot be written against a single blended number.
    /// </summary>
    public bool TryDerive(
        string? model,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        out decimal costUsd)
    {
        costUsd = 0m;
        return false;
    }
}

/// <summary>
/// Where a dollar figure in the §12 measured view came from. Rendered, not merely stored: a
/// number Landbridge multiplied into existence must never look like one a harness stated (§2
/// principle 2).
/// </summary>
public enum UsageCostProvenance
{
    /// <summary>The harness computed and reported this cost. Claude's <c>total_cost_usd</c> and
    /// its per-model <c>costUSD</c> land here.</summary>
    Reported,

    /// <summary>Landbridge derived this from token counts and a rate card
    /// (<see cref="ModelPricing"/>). Nothing produces this today.</summary>
    Derived,

    /// <summary>No cost is known — the harness reported none and no rate exists to derive one.
    /// An absence of measurement, not a measurement of zero.</summary>
    None,
}
