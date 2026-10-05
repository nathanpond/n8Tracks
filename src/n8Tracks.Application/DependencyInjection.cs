using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application;

/// <summary>Registers the application-service layer: the one place business rules live.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SetupCompletion>();
        services.AddScoped<SetupService>();
        services.AddSingleton<DummyPasswordHash>();
        services.AddScoped<SessionService>();
        services.AddScoped<AccountService>();
        services.AddScoped<CredentialService>();
        services.AddScoped<CredentialVerifier>();

        return services;
    }
}
