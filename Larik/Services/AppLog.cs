using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Larik.Services;

/// <summary>Severity tag on a diagnostics line. The order drives the fixed-width tags.</summary>
public enum AppLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The app's local diagnostics log. Larik ships no telemetry and no crash-reporting SDK, so when a
/// run misbehaves this file is the only evidence: one file per launch, flush-on-line, so the last
/// line survives a process that dies inside WinUI or a provider. It records shapes (lengths, row
/// counts, durations, exception stacks), never SQL text, result values or credentials.
/// </summary>
/// <remarks>
/// Written by both UI and provider work, hence the lock. A read-only or missing state folder
/// switches logging off for the session instead of throwing: diagnostics must never be the reason
/// the app fails to start.
/// </remarks>
public static partial class AppLog
{
    private const int RetainedSessions = 10;
    private const int RetainedDays = 14;
    private const int MaxEntryLength = 16_384;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const string SessionMarkerFileName = "session.marker";

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string _sessionStamp = string.Empty;
    private static int _fileIndex;
    private static bool _ended;

    /// <summary>Folder holding the session logs; shown in Settings and safe to open.</summary>
    public static string DirectoryPath => Path.Combine(LarikPaths.StateDirectory, "logs");

    /// <summary>This session's log file, or null when logging is unavailable this run.</summary>
    public static string? CurrentFilePath { get; private set; }

    /// <summary>
    /// Opens a fresh session log, reports whether the previous run ended cleanly, and records the
    /// environment once. Call as early as possible: a crash before this point leaves no file.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            var directory = DirectoryPath;
            Directory.CreateDirectory(directory);
            Prune(directory);

            _sessionStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _fileIndex = 1;
            var path = Path.Combine(directory, SessionFileName());
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                // One line per event, flushed immediately: a crash inside WinUI or a provider must
                // not take the last few breadcrumbs with it.
                AutoFlush = true,
                NewLine = "\n",
            };

            lock (Gate)
            {
                _writer = writer;
                CurrentFilePath = path;
            }

            WriteHeader();
            ReportPreviousSession(directory);
            WriteSessionMarker(directory);
        }
        catch
        {
            // No state folder, no permission, no room: run without a log rather than not at all.
            _writer = null;
            CurrentFilePath = null;
        }
    }

    /// <summary>True when this run has a writable log file.</summary>
    public static bool IsEnabled => CurrentFilePath is not null;

    /// <summary>
    /// Logs the end of the session and removes the crash marker. Idempotent: a window close and
    /// the process-exit hook both call it.
    /// </summary>
    public static void Shutdown(string reason)
    {
        lock (Gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
        }

        Info("session", $"ending: {reason}");
        TryDeleteMarker();

        lock (Gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // The file is already flushed line by line; a failed close changes nothing.
            }

            _writer = null;
        }
    }

    /// <summary>Pushes buffered lines to disk; used right before the process is allowed to die.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // Nothing useful to do while handling a crash.
            }
        }
    }

    public static void Info(string area, string message) =>
        Write(AppLogLevel.Info, area, message, exception: null);

    public static void Warning(string area, string message, Exception? exception = null) =>
        Write(AppLogLevel.Warning, area, message, exception);

    public static void Error(string area, string message, Exception? exception = null) =>
        Write(AppLogLevel.Error, area, message, exception);

    public static void Write(AppLogLevel level, string area, string message, Exception? exception = null)
    {
        if (Volatile.Read(ref _writer) is null)
        {
            return;
        }

        var entry = BuildEntry(level, area, message, exception);
        lock (Gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                RollIfNeeded();
                _writer?.WriteLine(entry);
            }
            catch
            {
                // A log that cannot be written is not worth a second exception.
                _writer = null;
            }
        }
    }

    private static string SessionFileName() => _fileIndex <= 1
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"larik-{_sessionStamp}-{Environment.ProcessId}.log")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"larik-{_sessionStamp}-{Environment.ProcessId}-{_fileIndex}.log");

    /// <summary>
    /// Starts a new file when the current one reaches <see cref="MaxFileBytes"/>, so a long or
    /// looping session cannot grow a single file forever. Called under <see cref="Gate"/>.
    /// </summary>
    private static void RollIfNeeded()
    {
        if (_writer is not { } previous || previous.BaseStream.Length < MaxFileBytes)
        {
            return;
        }

        try
        {
            _fileIndex++;
            var path = Path.Combine(DirectoryPath, SessionFileName());
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            _writer = writer;
            CurrentFilePath = path;
            previous.Dispose();

            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            writer.WriteLine(
                $"{stamp} [INFO ] session: continuing in {Path.GetFileName(path)} (the previous file reached its 4 MB limit)");

            // Keeps rotated files inside the same retention budget even during a runaway session.
            Prune(DirectoryPath);
        }
        catch
        {
            // Keep writing to the current file if the next one cannot be opened.
        }
    }

    private static void WriteHeader()
    {
        Info("session", $"starting Larik {AppInfo.VersionText} ({BuildFlavor()}, {(LarikPaths.IsPackaged ? "packaged" : "portable")})");
        Info("session", $"os: {RuntimeInformation.OSDescription}; arch: {RuntimeInformation.ProcessArchitecture}; runtime: {RuntimeInformation.FrameworkDescription}");
        Info("session", $"state: {LarikPaths.StateDirectory}");
    }

    private static string BuildFlavor()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    /// <summary>
    /// A marker file that only a clean exit deletes. Its presence at startup means the previous run
    /// was killed or crashed; the previous session's log file then ends at the last breadcrumb,
    /// which is what makes a native crash (no managed handler) diagnosable.
    /// </summary>
    private static void ReportPreviousSession(string directory)
    {
        var marker = Path.Combine(directory, SessionMarkerFileName);
        if (!File.Exists(marker))
        {
            return;
        }

        string previous;
        try
        {
            previous = File.ReadAllText(marker).Trim();
        }
        catch
        {
            previous = string.Empty;
        }

        Warning(
            "session",
            string.IsNullOrWhiteSpace(previous)
                ? "the previous session did not shut down cleanly"
                : $"the previous session did not shut down cleanly ({previous})");

        TryDeleteMarker();
    }

    private static void WriteSessionMarker(string directory)
    {
        var marker = Path.Combine(directory, SessionMarkerFileName);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"pid {Environment.ProcessId}, started {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}, version {AppInfo.Version}");
        try
        {
            File.WriteAllText(marker, text);
        }
        catch
        {
            // Without the marker the log is still complete; only the clean-exit hint is lost.
        }
    }

    private static void TryDeleteMarker()
    {
        try
        {
            File.Delete(Path.Combine(DirectoryPath, SessionMarkerFileName));
        }
        catch
        {
            // Best effort: a stale marker costs one extra "did not shut down cleanly" line.
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetainedDays);
            var files = new DirectoryInfo(directory)
                .GetFiles("larik-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            for (var index = 0; index < files.Count; index++)
            {
                if (index < RetainedSessions && files[index].LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                try
                {
                    files[index].Delete();
                }
                catch
                {
                    // A file another copy of the app holds open can wait for the next launch.
                }
            }
        }
        catch
        {
            // Pruning is housekeeping; failing it never disables the new log.
        }
    }

    private static string BuildEntry(
        AppLogLevel level,
        string area,
        string message,
        Exception? exception)
    {
        var builder = new StringBuilder(192);
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [")
            .Append(Tag(level))
            .Append("] ")
            .Append(area)
            .Append(": ")
            .Append(Cap(Scrub(message)));

        if (exception is not null)
        {
            builder.Append('\n')
                .Append("  ")
                .Append(Cap(Scrub(exception.ToString())).Replace("\r\n", "\n").Replace("\n", "\n  "));
        }

        return builder.ToString();
    }

    private static string Tag(AppLogLevel level) => level switch
    {
        AppLogLevel.Warning => "WARN ",
        AppLogLevel.Error => "ERROR",
        _ => "INFO ",
    };

    private static string Cap(string text) => text.Length <= MaxEntryLength
        ? text
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{text[..MaxEntryLength]} … ({text.Length:N0} characters truncated)");

    /// <summary>
    /// Provider and driver exceptions sometimes echo a connection string. The log is local, but a
    /// file a user may paste into an issue should never carry a password or token.
    /// </summary>
    private static string Scrub(string text) => SecretValue().Replace(text, "$1$2***");

    [GeneratedRegex(
        @"(?i)\b(password|pwd|passwd|secret|token|api[_-]?key|access[_-]?key|account[_-]?key)\b(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^;\s,}]+)")]
    private static partial Regex SecretValue();
}
