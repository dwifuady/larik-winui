using Larik.Data;
using Larik.Data.Addons;
using Larik.Data.Tests.Fixture;
using Xunit;

namespace Larik.Data.Tests;

public sealed class AddonContractTests
{
    [Theory]
    [InlineData(">=1.0 <2.0")]
    [InlineData(">=1.1 <2.0")]
    [InlineData(">=1.0.0 <1.2")]
    [InlineData(">=1.1.0 <1.1.1")]
    public void Contract_range_admits_the_running_version(string range) =>
        Assert.True(LarikDataContract.IsCompatible(range));

    [Theory]
    [InlineData(">=1.0.0 <1.0.1")]
    [InlineData(">=0.9 <1.0.1")]
    [InlineData(">=1.2 <2.0")]
    [InlineData(">=2.0 <3.0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.0")]
    [InlineData(">=1.0")]
    [InlineData("~1.0")]
    public void Contract_range_rejects_incompatible_or_malformed_values(string? range) =>
        Assert.False(LarikDataContract.IsCompatible(range));

    [Fact]
    public void Manifest_loads_and_validates()
    {
        var directory = NewDirectory();
        try
        {
            var path = WriteManifest(directory, """
                {
                  "manifestVersion": 1,
                  "id": "larik.mongodb",
                  "version": "1.0.0",
                  "contract": ">=1.0 <2.0",
                  "providerIds": ["mongodb"],
                  "entryAssembly": "Larik.Data.MongoDB.dll"
                }
                """);

            Assert.True(AddonManifest.TryLoad(path, out var manifest, out var error), error);
            Assert.NotNull(manifest);
            Assert.Equal("larik.mongodb", manifest!.Id);
            Assert.Equal("mongodb", Assert.Single(manifest.ProviderIds));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Manifest_requires_a_compatible_contract()
    {
        var directory = NewDirectory();
        try
        {
            var path = WriteManifest(directory, """
                {
                  "id": "larik.mongodb",
                  "version": "1.0.0",
                  "contract": ">=2.0 <3.0",
                  "providerIds": ["mongodb"],
                  "entryAssembly": "Larik.Data.MongoDB.dll"
                }
                """);

            Assert.False(AddonManifest.TryLoad(path, out _, out var error));
            Assert.Contains("contract", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Manifest_rejects_an_entry_assembly_outside_the_folder()
    {
        var directory = NewDirectory();
        try
        {
            var path = WriteManifest(directory, """
                {
                  "id": "larik.mongodb",
                  "version": "1.0.0",
                  "contract": ">=1.0 <2.0",
                  "providerIds": ["mongodb"],
                  "entryAssembly": "..\\evil.dll"
                }
                """);

            Assert.False(AddonManifest.TryLoad(path, out _, out var error));
            Assert.Contains("entryAssembly", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Manifest_treats_null_providerIds_as_invalid()
    {
        var directory = NewDirectory();
        try
        {
            var path = WriteManifest(directory, """
                {
                  "id": "larik.mongodb",
                  "version": "1.0.0",
                  "contract": ">=1.0 <2.0",
                  "providerIds": null,
                  "entryAssembly": "Larik.Data.MongoDB.dll"
                }
                """);

            Assert.False(AddonManifest.TryLoad(path, out _, out var error));
            Assert.Contains("providerIds", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Locator_skips_a_disabled_addon_and_reports_a_missing_version()
    {
        var root = NewDirectory();
        try
        {
            var disabled = Path.Combine(root, "larik.disabled");
            Directory.CreateDirectory(disabled);
            File.WriteAllText(Path.Combine(disabled, "current.json"), """{ "version": "1.0.0" }""");
            File.WriteAllText(Path.Combine(disabled, "disabled"), string.Empty);

            var broken = Path.Combine(root, "larik.broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "current.json"), """{ "version": "9.9.9" }""");

            var scan = AddonLocator.Scan(root);

            Assert.Empty(scan.Sources);
            Assert.Single(scan.Errors);
            Assert.Contains("larik.broken", scan.Errors[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locator_treats_a_missing_root_as_empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "larik-addon-tests", Guid.NewGuid().ToString("N"));

        var scan = AddonLocator.Scan(root);

        Assert.Empty(scan.Sources);
        Assert.Empty(scan.Errors);
        Assert.Empty(scan.Contexts);
    }

    [Fact]
    public void Addon_context_loads_a_provider_against_the_host_contract()
    {
        var root = NewDirectory();
        try
        {
            var versionDirectory = Path.Combine(root, "larik.fixture", "1.0.0");
            Directory.CreateDirectory(versionDirectory);

            var assemblyFileName = Path.GetFileName(typeof(FixtureProvider).Assembly.Location);
            File.Copy(
                typeof(FixtureProvider).Assembly.Location,
                Path.Combine(versionDirectory, assemblyFileName));

            File.WriteAllText(
                Path.Combine(versionDirectory, "larik-addon.json"),
                $$"""
                {
                  "id": "larik.fixture",
                  "version": "1.0.0",
                  "contract": ">=1.0 <2.0",
                  "providerIds": ["fixture"],
                  "entryAssembly": "{{assemblyFileName}}"
                }
                """);
            File.WriteAllText(
                Path.Combine(root, "larik.fixture", "current.json"),
                """{ "version": "1.0.0" }""");

            var scan = AddonLocator.Scan(root);

            Assert.Empty(scan.Errors);
            Assert.Equal("larik.fixture 1.0.0", Assert.Single(scan.Sources).Origin);

            DatabaseProviderCatalog.Initialize(scan.Sources, scan.Errors);

            Assert.Contains(DatabaseProviderCatalog.Providers, provider => provider.Id == FixtureProvider.IdValue);
        }
        finally
        {
            // The add-on assembly is loaded into a non-collectible context, so Windows keeps the
            // file locked until the test host exits. Leaving the temp folder is better than failing
            // a passing assertion on cleanup.
            TryDelete(root);
        }
    }

    [Fact]
    public void Catalogue_appends_a_provider_loaded_after_startup()
    {
        var root = NewDirectory();
        try
        {
            var versionDirectory = Path.Combine(root, "larik.fixture", "1.0.0");
            Directory.CreateDirectory(versionDirectory);

            var assemblyFileName = Path.GetFileName(typeof(FixtureProvider).Assembly.Location);
            File.Copy(
                typeof(FixtureProvider).Assembly.Location,
                Path.Combine(versionDirectory, assemblyFileName));

            File.WriteAllText(
                Path.Combine(versionDirectory, "larik-addon.json"),
                $$"""
                {
                  "id": "larik.fixture",
                  "version": "1.0.0",
                  "contract": ">=1.0 <2.0",
                  "providerIds": ["fixture"],
                  "entryAssembly": "{{assemblyFileName}}"
                }
                """);
            File.WriteAllText(
                Path.Combine(root, "larik.fixture", "current.json"),
                """{ "version": "1.0.0" }""");

            DatabaseProviderCatalog.Initialize(Array.Empty<ProviderSource>());
            var scan = AddonLocator.Scan(root);

            var added = DatabaseProviderCatalog.AddSources(scan.Sources);

            Assert.Single(added);
            Assert.Equal(FixtureProvider.IdValue, added[0].Id);
            Assert.Contains(
                DatabaseProviderCatalog.Providers,
                provider => provider.Id == FixtureProvider.IdValue);

            // The id is already loaded, so a second pass must not add a duplicate.
            Assert.Empty(DatabaseProviderCatalog.AddSources(scan.Sources));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string WriteManifest(string directory, string json) =>
        WriteFile(directory, "larik-addon.json", json);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "larik-addon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
