using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Larik.ViewModels;

/// <summary>
/// Drives the update notice that appears after launch: check once per launch, download in the
/// background, then hand off to the installer or the portable updater. The notice is an InfoBar,
/// so it never blocks what the user is doing.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _updates = new();
    private UpdateInfo? _update;
    private string? _downloadedPath;
    private CancellationTokenSource? _operation;
    private bool _checking;
    private bool _dismissed;

    /// <summary>Whether the notice is showing. Closing it dismisses it for this launch.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial string ActionText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanAct { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionVisibility))]
    public partial bool IsActionVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    public partial bool IsProgressVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity Severity { get; set; } = InfoBarSeverity.Informational;

    /// <summary>XAML cannot bind a bool to Visibility directly; these keep the markup simple.</summary>
    public Visibility ActionVisibility => IsActionVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProgressVisibility => IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Checks GitHub for a newer release. Failures are silent: the app opens normally and the
    /// next launch tries again.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_checking || _dismissed || IsOpen)
        {
            return;
        }

        await CheckCoreAsync(cancellationToken);
    }

    /// <summary>
    /// A check the user asked for from Settings. It clears the per-launch dismissal so closing the
    /// old notice does not block this check, and returns the outcome so the page can say what
    /// happened.
    /// </summary>
    public async Task<UpdateCheckStatus> CheckNowAsync()
    {
        _dismissed = false;

        if (IsOpen && _update is not null)
        {
            return UpdateCheckStatus.Available;
        }

        if (_checking)
        {
            return UpdateCheckStatus.Unavailable;
        }

        var result = await CheckCoreAsync(CancellationToken.None);
        return result.Status;
    }

    /// <summary>Download, or install once the download is complete.</summary>
    public async Task ActAsync()
    {
        if (!IsOpen || !CanAct || _update is null)
        {
            return;
        }

        if (_downloadedPath is null)
        {
            await DownloadAsync();
        }
        else
        {
            await InstallAsync();
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            return;
        }

        _dismissed = true;
        _operation?.Cancel();
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        _checking = true;
        try
        {
            var result = await _updates.CheckAsync(cancellationToken);
            if (result is { Status: UpdateCheckStatus.Available, Update: { } update })
            {
                _update = update;
                _downloadedPath = _updates.FindDownloadedFile(update);

                if (_downloadedPath is not null)
                {
                    ShowReadyToInstall();
                }
                else
                {
                    ShowAvailable();
                }

                IsOpen = true;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            // Cancelled by shutdown; not an update error.
            return new(UpdateCheckStatus.Unavailable);
        }
        catch (Exception exception)
        {
            // A failed check must never interrupt startup.
            AppLog.Warning("update", "update check failed", exception);
            return new(UpdateCheckStatus.Failed, Error: exception.Message);
        }
        finally
        {
            _checking = false;
        }
    }

    private void ShowAvailable()
    {
        if (_update is null)
        {
            return;
        }

        Title = $"Larik {_update.Version} is available";
        var size = FormatSize(_update.AssetSize);
        Message = $"You're on {(AppInfo.HasVersion ? AppInfo.VersionText : "this build")}. Download the update" +
            (size.Length > 0 ? $" ({size})" : "") +
            " now, then install when it's ready.";
        ActionText = "Download";
        CanAct = true;
        IsActionVisible = true;
        IsProgressVisible = false;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "";
        Severity = InfoBarSeverity.Informational;
    }

    private void ShowReadyToInstall()
    {
        if (_update is null)
        {
            return;
        }

        Title = $"Larik {_update.Version} is ready to install";
        Message = _update.InstallKind == UpdateInstallKind.Installer
            ? "Click Install. Larik closes, the installer finishes the update, and Larik reopens."
            : "Click Install. Larik closes, updates this folder, and reopens.";
        ActionText = "Install";
        CanAct = true;
        IsActionVisible = true;
        IsProgressVisible = false;
        IsProgressIndeterminate = false;
        ProgressValue = 100;
        ProgressText = "";
        Severity = InfoBarSeverity.Success;
    }

    private async Task DownloadAsync()
    {
        if (_update is null)
        {
            return;
        }

        _operation = new CancellationTokenSource();
        var cancellationToken = _operation.Token;

        CanAct = false;
        IsActionVisible = false;
        IsProgressVisible = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "Starting download…";
        Severity = InfoBarSeverity.Informational;
        Title = $"Downloading Larik {_update.Version}";
        Message = "Larik stays usable while the update downloads.";

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (value.Percent is { } percent)
                {
                    IsProgressIndeterminate = false;
                    ProgressValue = percent;
                    ProgressText = $"Downloading… {percent:0}% of {FormatSize(value.TotalBytes)}";
                }
                else
                {
                    IsProgressIndeterminate = true;
                    ProgressText = $"Downloading… {FormatSize(value.BytesReceived)}";
                }
            });

            _downloadedPath = await _updates.DownloadAsync(_update, progress, cancellationToken);
            ShowReadyToInstall();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The notice was closed; the partial file is cleaned up by the service.
        }
        catch (Exception exception)
        {
            _downloadedPath = null;
            IsProgressVisible = false;

            Severity = InfoBarSeverity.Error;
            Title = "Couldn't download the update";
            Message = Describe(exception);
            ActionText = "Try again";
            CanAct = true;
            IsActionVisible = true;
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
        }
    }

    private async Task InstallAsync()
    {
        if (_update is null || _downloadedPath is null)
        {
            return;
        }

        _operation = new CancellationTokenSource();
        var cancellationToken = _operation.Token;

        CanAct = false;
        IsActionVisible = false;
        IsProgressVisible = true;
        IsProgressIndeterminate = true;
        ProgressText = _update.InstallKind == UpdateInstallKind.Installer
            ? "Starting the installer…"
            : "Preparing this folder…";
        Severity = InfoBarSeverity.Informational;
        Title = $"Installing Larik {_update.Version}";
        Message = "Larik is closing to finish the update.";

        try
        {
            await _updates.InstallAsync(_update, _downloadedPath, cancellationToken);

            // The installer or portable helper needs this process gone before it can replace files.
            Application.Current.Exit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closed while the portable copy was being prepared. Nothing has been replaced yet,
            // so the next launch can try again.
        }
        catch (Exception exception)
        {
            IsProgressVisible = false;
            IsProgressIndeterminate = false;
            Severity = InfoBarSeverity.Error;
            Title = "Couldn't start the update";
            Message = Describe(exception);

            // A bad download must be fetched again; a folder-permission failure can retry with the
            // file that is already here.
            if (exception is InvalidDataException or FileNotFoundException)
            {
                _downloadedPath = null;
                ActionText = "Download again";
            }
            else
            {
                ActionText = "Try again";
            }

            CanAct = true;
            IsActionVisible = true;
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException =>
            "Larik couldn't reach the update server. Check your connection and try again.",
        InvalidDataException => exception.Message,
        FileNotFoundException => "The downloaded update is missing. Download it again.",
        UnauthorizedAccessException or IOException =>
            "Larik couldn't save the update. Check free space and try again.",
        _ => exception.Message,
    };

    private static string FormatSize(long? bytes)
    {
        if (bytes is not { } size || size <= 0)
        {
            return "";
        }

        const double kilobyte = 1024;
        var value = (double)size;

        if (value >= kilobyte * kilobyte * 1024)
        {
            return $"{value / (kilobyte * kilobyte * 1024):0.#} GB";
        }

        return value >= kilobyte * kilobyte
            ? $"{value / (kilobyte * kilobyte):0.#} MB"
            : $"{value / kilobyte:0.#} KB";
    }
}
