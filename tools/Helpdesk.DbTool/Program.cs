using Helpdesk.Migrations;
using Microsoft.Data.SqlClient;

// Database chores that should not need the whole application running.
//
//   dotnet run --project tools/Helpdesk.DbTool -- migrate "<connection string>"
//       Applies pending migrations. This is the deployment step: run it before starting the new
//       version of the API. Production does not migrate at startup.
//
//   dotnet run --project tools/Helpdesk.DbTool -- drop "<connection string>"
//       Drops a throwaway database so a test run starts clean. Refuses any database whose name
//       does not end in _Test or _E2E, so it cannot be pointed at real data by mistake.
//
// The connection string can also come from the HELPDESK_CONNECTION environment variable.

var command = args.ElementAtOrDefault(0);
var connectionString = args.ElementAtOrDefault(1) ?? Environment.GetEnvironmentVariable("HELPDESK_CONNECTION");

if (command is not ("migrate" or "drop") || string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Usage: Helpdesk.DbTool <migrate|drop> \"<connection string>\"");
    return 2;
}

var builder = new SqlConnectionStringBuilder(connectionString);
var database = builder.InitialCatalog;
if (string.IsNullOrWhiteSpace(database))
{
    Console.Error.WriteLine("The connection string must name a database.");
    return 2;
}

if (command == "migrate")
{
    DatabaseMigrator.Migrate(connectionString);
    Console.WriteLine($"Database '{database}' is up to date.");
    return 0;
}

if (!database.EndsWith("_Test", StringComparison.OrdinalIgnoreCase) && !database.EndsWith("_E2E", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to drop '{database}': only databases named *_Test or *_E2E can be dropped.");
    return 1;
}

builder.InitialCatalog = "master";
await using var connection = new SqlConnection(builder.ConnectionString);
await connection.OpenAsync();

// The name was checked above and is quoted here; it cannot be passed as a parameter.
var quoted = "[" + database.Replace("]", "]]") + "]";
await using var drop = connection.CreateCommand();
drop.CommandText = $"""
    IF DB_ID(@name) IS NOT NULL
    BEGIN
        ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        DROP DATABASE {quoted};
    END
    """;
drop.Parameters.AddWithValue("@name", database);
await drop.ExecuteNonQueryAsync();

Console.WriteLine($"Database '{database}' dropped.");
return 0;
