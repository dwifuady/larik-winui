using Larik.Data;
using Larik.Data.SqlServer;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>The provider's declared knobs, checked where they are cheap to check.</summary>
public sealed class SqlServerProviderTests
{
    [Fact]
    public void Declares_a_query_timeout_in_the_advanced_section()
    {
        var setting = Assert.Single(new SqlServerProvider().Settings, item => item.Key == "queryTimeout");

        Assert.Equal(ProviderSettingKind.Number, setting.Kind);
        Assert.True(setting.IsAdvanced);
        Assert.Equal("60", setting.DefaultValue);
    }

    [Fact]
    public void Describes_its_endpoint_from_the_server()
    {
        var profile = new ConnectionProfile { ProviderId = "mssql" };
        profile.Values["server"] = @"(localdb)\MSSQLLocalDB";

        Assert.Equal(
            @"SQL Server · (localdb)\MSSQLLocalDB",
            new SqlServerProvider().DescribeEndpoint(profile));
    }

    [Fact]
    public void Trust_server_certificate_defaults_off_for_new_remote_connections()
    {
        var setting = Assert.Single(
            new SqlServerProvider().Settings,
            item => item.Key == "trustServerCertificate");

        Assert.Equal(ProviderSettingKind.Boolean, setting.Kind);
        Assert.Equal("false", setting.DefaultValue);
    }

    [Fact]
    public void Warns_only_when_a_remote_connection_trusts_any_certificate()
    {
        var provider = new SqlServerProvider();

        var remoteTrusted = new ConnectionProfile { ProviderId = "mssql" };
        remoteTrusted.Values["server"] = "db.internal";
        remoteTrusted.Values["trustServerCertificate"] = "true";
        Assert.NotNull(provider.DescribeSecurityWarning(remoteTrusted));

        var remoteVerified = new ConnectionProfile { ProviderId = "mssql" };
        remoteVerified.Values["server"] = "db.internal";
        Assert.Null(provider.DescribeSecurityWarning(remoteVerified));

        var localTrusted = new ConnectionProfile { ProviderId = "mssql" };
        localTrusted.Values["server"] = @"(localdb)\MSSQLLocalDB";
        localTrusted.Values["trustServerCertificate"] = "true";
        Assert.Null(provider.DescribeSecurityWarning(localTrusted));
    }
}
