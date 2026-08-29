namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// The grok model pin is load-bearing. LANDBRIDGE_GROK_MODEL used to only
/// feed our C# property (#220); --model on argv is ignored by agent stdio
/// (#222). GROK_DEFAULT_MODEL on the spawn env is what grok reads.
/// </summary>
public sealed class RealHarnessProfilesTests
{
    [Fact]
    public void Grok_pins_the_model_on_env_spawn_and_config_options()
    {
        var profile = RealHarnessProfiles.Grok("/usr/bin/grok");
        Assert.Equal(
            ["/usr/bin/grok", "--model", RealHarnessProfiles.GrokModel, "agent", "stdio"],
            profile.AcpSpawn);
        Assert.NotNull(profile.Env);
        Assert.Equal(RealHarnessProfiles.GrokModel, profile.Env["GROK_DEFAULT_MODEL"]);
        Assert.NotNull(profile.ConfigOptions);
        Assert.Equal(RealHarnessProfiles.GrokModel, profile.ConfigOptions["model"]);
    }
}
