using Landbridge.ControlPlane;
using Landbridge.Core;

namespace Landbridge.Mcp;

/// <summary>
/// Compose Lead/worker tool JSON from Hub nouns. Bindings and mark-read stay off Hub.
/// </summary>
public static class HubPackager
{
    public static ProfileRoutingView Profiles(IReadOnlyList<HubMachine> machines)
    {
        var live = machines.Where(m => m.Live).ToList();
        var byProfile = new Dictionary<string, List<ProfileMachineView>>(StringComparer.Ordinal);
        foreach (var machine in live)
        {
            var view = new ProfileMachineView(
                string.IsNullOrEmpty(machine.Slug) ? machine.Id.ToString("D") : machine.Slug,
                machine.Ready, machine.UnderBackPressure, machine.LastSpokeAt,
                machine.Name, machine.Os);
            foreach (var profile in machine.Profiles)
            {
                if (!byProfile.TryGetValue(profile, out var list))
                    byProfile[profile] = list = [];
                list.Add(view);
            }
        }

        var profiles = byProfile
            .Select(p => new ProfileRoutingEntry(
                p.Key, p.Value.Any(m => m.Ready),
                p.Value.OrderBy(m => m.MachineId, StringComparer.Ordinal).ToList()))
            .OrderBy(p => p.Profile, StringComparer.Ordinal)
            .ToList();
        return new ProfileRoutingView(profiles, live.Count);
    }

    public static TeamStateView TeamState(string teamId, IReadOnlyList<HubSessionListItem> rows)
    {
        var slugs = rows.ToDictionary(s => s.Id, s => s.Slug);
        var counts = rows
            .GroupBy(s => s.State)
            .ToDictionary(g => g.Key, g => g.Count());
        var summaries = rows.Select(s => new TeamSessionSummary(
            string.IsNullOrEmpty(s.Slug) ? s.Id.ToString("D") : s.Slug,
            s.Namespace, s.State, s.Attempt, s.ParkMachine is not null,
            s.ContinuesSessionId is { } cid
                ? slugs.GetValueOrDefault(cid) is { Length: > 0 } cs ? cs : cid.ToString("D")
                : null,
            s.CompletionProvenance, s.HasReport, s.InputKind, s.HasQuestion,
            s.InfrastructureRequeues, s.LastRequeueReason)).ToList();
        var slug = rows.Select(s => s.TeamSlug).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        return new TeamStateView(slug ?? teamId, rows.Count, counts, summaries);
    }

    public static LeadInboxView InboxIdentifiers(IReadOnlyList<HubSessionListItem> rows)
    {
        var items = rows
            .Where(s => !s.Hidden)
            .SelectMany(s => LeadInboxKindMapping.ItemsFor(
                string.IsNullOrEmpty(s.Slug) ? s.Id.ToString("D") : s.Slug,
                s.Namespace, s.Health, s.MessageState, s.InputKind, s.MessageId, s.ReportUnread))
            .OrderBy(i => LeadInboxKindMapping.Rank(i.Kind))
            .ThenBy(i => i.SessionId, StringComparer.Ordinal)
            .ToList();
        return new LeadInboxView(items);
    }
}
