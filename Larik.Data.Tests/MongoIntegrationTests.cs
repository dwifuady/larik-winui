using Larik.Data;
using Larik.Data.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Real MongoDB round-trip: connect, insert, select, group, update, delete, and mongosh passthrough.
/// The server is not a build dependency: if nothing answers on the configured host the test is a
/// no-op, so CI and offline machines stay green while a live <c>mongod</c> fully exercises the
/// provider. Set <c>LARIK_MONGO_HOST</c> / <c>LARIK_MONGO_PORT</c> to point elsewhere.
/// </summary>
public sealed class MongoIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 27017;
    private const string Database = "larik_test";

    private static readonly string ConfiguredHost = Environment.GetEnvironmentVariable("LARIK_MONGO_HOST") ?? Host;
    private static readonly int ConfiguredPort = int.TryParse(Environment.GetEnvironmentVariable("LARIK_MONGO_PORT"), out var p) ? p : Port;

    private static bool ServerReachable(out MongoClient client)
    {
        client = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress(ConfiguredHost, ConfiguredPort),
            ServerSelectionTimeout = TimeSpan.FromSeconds(2),
            DirectConnection = true,
        });

        try
        {
            client.GetDatabase("admin").RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("ping", 1)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MongoConnection NewConnection() =>
        (MongoConnection)new MongoProvider().CreateConnection(new ConnectionProfile
        {
            ProviderId = "mongodb",
            Values = { ["host"] = ConfiguredHost, ["port"] = ConfiguredPort.ToString(), ["database"] = Database },
        });

    private static async Task<MongoConnection> OpenAndCleanAsync()
    {
        // Deliberately not opened here: the first ExecuteBatchAsync must self-open, which is the
        // path the app actually takes (a tab runs before any explicit connect).
        var connection = NewConnection();
        var db = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress(ConfiguredHost, ConfiguredPort),
            ServerSelectionTimeout = TimeSpan.FromSeconds(2),
            DirectConnection = true,
        }).GetDatabase(Database);
        await db.DropCollectionAsync("seed");
        return connection;
    }

    [Fact]
    public async Task End_to_end_round_trip()
    {
        if (!ServerReachable(out _))
        {
            return; // mongod is not running; this integration path is skipped.
        }

        await using var connection = await OpenAndCleanAsync();

        var inserted = await connection.ExecuteBatchAsync(
            "INSERT INTO [seed] ([name], [age]) VALUES ('alice', 30), ('bob', 25), ('carol', 40);",
            Database, QueryExecutionOptions.Default);
        Assert.All(inserted, r => Assert.False(r.IsError));
        Assert.Equal(3, inserted[0].RecordsAffected);

        // SELECT with a WHERE, ORDER BY and projected columns.
        var selected = await connection.ExecuteBatchAsync(
            "SELECT [name], [age] FROM [seed] WHERE [age] >= 30 ORDER BY [age] DESC;",
            Database, QueryExecutionOptions.Default);
        Assert.False(selected[0].IsError);
        Assert.Equal(["carol", "alice"], selected[0].Rows.Select(r => r[0]).ToList());
        Assert.NotNull(selected[0].GeneratedQuery);

        // GROUP BY with an aggregate.
        var grouped = await connection.ExecuteBatchAsync(
            "SELECT [age], COUNT(*) AS [n] FROM [seed] GROUP BY [age] ORDER BY [age];",
            Database, QueryExecutionOptions.Default);
        Assert.False(grouped[0].IsError);
        Assert.Equal(3, grouped[0].Rows.Count);
        Assert.Equal("1", grouped[0].Rows[0][1]);

        // UPDATE.
        var updated = await connection.ExecuteBatchAsync(
            "UPDATE [seed] SET [age] = 31 WHERE [name] = 'alice';",
            Database, QueryExecutionOptions.Default);
        Assert.False(updated[0].IsError);
        Assert.Equal(1, updated[0].RecordsAffected);

        // DELETE.
        var deleted = await connection.ExecuteBatchAsync(
            "DELETE FROM [seed] WHERE [name] = 'bob';",
            Database, QueryExecutionOptions.Default);
        Assert.False(deleted[0].IsError);
        Assert.Equal(1, deleted[0].RecordsAffected);

        // mongosh passthrough.
        var shell = await connection.ExecuteBatchAsync(
            "db.seed.count({});",
            Database, QueryExecutionOptions.Default);
        Assert.False(shell[0].IsError);
        Assert.Equal("2", shell[0].Rows[0][0]);

        // Sampled schema exposes collections and inferred columns.
        var schema = await connection.GetSchemaAsync(Database);
        var seed = schema.Single(t => t.Name == "seed");
        Assert.Equal(["_id", "name", "age"], seed.Columns);

        // mongosh insertMany + find (BSON documents are double-quoted JSON).
        var shellInsert = await connection.ExecuteBatchAsync(
            "db.seed.insertMany([{ \"name\": \"dave\", \"age\": 22 }]);",
            Database, QueryExecutionOptions.Default);
        Assert.False(shellInsert[0].IsError);
        Assert.Equal(1, shellInsert[0].RecordsAffected);

        var shellFind = await connection.ExecuteBatchAsync(
            "db.seed.find({ \"name\": \"dave\" });",
            Database, QueryExecutionOptions.Default);
        Assert.False(shellFind[0].IsError);
        Assert.Contains("name", shellFind[0].Columns.Select(c => c.Name));
        Assert.Single(shellFind[0].Rows);
    }
}