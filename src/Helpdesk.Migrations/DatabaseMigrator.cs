using System.Reflection;
using DbUp;
using DbUp.Engine.Output;

namespace Helpdesk.Migrations;

/// <summary>
/// Applies the versioned SQL scripts embedded in this assembly (the Scripts folder).
/// Scripts run once each, in name order, and are recorded in the SchemaVersions table.
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>
    /// A script whose name contains this runs outside a transaction. Use it only for statements
    /// SQL Server refuses to run inside one, such as creating a full-text index.
    /// </summary>
    public const string NoTransactionMarker = "_NoTransaction";

    public static void Migrate(string connectionString, IUpgradeLog? log = null)
    {
        log ??= new ConsoleUpgradeLog();

        EnsureDatabase.For.SqlDatabase(connectionString, log);

        // Each script gets its own engine so that most can run in a transaction (all or nothing)
        // while the few that cannot are still applied in their place in the order.
        foreach (var scriptName in ScriptNames())
        {
            var builder = DeployChanges.To
                .SqlDatabase(connectionString)
                .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name == scriptName)
                .LogTo(log);

            builder = scriptName.Contains(NoTransactionMarker, StringComparison.Ordinal)
                ? builder.WithoutTransaction()
                : builder.WithTransactionPerScript();

            var upgrader = builder.Build();
            if (!upgrader.IsUpgradeRequired())
            {
                continue;
            }

            var result = upgrader.PerformUpgrade();
            if (!result.Successful)
            {
                throw new InvalidOperationException(
                    $"Database migration failed in script '{result.ErrorScript?.Name ?? scriptName}'.", result.Error);
            }
        }
    }

    public static IReadOnlyList<string> ScriptNames() =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
}
