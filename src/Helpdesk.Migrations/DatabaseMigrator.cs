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
    public static void Migrate(string connectionString, IUpgradeLog? log = null)
    {
        log ??= new ConsoleUpgradeLog();

        EnsureDatabase.For.SqlDatabase(connectionString, log);

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .WithTransactionPerScript()
            .LogTo(log)
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed in script '{result.ErrorScript?.Name}'.", result.Error);
        }
    }

    public static IReadOnlyList<string> ScriptNames() =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
}
