namespace Docket.Verifier;

/// <summary>
/// The verifier's plain-console logging seam. Ordinary verdict lines go to
/// stdout; warnings and the loud auth/misprovision errors go to stderr, so an
/// operator's log pipeline can split them. Injected everywhere (rather than
/// calling <see cref="Console"/> directly) so tests can capture the output.
/// </summary>
public sealed class VerifierLog(TextWriter output, TextWriter error)
{
    public void Info(string message) => output.WriteLine(message);
    public void Warn(string message) => error.WriteLine($"warn: {message}");
    public void Error(string message) => error.WriteLine($"error: {message}");

    /// <summary>The real console sink used by <see cref="Program"/>.</summary>
    public static VerifierLog Console { get; } = new(System.Console.Out, System.Console.Error);
}
