using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// Publishes this assembly's user secrets into the process environment before
/// any test runs. The real-harness facts read <c>Environment.GetEnvironmentVariable</c>
/// and spawn CLIs that inherit that environment — user secrets are not env vars
/// unless something copies them. Existing process env (CI job secrets) wins.
/// </summary>
internal static class UserSecretsEnv
{
    [ModuleInitializer]
    internal static void Publish()
    {
        IConfigurationRoot config;
        try
        {
            config = new ConfigurationBuilder()
                .AddUserSecrets(typeof(UserSecretsEnv).Assembly, optional: true)
                .Build();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var (key, value) in config.AsEnumerable())
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                continue;
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 })
                continue;
            Environment.SetEnvironmentVariable(key, value);
        }

        Alias("ANTHROPIC_KEY", "ANTHROPIC_API_KEY");
        Alias("OPENAI_KEY", "CODEX_API_KEY");
        Alias("OPENAI_API_KEY", "CODEX_API_KEY");
        Alias("XAI_KEY", "XAI_API_KEY");
    }

    private static void Alias(string from, string to)
    {
        if (Environment.GetEnvironmentVariable(to) is { Length: > 0 })
            return;
        if (Environment.GetEnvironmentVariable(from) is { Length: > 0 } value)
            Environment.SetEnvironmentVariable(to, value);
    }
}
