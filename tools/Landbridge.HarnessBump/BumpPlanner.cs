namespace Docket.HarnessBump;

/// <summary>What this run should do.</summary>
public enum BumpAction
{
    /// <summary>Nothing. Either every pin is current, or the one open bump PR already targets it.</summary>
    Idle,

    /// <summary>No bump PR is open and something is newer — open one.</summary>
    OpenNew,

    /// <summary>A bump PR is open but `latest` has moved past it — close it and open a fresh one.</summary>
    SupersedeOpen,

    /// <summary>Something is wrong enough that acting could churn or double-spend. A human looks.</summary>
    Blocked,
}

/// <summary>An existing open bump PR, and the versions its branch name says it targets.</summary>
/// <param name="Targets">npm package name → version this PR bumps it to. Packages it does not
/// touch are absent, and read as "leaves the pin alone".</param>
public sealed record OpenBumpPr(
    int Number,
    string Url,
    string Branch,
    IReadOnlyDictionary<string, string> Targets);

/// <summary>Why one package is, or is not, in the bump set — logged for every package, every run.</summary>
public sealed record PackageVerdict(HarnessPackage Package, string Pinned, string Latest, bool Bump, string Reason);

/// <summary>The whole decision for one run.</summary>
public sealed record BumpPlan(
    BumpAction Action,
    IReadOnlyList<Bump> Bumps,
    OpenBumpPr? Open,
    IReadOnlyList<PackageVerdict> Verdicts,
    string Summary)
{
    /// <summary>Branch name for the PR this plan would open; only meaningful when bumps exist.</summary>
    public string BranchName => BumpBranch.Name(Bumps);
}

/// <summary>
/// Decides what to do this run. Pure — no network, no git, no gh — so the cost behaviour is
/// unit-testable without spending anything to find out.
/// </summary>
/// <remarks>
/// THE ONLY STATE IS THE ONE OPEN BUMP PR. There is no known-bad list, no tracking file, no
/// label, and no scan of closed PRs. The rule is:
/// <list type="bullet">
///   <item>at most one bump PR is open at a time;</item>
///   <item>if one is open and <c>latest</c> has climbed past what it targets, close it as
///   superseded and open a fresh PR at the new <c>latest</c>;</item>
///   <item>if one is open and it already targets <c>latest</c>, do nothing;</item>
///   <item>if none is open and a pin is behind <c>latest</c>, open one.</item>
/// </list>
/// That bounds cost without remembering failures: a bump to version X gets exactly ONE e2e run,
/// when its PR is opened. Every later run sees the open PR already targeting X and idles. A red
/// PR therefore sits open costing nothing until either a human resolves it or a higher version
/// ships — and a version that failed can pass once a newer one arrives, which a suppression list
/// would have prevented from ever being retried.
/// </remarks>
public static class BumpPlanner
{
    /// <param name="pinned">npm name → version pinned in ci.yml, or null where the install is unpinned.</param>
    /// <param name="latest">npm name → <c>dist-tags.latest</c>.</param>
    /// <param name="open">The single open bump PR, or null if there is none.</param>
    public static BumpPlan Plan(
        IReadOnlyDictionary<string, string?> pinned,
        IReadOnlyDictionary<string, string> latest,
        OpenBumpPr? open)
    {
        var bumps = new List<Bump>();
        var verdicts = new List<PackageVerdict>();
        // Per package: the version that would be installed if the open PR merged as-is. A package
        // the PR does not touch keeps its current pin, so "has latest moved past the open PR" and
        // "is a package missing from the open PR now bumpable" are the same question.
        var openTargets = new Dictionary<string, SemVer>();
        var climbed = new List<string>();

        foreach (var package in HarnessPackages.All)
        {
            var havePin = pinned.TryGetValue(package.NpmName, out var pin);
            var haveLatest = latest.TryGetValue(package.NpmName, out var newest) && newest is not null;
            var pinText = pin ?? (havePin ? "<unpinned>" : "<absent>");
            var latestText = haveLatest ? newest! : "<unknown>";

            void Verdict(bool bump, string reason) =>
                verdicts.Add(new PackageVerdict(package, pinText, latestText, bump, reason));

            if (!havePin)
            {
                Verdict(false, "ci.yml has no install line for it — nothing to bump.");
                continue;
            }
            if (pin is null)
            {
                // Bumping an unpinned install is a no-op, and choosing a first version to pin at
                // is a judgement about what is known-good — a human's PR, not the bot's.
                Verdict(false, "ci.yml installs it UNPINNED — pin it before the bot can track it.");
                continue;
            }
            if (!haveLatest)
            {
                Verdict(false, "no dist-tags.latest read from the registry.");
                continue;
            }
            if (!SemVer.TryParse(pin, out var pinnedVersion))
            {
                Verdict(false, $"pinned version '{pin}' is not a semver — refusing to guess.");
                continue;
            }
            if (!SemVer.TryParse(newest, out var latestVersion))
            {
                Verdict(false, $"dist-tags.latest '{newest}' is not a semver — refusing to guess.");
                continue;
            }
            if (latestVersion.IsPrerelease)
            {
                // Belt and braces: we only ever read dist-tags.latest, so this should be
                // unreachable. It fires if a publisher points `latest` at a prerelease.
                Verdict(false, $"latest ({newest}) is a prerelease — never a bump target.");
                continue;
            }

            // What the open PR would leave this package at.
            var target = pinnedVersion;
            if (open is not null &&
                open.Targets.TryGetValue(package.NpmName, out var openTarget) &&
                SemVer.TryParse(openTarget, out var openVersion))
                target = openVersion;
            openTargets[package.NpmName] = target;

            if (SemVer.Compare(latestVersion, pinnedVersion) <= 0)
            {
                Verdict(false, SemVer.Compare(latestVersion, pinnedVersion) == 0
                    ? "already at latest."
                    : $"latest ({newest}) is OLDER than the pin — pin left alone.");
                continue;
            }

            bumps.Add(new Bump(package, pin, newest!));
            if (SemVer.Compare(latestVersion, target) > 0)
                climbed.Add($"{package.Short} {target} → {newest}");
            Verdict(true, $"latest ({newest}) is newer than the pin ({pin}).");
        }

        if (open is null)
            return bumps.Count > 0
                ? new BumpPlan(BumpAction.OpenNew, bumps, null, verdicts,
                    $"no bump PR open; opening one for {string.Join(", ", bumps)}")
                : new BumpPlan(BumpAction.Idle, bumps, null, verdicts,
                    "no bump PR open and every pin is at latest — nothing to do.");

        // A bump branch whose name yields no versions we recognise: refuse to act rather than
        // guess. Superseding on a misread would close a human's PR; opening a second one would
        // break the single-open-PR invariant that IS the cost guard.
        if (open.Targets.Count == 0)
            return new BumpPlan(BumpAction.Blocked, bumps, open, verdicts,
                $"PR #{open.Number} looks like a bump PR but its branch `{open.Branch}` names no " +
                "recognisable versions. Not touching it.");

        if (climbed.Count == 0)
            return new BumpPlan(BumpAction.Idle, bumps, open, verdicts,
                $"PR #{open.Number} already targets latest ({FormatTargets(openTargets)}) — leaving it alone. " +
                "Its e2e has already run once; re-running it is a human's call.");

        return new BumpPlan(BumpAction.SupersedeOpen, bumps, open, verdicts,
            $"latest has climbed past PR #{open.Number} ({string.Join(", ", climbed)}) — " +
            "closing it as superseded and opening a fresh PR at the new latest.");
    }

    private static string FormatTargets(IReadOnlyDictionary<string, SemVer> targets) =>
        string.Join(", ", targets
            .Select(kv => $"{HarnessPackages.ByNpmName(kv.Key)?.Short ?? kv.Key} {kv.Value}")
            .OrderBy(s => s, StringComparer.Ordinal));
}
