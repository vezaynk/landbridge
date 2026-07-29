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

        services.AddSingleton<ForwardRegistry>();

        // The default grant validator is injectable via TryAdd so a host — or a
        // test — that registers its own IGrantValidator first wins. The real
        // control-plane validator (spec §8.3) is a later increment.
        services.TryAddSingleton<IGrantValidator, StaticSecretGrantValidator>();

        return services;
    }
}
