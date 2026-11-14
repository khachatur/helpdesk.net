using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.KnowledgeBase;
using Helpdesk.Infrastructure.Teams;
using Helpdesk.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TicketStore>();
        services.AddSingleton<TeamStore>();
        services.AddSingleton<ArticleStore>();
        services.AddSingleton<EmailHtmlSanitizer>();

        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<EmailStore>();
        services.AddSingleton<EmailIngestionService>();
        services.AddSingleton<ReplySender>();
        services.TryAddSingleton<IReplyQueue, NoReplyQueue>();
        services.AddSingleton<IMailbox>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<EmailOptions>>();
            if (!options.Value.IsEnabled)
            {
                return new NoMailbox();
            }

            var missing = new[]
                {
                    (Name: nameof(EmailOptions.Mailbox), options.Value.Mailbox),
                    (Name: nameof(EmailOptions.TenantId), options.Value.TenantId),
                    (Name: nameof(EmailOptions.ClientId), options.Value.ClientId),
                    (Name: nameof(EmailOptions.ClientSecret), options.Value.ClientSecret),
                }
                .Where(setting => string.IsNullOrWhiteSpace(setting.Item2))
                .Select(setting => "Email:" + setting.Name)
                .ToList();

            return missing.Count == 0
                ? new GraphMailbox(options)
                : throw new InvalidOperationException(
                    "Email:Mode is Graph but these settings are missing: " + string.Join(", ", missing) + ".");
        });

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
