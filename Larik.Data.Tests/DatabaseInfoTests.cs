using Larik.Data;
using Larik.Data.Demo;
using Xunit;

namespace Larik.Data.Tests;

public sealed class DatabaseInfoTests
{
    [Fact]
    public void Display_order_puts_accessible_databases_first_then_alphabetical()
    {
        DatabaseInfo[] databases =
        [
            new("zeta", IsAccessible: false),
            new("Beta"),
            new("alpha"),
            new("omega", IsAccessible: false),
        ];

        var ordered = DatabaseInfo.InDisplayOrder(databases).Select(database => database.Name).ToArray();

        Assert.Equal(["alpha", "Beta", "omega", "zeta"], ordered);
    }

    [Fact]
    public void Preferred_database_leads_only_its_own_accessibility_group()
    {
        DatabaseInfo[] databases =
        [
            new("zeta", IsAccessible: false),
            new("beta"),
            new("alpha"),
            new("omega", IsAccessible: false),
        ];

        var ordered = DatabaseInfo.InDisplayOrder(databases, preferred: "beta")
            .Select(database => database.Name)
            .ToArray();

        Assert.Equal(["beta", "alpha", "omega", "zeta"], ordered);
    }

    [Fact]
    public async Task Providers_without_access_details_report_every_database_as_accessible()
    {
        var provider = new DemoProvider();
        await using var connection = provider.CreateConnection(new ConnectionProfile { ProviderId = DemoProvider.ProviderId });
        await connection.OpenAsync();

        var databases = await connection.GetDatabaseDetailsAsync();

        Assert.NotEmpty(databases);
        Assert.All(databases, database => Assert.True(database.IsAccessible));
    }
}
