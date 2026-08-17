using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docket.Relay;

/// <summary>DI wiring for the relay splice core.</summary>
public static class RelayServiceCollectionExtensions
{
    public static IServiceCollection AddRelay(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RelayOptions>()
            .Bind(configuration.GetSection(RelayOptions.SectionName));
        services.AddOptions<StaticSecretGrantValidatorOptions>()
            .Bind(configuration.GetSection(StaticSecretGrantValidatorOptions.SectionName));
        services.AddOptions<ControlPlaneGrantValidatorOptions>()
            .Bind(configuration.GetSection(ControlPlaneGrantValidatorOptions.SectionName));

        services.AddSingleton<ForwardRegistry>();

        // Grant policy (spec §8.3): when a control-plane URL is configured, the
        // real validator asks the plane over HTTP; otherwise the fail-closed
        // static-secret stub stands. Both go in via TryAdd so a host — or a test —
        // that registered its own IGrantValidator first still wins.
        var controlPlaneUrl = configuration
            .GetSection(ControlPlaneGrantValidatorOptions.SectionName)["Url"];
        if (!string.IsNullOrWhiteSpace(controlPlaneUrl))
        {
            services.AddHttpClient(ControlPlaneGrantValidator.HttpClientName);
            services.TryAddSingleton<IGrantValidator, ControlPlaneGrantValidator>();

            // §9.10 byte accounting, on the same plane and the same bearer. One instance serves
            // as both the reporter the splice pumps count into and the hosted service that
            // flushes it, so the timer belongs to the host's lifetime and shutdown gets a final
            // report. Registered only alongside a real plane, for the same reason the validator
            // is: there is nowhere to report otherwise.
            services.TryAddSingleton<ControlPlaneForwardUsageReporter>();
            services.TryAddSingleton<IForwardUsageReporter>(
                sp => sp.GetRequiredService<ControlPlaneForwardUsageReporter>());
            services.AddHostedService(sp => sp.GetRequiredService<ControlPlaneForwardUsageReporter>());
        }
        else
        {
            services.TryAddSingleton<IGrantValidator, StaticSecretGrantValidator>();
            // No plane to report to: count nothing rather than accumulate forever.
            services.TryAddSingleton<IForwardUsageReporter, NoOpForwardUsageReporter>();
        }

        return services;
    }
}
