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
}
