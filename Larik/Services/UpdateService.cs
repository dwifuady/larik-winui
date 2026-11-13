using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Larik.Services;

/// <summary>How a downloaded update replaces this copy of Larik.</summary>
public enum UpdateInstallKind
{
    /// <summary>The per-user Inno Setup installer replaces an installed copy.</summary>
    Installer,

    /// <summary>The release ZIP replaces a portable folder in place.</summary>
    Portable,
}

/// <summary>One newer release this copy of Larik can download and install itself.</summary>
public sealed record UpdateInfo(
    string Version,
    string AssetName,
    Uri AssetUri,
    long AssetSize,
    Uri? ChecksumsUri,
    UpdateInstallKind InstallKind);

/// <summary>What a check found. Launch checks ignore everything but <see cref="Available"/>.</summary>
public enum UpdateCheckStatus
{
    /// <summary>This build does not update itself (Store or another packaged copy).</summary>
    Unavailable,

    /// <summary>This is the newest published release.</summary>
    UpToDate,

    /// <summary>A newer release with an asset for this PC was found.</summary>
    Available,

    /// <summary>The feed could not be reached or could not be understood.</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null, string? Error = null);

/// <summary>Download progress. <paramref name="TotalBytes"/> is null when the server omits the length.</summary>
public readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>0–100, or null when the total is unknown.</summary>
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
        : null;
}

/// <summary>
/// Checks GitHub Releases for a newer build, downloads it into the app's data folder, verifies the
/// published SHA-256, and starts the install.
///
/// <para>
/// Only the unpackaged GitHub edition updates itself. A packaged build (the Store edition, or a
/// debug run through <c>winapp run</c>) has its files in a read-only package, so updates belong to
/// the package owner; <see cref="CanSelfUpdate"/> turns the whole feature off there rather than
/// offering an installer that cannot do the right thing.
/// </para>
/// </summary>
public sealed class UpdateService
{
    private const string Repository = "dwifuady/larik-winui";
    private const string ReleasesApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string ChecksumsAssetName = "SHA256SUMS.txt";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string UpdatesRoot = Path.Combine(LarikPaths.StateDirectory, "updates");

    /// <summary>True for builds that own their own files and may replace them.</summary>
    public static bool CanSelfUpdate { get; } = DetectSelfUpdate();

    /// <summary>
    /// Returns the newest release when it is newer than this build and has an asset for this
    /// architecture. Network and rate-limit failures become <see cref="UpdateCheckStatus.Failed"/>,
    /// never an exception: an update check must not interrupt a launch.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSelfUpdate)
        {
            return new(UpdateCheckStatus.Unavailable);
        }

        if (!TryGetCurrentVersion(out var current) || CurrentArchitecture() is not { } architecture)
        {
            return new(UpdateCheckStatus.Unavailable);
        }

        try
        {
            // The API call is quick; the download deliberately has no such short timeout because a
            // full release is tens of megabytes.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No release has been published yet; there is nothing to install.
                return new(UpdateCheckStatus.UpToDate);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    UpdateCheckStatus.Failed,
                    Error: $"GitHub answered {(int)response.StatusCode} for the release feed.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var release = await JsonSerializer.DeserializeAsync<ReleaseRecord>(
                stream,
                JsonOptions,
                timeout.Token);

            if (release is null || release.Draft || release.Prerelease ||
                release.Assets is not { Length: > 0 } assets)
            {
                return new(UpdateCheckStatus.UpToDate);
            }

            var tag = release.TagName?.Trim();
            var versionText = tag?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(versionText) ||
                !Version.TryParse(versionText, out var version) ||
                version <= current)
            {
                // Nothing to install. This is also the first check after a successful update, so
                // drop the installer or ZIP the previous version left behind.
                await Task.Run(() => CleanupOldDownloads(keepVersion: null), CancellationToken.None);
                return new(UpdateCheckStatus.UpToDate);
            }

            var kind = IsInstalledCopy() ? UpdateInstallKind.Installer : UpdateInstallKind.Portable;
            var suffix = kind == UpdateInstallKind.Installer
                ? $"-win-{architecture}-setup.exe"
                : $"-win-{architecture}.zip";
            var expectedName = $"Larik-{versionText}{suffix}";

            var asset = assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, expectedName, StringComparison.OrdinalIgnoreCase))
                ?? assets.FirstOrDefault(candidate =>
                    candidate.Name?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true &&
                    candidate.Name.Contains(versionText, StringComparison.OrdinalIgnoreCase));

            if (asset?.Name is not { Length: > 0 } assetName ||
                Path.GetFileName(assetName) != assetName ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var assetUri) ||
                !IsTrustedDownloadUri(assetUri))
            {
                return new(
                    UpdateCheckStatus.Failed,
                    Error: $"Larik {versionText} has no downloadable {architecture} build.");
            }

            var checksumsUrl = assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;
            Uri? checksumsUri = Uri.TryCreate(checksumsUrl, UriKind.Absolute, out var parsedChecksums) &&
                IsTrustedDownloadUri(parsedChecksums)
                    ? parsedChecksums
                    : null;

            if (checksumsUri is null)
            {
                // Fail closed: without the published hash the download is only "whatever GitHub
                // served", which cannot detect a tampered release. Offer the manual download instead.
                return new(
                    UpdateCheckStatus.Failed,
                    Error: $"Larik {versionText} has no SHA256SUMS.txt, so it cannot be verified. " +
                        "Download it manually from GitHub Releases.");
            }

            await Task.Run(() => CleanupOldDownloads(versionText), CancellationToken.None);

            return new(
                UpdateCheckStatus.Available,
                new UpdateInfo(versionText, assetName, assetUri, asset.Size, checksumsUri, kind));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(UpdateCheckStatus.Failed, Error: exception.Message);
        }
    }

    /// <summary>A complete download left by an earlier launch, if there is one.</summary>
    public string? FindDownloadedFile(UpdateInfo update)
    {
        try
        {
            var path = Path.Combine(
                UpdatesRoot,
                SafeDirectoryName(update.Version),
                Path.GetFileName(update.AssetName));

            if (!File.Exists(path))
            {
                return null;
            }

            return update.AssetSize > 0 && new FileInfo(path).Length != update.AssetSize
                ? null
                : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the release asset into the app's data folder and verifies the published
    /// checksum. A complete download from an earlier attempt is reused instead of fetched again.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(UpdatesRoot, SafeDirectoryName(update.Version));
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, Path.GetFileName(update.AssetName));
        var partialPath = filePath + ".part";

        if (File.Exists(filePath))
        {
            var complete = update.AssetSize <= 0 || new FileInfo(filePath).Length == update.AssetSize;
            if (complete && await VerifyAsync(update, filePath, cancellationToken))
            {
                return filePath;
            }

            TryDelete(filePath);
        }

        TryDelete(partialPath);

        try
        {
            using var response = await Http.GetAsync(
                update.AssetUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            if (total is null or <= 0)
            {
                total = update.AssetSize > 0 ? update.AssetSize : null;
            }

            progress?.Report(new UpdateDownloadProgress(0, total));

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new UpdateDownloadProgress(received, total));
                }
            }

            File.Move(partialPath, filePath, overwrite: true);

            if (!await VerifyAsync(update, filePath, cancellationToken))
            {
                TryDelete(filePath);
                throw new InvalidDataException(
                    "The downloaded update failed its integrity check. Try the download again.");
            }

            return filePath;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    /// <summary>
    /// Starts the process that replaces this copy. The caller must exit the app afterwards so the
    /// installer or the portable copy can write to its files.
    /// </summary>
    public async Task InstallAsync(
        UpdateInfo update,
        string downloadedPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(downloadedPath))
        {
            throw new FileNotFoundException("The downloaded update is missing.", downloadedPath);
        }

        if (!await VerifyAsync(update, downloadedPath, cancellationToken))
        {
            throw new InvalidDataException(
                "The downloaded update failed its integrity check. Download it again.");
        }

        if (update.InstallKind == UpdateInstallKind.Installer)
        {
            StartInstaller(downloadedPath);
            return;
        }

        await StartPortableUpdateAsync(downloadedPath, cancellationToken);
    }

    private static void StartInstaller(string installerPath)
    {
        var directory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory;
        var logPath = Path.Combine(directory, "install.log");
        var targetDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            // /DIR keeps a moved or copied install in the folder the user is actually running,
            // instead of falling back to the default path. /RELAUNCH reopens Larik afterwards.
            Arguments = $"/SILENT /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /RELAUNCH=1 /DIR=\"{targetDirectory}\" /LOG=\"{logPath}\"",
            WorkingDirectory = directory,
            UseShellExecute = true,
        };

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Windows didn't start the update installer.");
        }
    }

    /// <summary>
    /// Extracts the portable ZIP next to the download, then starts a small command script that
    /// waits for this process to exit, copies the files over the running folder, and starts Larik
    /// again.
    /// </summary>
    private static async Task StartPortableUpdateAsync(
        string zipPath,
        CancellationToken cancellationToken)
    {
        var updateDirectory = Path.GetDirectoryName(zipPath)!;
        var targetDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));

        EnsureWritable(targetDirectory);

        var stagingDirectory = Path.Combine(updateDirectory, "staging");
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
            () => ZipFile.ExtractToDirectory(zipPath, stagingDirectory, overwriteFiles: true),
            CancellationToken.None);

        if (!File.Exists(Path.Combine(stagingDirectory, "Larik.exe")))
        {
            throw new InvalidDataException("The downloaded ZIP doesn't contain Larik.exe.");
        }

        var scriptPath = Path.Combine(updateDirectory, "apply-update.cmd");
        await File.WriteAllTextAsync(
            scriptPath,
            BuildPortableApplyScript().ReplaceLineEndings("\r\n"),
            Encoding.ASCII,
            cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c \"\"{scriptPath}\"\"",
            WorkingDirectory = updateDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        // Passed through the environment rather than pasted into the script, so a profile or
        // folder name with non-ASCII characters survives the batch file's OEM code page.
        startInfo.Environment["LARIK_UPDATE_PID"] =
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["LARIK_UPDATE_SOURCE"] = stagingDirectory;
        startInfo.Environment["LARIK_UPDATE_TARGET"] = targetDirectory;
        startInfo.Environment["LARIK_UPDATE_EXE"] = Path.Combine(targetDirectory, "Larik.exe");

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Windows didn't start the portable updater.");
        }
    }

    /// <summary>
    /// The script waits for the old process, copies every file with robocopy, and relaunches the
    /// new Larik.exe. Paths arrive as environment variables so the batch file itself stays plain
    /// ASCII and Unicode paths survive.
    /// </summary>
    private static string BuildPortableApplyScript() => """
        @echo off
        setlocal
        :wait_for_larik
        "%SystemRoot%\System32\tasklist.exe" /FI "PID eq %LARIK_UPDATE_PID%" /NH 2>nul | "%SystemRoot%\System32\find.exe" "%LARIK_UPDATE_PID%" >nul
        if not errorlevel 1 (
            "%SystemRoot%\System32\ping.exe" -n 2 127.0.0.1 >nul
            goto wait_for_larik
        )
        "%SystemRoot%\System32\robocopy.exe" "%LARIK_UPDATE_SOURCE%" "%LARIK_UPDATE_TARGET%" /E /IS /IT /R:3 /W:1 /NFL /NDL /NJH /NJS /NP > "%~dp0robocopy.log" 2>&1
        start "" "%LARIK_UPDATE_EXE%"
        endlocal
        """;

    /// <summary>
    /// A portable copy can only update in place when the user can write to the folder it runs
    /// from. Checking first turns a failed copy into a clear message before the app closes.
    /// </summary>
    private static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, $".larik-{Guid.NewGuid():N}.tmp");

        try
        {
            using var _ = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Larik can't write to the folder it runs from, so this portable copy can't update in place. Download the new ZIP from GitHub Releases instead.",
                exception);
        }
    }

    /// <summary>
    /// Checks the SHA-256 published with the release. Every path fails closed: no checksum asset,
    /// an unreachable checksum, a missing entry or a mismatch all block the install. TLS alone is
    /// not an independent check against a compromised release.
    /// </summary>
    private static async Task<bool> VerifyAsync(
        UpdateInfo update,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (update.ChecksumsUri is null)
        {
            return false;
        }

        string checksums;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            checksums = await Http.GetStringAsync(update.ChecksumsUri, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            // A checksum we cannot fetch proves nothing, so the update stays blocked.
            return false;
        }

        var expected = checksums
            .TrimStart('\uFEFF')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts =>
                parts.Length == 2 &&
                parts[1].TrimStart('*').Equals(update.AssetName, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault();

        if (expected is null)
        {
            return false;
        }

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Release assets must come from GitHub over HTTPS. The API's browser URL is normally on
    /// github.com and redirects to githubusercontent.com for the bytes, so those hosts cover the
    /// legitimate flow without accepting an arbitrary download origin.
    /// </summary>
    private static bool IsTrustedDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static void CleanupOldDownloads(string? keepVersion)
    {
        try
        {
            if (!Directory.Exists(UpdatesRoot))
            {
                return;
            }

            var keepDirectory = keepVersion is null ? null : SafeDirectoryName(keepVersion);

            foreach (var directory in Directory.EnumerateDirectories(UpdatesRoot))
            {
                if (keepDirectory is not null &&
                    string.Equals(
                        Path.GetFileName(directory),
                        keepDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // A running portable updater may still hold a file here.
                }
            }
        }
        catch
        {
            // Old downloads are harmless if they can't be cleaned up now.
        }
    }

    private static bool IsInstalledCopy()
    {
        try
        {
            var baseDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AppContext.BaseDirectory));

            // The Inno Setup uninstaller is the strongest signal and also covers installs the user
            // moved or put in a custom folder.
            if (File.Exists(Path.Combine(baseDirectory, "unins000.exe")))
            {
                return true;
            }

            const string uninstallKey =
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E701D625-402C-4E6D-AE74-0758E2C65A41}_is1";

            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(uninstallKey);
                if (key?.GetValue("InstallLocation") is not string location ||
                    string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(location))
                    .Equals(baseDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to the portable path rather than installing a second copy next to a
            // portable one.
        }

        return false;
    }

    private static bool TryGetCurrentVersion(out Version version)
    {
        version = new Version(0, 0);

        if (!AppInfo.HasVersion || !Version.TryParse(AppInfo.Version, out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static string? CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    private static string SafeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Leftovers are retried on the next download or cleaned on launch.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Larik/{(AppInfo.HasVersion ? AppInfo.Version : "unknown")} (+https://github.com/{Repository})");
        return client;
    }

    private static bool DetectSelfUpdate()
    {
#if LARIK_STORE
        // The Store owns package updates; the app never downloads a second copy beside its package.
        return false;
#else
        try
        {
            // A debug run through winapp also has package identity, so this covers it too.
            _ = Windows.ApplicationModel.Package.Current;
            return false;
        }
        catch
        {
            return true;
        }
#endif
    }

    private sealed class ReleaseRecord
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public ReleaseAsset[]? Assets { get; set; }
    }

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
