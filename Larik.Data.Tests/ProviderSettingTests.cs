using Larik.Data;
using Xunit;

namespace Larik.Data.Tests;

public sealed class ProviderSettingTests
{
    [Fact]
    public void File_kind_is_appended_so_existing_numeric_values_do_not_move()
    {
        // Hosts switch on the numeric value and fall back to a text box for unknown kinds, so a new
        // kind must never take a value an older host already renders as something else.
        Assert.Equal(4, (int)ProviderSettingKind.File);
        Assert.Equal(ProviderSettingKind.Boolean, (ProviderSettingKind)3);
    }

    [Fact]
    public void File_extensions_default_to_empty()
    {
        var setting = new ProviderSetting { Key = "file", Label = "Database file" };

        Assert.Empty(setting.FileExtensions);
    }
}
