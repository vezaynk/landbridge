using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace Docket.Verifier;

/// <summary>
/// The <c>docket-verifier</c> entrypoint: the reference automated verifier
/// (spec §5 verifier credential, §6 verdicts, §10 "the verifier posts to Docket,
/// not the reverse"). It closes the zero-human task lifecycle — an automated task
/// reaches <c>verifying</c>, this console polls the plane's verifier webhook,
/// applies its own accept policy (§7) to the reported result reference, and posts
/// a verdict, with no human in the loop.
///
/// <para>Deliberately tiny and dependency-free: it references no other Docket
/// project and speaks only plain HTTP + JSON, so a real CI-driven verifier can
/// copy this shape. It holds exactly one credential (its bearer token, from the
/// environment) and treats <c>completionCriteria</c>/<c>resultReference</c> as
/// opaque strings — interpretation is its policy, never the plane's.</para>
/// </summary>
public static class Program
{
    /// <summary>
    /// A wrong token will not heal itself, so we stop pretending it might: after
    /// this many <b>consecutive</b> auth-refused polls, exit nonzero for the
    /// operator to rotate the credential and restart. A single transient blip
    /// resets the count.
    /// </summary>
    public const int MaxConsecutiveAuthFailures = 3;

    /// <summary>Exit code when the loop gives up on a persistently-refused credential.</summary>
    public const int AuthFailureExitCode = 3;

    /// <summary>Exit code for a startup config/usage error (nothing ran).</summary>
    public const int ConfigErrorExitCode = 2;

    public static async Task<int> Main(string[] args)
    {
        VerifierConfig config;
        try
        {
            config = VerifierConfig.Parse(args, Environment.GetEnvironmentVariable);
        }
        catch (VerifierConfigException e)
        {
            foreach (var problem in e.Errors)
                Console.Error.WriteLine($"error: {problem}");
            Console.Error.WriteLine(VerifierConfig.Usage);
            return ConfigErrorExitCode;
        }

        using var http = new HttpClient { BaseAddress = config.PlaneUrl, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        var client = new VerifierClient(http);
        var policy = new VerdictPolicy(config.AcceptPattern);

        // Exit only on signal (like docketd) or the auth-failure give-up below.
        using var shutdown = new CancellationTokenSource();
        using var sigint = PosixSignalRegistration.Create(
            PosixSignal.SIGINT, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
        using var sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });

        Console.WriteLine(
            $"docket-verifier up: plane={config.PlaneUrl} " +
            $"interval={config.Interval.TotalSeconds:0}s accept-pattern=/{config.AcceptPatternSource}/");

        return await RunAsync(client, policy, VerifierLog.Console, config.Interval, TimeProvider.System, shutdown.Token);
    }

    /// <summary>
    /// The poll loop, factored out of <see cref="Main"/> so it is testable with a
    /// <see cref="TimeProvider"/> and a real plane host. Runs a tick immediately,
    /// then every <paramref name="interval"/> — the <b>same</b> interval whether
    /// the last tick succeeded or the plane was unreachable, so a downed plane
    /// draws steady retries, never a backoff storm. Returns 0 on a clean
    /// signalled shutdown, or <see cref="AuthFailureExitCode"/> when the
    /// credential is persistently refused.
    /// </summary>
    public static async Task<int> RunAsync(
        VerifierClient client, VerdictPolicy policy, VerifierLog log,
        TimeSpan interval, TimeProvider clock, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, clock);
        var consecutiveAuthFailures = 0;
        try
        {
            do
            {
                var tick = await VerifierLoop.RunTickAsync(client, policy, log, ct);
                if (tick.Status == TickStatus.Unauthorized)
                {
                    if (++consecutiveAuthFailures >= MaxConsecutiveAuthFailures)
                    {
                        log.Error(
                            $"{consecutiveAuthFailures} consecutive auth failures — the verifier " +
                            $"credential is not valid; exiting. Rotate ${VerifierConfig.TokenEnvVar} and restart.");
                        return AuthFailureExitCode;
                    }
                }
                else
                {
                    consecutiveAuthFailures = 0;
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // SIGINT/SIGTERM cancelled the token; fall through to a clean exit.
        }

        Console.WriteLine("docket-verifier shutting down");
        return 0;
    }
}
