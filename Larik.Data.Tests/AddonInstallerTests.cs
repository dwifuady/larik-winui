using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Larik.Data;
using Larik.Data.Addons;
using Xunit;

namespace Larik.Data.Tests;

public sealed class AddonInstallerTests
{
    private const string Manifest = """
        {
          "manifestVersion": 1,
          "id": "larik.test",
          "version": "1.0.0",
          "publisher": "Larik",
          "contract": ">=1.0 <2.0",
          "providerIds": ["test"],
          "entryAssembly": "Test.dll"
        }
        """;

    [Fact]
    public async Task Index_client_reads_a_local_index_and_filters_entries()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "index.json");
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "channel": "all",
                  "addons": [
                    { "id": "larik.ok", "name": "OK", "publisher": "Larik", "summary": "ok", "version": "1.0.0", "contract": ">=1.0 <2.0", "minAppVersion": "0.1.0", "platforms": ["win-x64"], "providerIds": ["ok"], "url": "https://example.test/ok.zip", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "license": "MIT", "homepage": "https://example.test" },
                    { "id": "larik.future", "name": "Future", "publisher": "Larik", "summary": "future", "version": "1.0.0", "contract": ">=2.0 <3.0", "minAppVersion": "0.1.0", "platforms": ["win-x64"], "providerIds": ["future"], "url": "https://example.test/future.zip", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "license": "MIT", "homepage": "https://example.test" },
                    { "id": "larik.arm", "name": "ARM", "publisher": "Larik", "summary": "arm", "version": "1.0.0", "contract": ">=1.0 <2.0", "minAppVersion": "0.1.0", "platforms": ["win-arm64"], "providerIds": ["arm"], "url": "https://example.test/arm.zip", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "license": "MIT", "homepage": "https://example.test" }
                  ]
                }
                """);

            var client = new AddonIndexClient(new HttpClient());
            var index = await client.FetchAsync(path);
            var matches = index.Addons.Where(entry => entry.Matches("0.1.0", "win-x64")).ToList();

            Assert.Single(matches);
            Assert.Equal("larik.ok", matches[0].Id);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Installer_verifies_the_hash_and_writes_the_current_pointer()
    {
        var root = NewDirectory();
        try
        {
            var zip = CreateZip(
                ("larik-addon.json", Manifest),
                ("Test.dll", "not a real assembly"),
                ("LICENSE", "MIT"),
                ("THIRD-PARTY-NOTICES.md", "none"));

            var hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            var entry = Entry(hash);
            var installer = new AddonInstaller(new HttpClient(new StubHandler(zip)));

            var installed = await installer.InstallAsync(entry, root);

            Assert.Equal("larik.test", installed.Id);
            Assert.Equal("1.0.0", installed.Version);
            Assert.True(File.Exists(Path.Combine(root, "larik.test", "1.0.0", "Test.dll")));
            Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(root, "larik.test", "current.json")));

            var listed = Assert.Single(installer.ListInstalled(root));
            Assert.True(listed.Enabled);

            installer.SetEnabled(root, "larik.test", enabled: false);
            Assert.False(Assert.Single(installer.ListInstalled(root)).Enabled);

            installer.SetEnabled(root, "larik.test", enabled: true);
            Assert.True(Assert.Single(installer.ListInstalled(root)).Enabled);

            installer.Remove(root, "larik.test");
            Assert.Empty(installer.ListInstalled(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Installer_rejects_a_hash_mismatch_without_installing()
    {
        var root = NewDirectory();
        try
        {
            var zip = CreateZip(
                ("larik-addon.json", Manifest),
                ("Test.dll", "not a real assembly"));
            var installer = new AddonInstaller(new HttpClient(new StubHandler(zip)));

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => installer.InstallAsync(Entry(new string('0', 64)), root));

            Assert.Contains("verification", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "larik.test")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Installer_rejects_a_zip_entry_outside_the_addon_folder()
    {
        var root = NewDirectory();
        try
        {
            var zip = CreateZip(
                ("larik-addon.json", Manifest),
                ("Test.dll", "not a real assembly"),
                ("../evil.txt", "escape"));
            var zipPath = Path.Combine(root, "addon.zip");
            File.WriteAllBytes(zipPath, zip);
            var installer = new AddonInstaller(new HttpClient());

            await Assert.ThrowsAsync<InvalidDataException>(
                () => installer.InstallZipAsync(zipPath, Path.Combine(root, "plugins")));

            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "plugins", "larik.test")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Version_comparison_pads_short_versions()
    {
        Assert.True(AddonVersions.IsAtLeast("1.0.0", "1.0"));
        Assert.True(AddonVersions.IsNewer("1.10.0", "1.9.0"));
        Assert.False(AddonVersions.IsNewer("1.0.0", "1.0"));
    }

    private static AddonIndexEntry Entry(string hash) => new()
    {
        Id = "larik.test",
        Name = "Test add-on",
        Publisher = "Larik",
        Summary = "Test",
        Version = "1.0.0",
        Contract = ">=1.0 <2.0",
        MinAppVersion = "0.1.0",
        Platforms = ["win-x64"],
        ProviderIds = ["test"],
        Url = "https://example.test/addon.zip",
        Sha256 = hash,
        SizeBytes = 1,
        License = "MIT",
        Homepage = "https://example.test",
    };

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "larik-addon-install", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

    private sealed class StubHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
    }
}

