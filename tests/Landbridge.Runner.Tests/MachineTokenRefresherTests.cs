using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// The machine access-token refresher (spec §5, §13): it swaps the token the
/// channel presents at ~50% of the token's remaining lifetime, reschedules off
/// each fresh token, refreshes on demand for the 401 hook, and reports a plane
/// refusal (revoked machine) as a failed tick without mutating state.
/// </summary>
public class MachineTokenRefresherTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-29T00:00:00+00:00");

    private static MachineCredentialFile Initial() =>
        new("m-1", "https://plane.example.com", "access-0", Start + TimeSpan.FromHours(1), "refresh-0", Start + TimeSpan.FromDays(90));

    /// <summary>A refresh delegate that hands out access-1, access-2, … each with a fresh 1h TTL.</summary>
    private static Func<string, CancellationToken, Task<RefreshResponse?>> SequentialRefresh(
        FakeTimeProvider clock, ConcurrentQueue<int> counter)
    {
        return (_, _) =>
        {
            var n = counter.Count + 1;
            counter.Enqueue(n);
            return Task.FromResult<RefreshResponse?>(
                new RefreshResponse($"access-{n}", clock.GetUtcNow() + TimeSpan.FromHours(1)));
        };
    }

    [Fact]
    public async Task Timer_refreshes_at_half_life_and_reschedules_off_the_new_token()
    {
        var clock = new FakeTimeProvider(Start);
        var counter = new ConcurrentQueue<int>();
        var persisted = new ConcurrentQueue<MachineCredentialFile>();

        await using var refresher = new MachineTokenRefresher(
            Initial(), SequentialRefresh(clock, counter), persisted.Enqueue, clock);

        Assert.Equal("access-0", refresher.CurrentAccessToken); // the enrolled token, before any refresh
        refresher.Start();

        // 1h TTL → first proactive refresh fires at the 30-minute half-life.
        Assert.True(await AdvanceUntilAsync(clock, () => refresher.CurrentAccessToken == "access-1"),
            "the half-life refresh never fired");
        // Rescheduled off the NEW token's own 1h TTL → a second refresh ~30min later.
        Assert.True(await AdvanceUntilAsync(clock, () => refresher.CurrentAccessToken == "access-2"),
            "the refresher did not reschedule off the refreshed token");

        Assert.Contains(persisted, p => p.AccessToken == "access-1"); // every refresh is persisted (§13)
        Assert.Contains(persisted, p => p.AccessToken == "access-2");
    }

    [Fact]
    public async Task RefreshOnceAsync_swaps_the_token_immediately_and_persists()
    {
        // The 401-hook path: no timer, an explicit refresh (as the channel makes
        // after an auth-rejected reconnect). The provider must present the new token.
        var clock = new FakeTimeProvider(Start);
        var counter = new ConcurrentQueue<int>();
        var persisted = new ConcurrentQueue<MachineCredentialFile>();

        await using var refresher = new MachineTokenRefresher(
            Initial(), SequentialRefresh(clock, counter), persisted.Enqueue, clock);

        Assert.True(await refresher.RefreshOnceAsync(CancellationToken.None));
        Assert.Equal("access-1", refresher.CurrentAccessToken);
        Assert.Contains(persisted, p => p.AccessToken == "access-1");
    }

    [Fact]
    public async Task A_plane_refusal_is_a_failed_tick_that_leaves_the_token_untouched()
    {
        // A revoked machine's refresh mints nothing (§13): RefreshAsync returns
        // null → RefreshOnceAsync returns false and does not swap or persist.
        var clock = new FakeTimeProvider(Start);
        var persisted = new ConcurrentQueue<MachineCredentialFile>();

        await using var refresher = new MachineTokenRefresher(
            Initial(), (_, _) => Task.FromResult<RefreshResponse?>(null), persisted.Enqueue, clock);

        Assert.False(await refresher.RefreshOnceAsync(CancellationToken.None));
        Assert.Equal("access-0", refresher.CurrentAccessToken); // unchanged
        Assert.Empty(persisted);
    }

    /// <summary>
    /// Advances the fake clock in steps, letting the loop's <c>Task.Delay</c>
    /// continuation and refresh run and re-arm between steps, until the condition
    /// holds or the wall-clock timeout elapses. Robust to the small window between
    /// a refresh completing and the loop re-registering its next delay.
    /// </summary>
    private static async Task<bool> AdvanceUntilAsync(
        FakeTimeProvider clock, Func<bool> condition, int steps = 30)
    {
        for (var i = 0; i < steps; i++)
        {
            if (condition())
                return true;
            clock.Advance(TimeSpan.FromMinutes(20));
            for (var j = 0; j < 20 && !condition(); j++)
                await Task.Delay(10);
        }
        return condition();
    }
}
