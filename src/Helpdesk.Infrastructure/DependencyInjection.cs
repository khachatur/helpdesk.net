using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Helpdesk";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDbConnectionFactory>(_ =>
            new SqlConnectionFactory(configuration.GetRequiredHelpdeskConnectionString()));
        services.AddSingleton<UserQueries>();

        return services;
    }

    public static string GetRequiredHelpdeskConnectionString(this IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. " +
                "Set it with 'dotnet user-secrets' locally or the ConnectionStrings__Helpdesk environment variable.");
        }

        return connectionString;
    }
}
