namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// The grok model pin is load-bearing: LANDBRIDGE_GROK_MODEL in the job env
/// used to be a lie (#220). Keep spawn and config_options in lockstep with
/// <see cref="RealHarnessProfiles.GrokModel"/>.
/// </summary>
public sealed class RealHarnessProfilesTests
{
    [Fact]
    public void Grok_pins_the_model_on_spawn_and_config_options()
    {
        var profile = RealHarnessProfiles.Grok("/usr/bin/grok");
        Assert.Equal(
            ["/usr/bin/grok", "--model", RealHarnessProfiles.GrokModel, "agent", "stdio"],
            profile.AcpSpawn);
        Assert.NotNull(profile.ConfigOptions);
        Assert.Equal(RealHarnessProfiles.GrokModel, profile.ConfigOptions["model"]);
    }
}
