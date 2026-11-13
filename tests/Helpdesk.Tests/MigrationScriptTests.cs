using System.Text.RegularExpressions;
using Helpdesk.Migrations;

namespace Helpdesk.Tests;

public partial class MigrationScriptTests
{
    [Fact]
    public void Scripts_are_embedded()
    {
        Assert.NotEmpty(DatabaseMigrator.ScriptNames());
    }

    [Fact]
    public void Script_names_start_with_a_unique_four_digit_number()
    {
        // DbUp runs scripts in name order, so the numeric prefix is what fixes the order.
        var prefixes = DatabaseMigrator.ScriptNames()
            .Select(name =>
            {
                var match = ScriptPrefix().Match(name);
                Assert.True(match.Success, $"'{name}' should be named like Scripts/0001_Name.sql");
                return match.Groups[1].Value;
            })
            .ToList();

        Assert.Equal(prefixes.Count, prefixes.Distinct().Count());
    }

    [GeneratedRegex(@"\.Scripts\.(\d{4})_\w+\.sql$")]
    private static partial Regex ScriptPrefix();
}
