using System.Text;
using Larik.Models;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Larik.Controls;

/// <summary>
/// Captures the two things the app cannot infer when the user turns selected SQL into a snippet:
/// the trigger to type and a name to find it by. Content, scope and enabled state already have
/// sensible values, so the dialog keeps the rest of the snippet editor in Settings.
/// </summary>
public sealed class SnippetCaptureDialog : ContentDialog
{
    private const int MaxNameLength = 80;
    private const int MaxTriggerLength = 20;

    private readonly SnippetLibrary _library;
    private readonly string _content;
    private readonly TextBox _triggerBox;
    private readonly TextBox _nameBox;
    private readonly TextBlock _error;

    public SnippetCaptureDialog(XamlRoot xamlRoot, string sql, SnippetLibrary? library = null)
    {
        _library = library ?? SnippetLibrary.Current;
        _content = Normalize(sql);

        _triggerBox = new TextBox
        {
            Header = "Trigger",
            PlaceholderText = "Type this in the editor, then press Tab",
            Text = SuggestTrigger(_content, _library),
            MaxLength = 32,
        };
        AutomationProperties.SetName(_triggerBox, "Snippet trigger");

        _nameBox = new TextBox
        {
            Header = "Name",
            PlaceholderText = "What the snippet is for",
            Text = SuggestName(_content),
            MaxLength = MaxNameLength,
        };
        AutomationProperties.SetName(_nameBox, "Snippet name");

        _error = new TextBlock
        {
            Foreground = ThemeService.GetBrush("StudioDangerBrush", StudioColors.Danger),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);

        XamlRoot = xamlRoot;
        Title = "Add to snippets";
        PrimaryButtonText = "Add snippet";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Keep this selection so it can be typed again from the editor.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeService.GetBrush("StudioTextSecondaryBrush", StudioColors.Muted),
                },
                _triggerBox,
                _nameBox,
                _error,
            },
        };

        Opened += (_, _) =>
        {
            _triggerBox.Focus(FocusState.Programmatic);
            _triggerBox.SelectAll();
        };

        PrimaryButtonClick += OnPrimaryClick;
    }

    /// <summary>The saved snippet, or null while the dialog is open or after cancelling.</summary>
    public SnippetRecord? Created { get; private set; }

    /// <summary>Shows the capture dialog and returns the created snippet, or null if cancelled.</summary>
    public static async Task<SnippetRecord?> CaptureAsync(
        XamlRoot xamlRoot,
        string sql,
        SnippetLibrary? library = null)
    {
        var dialog = new SnippetCaptureDialog(xamlRoot, sql, library);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.Created : null;
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var trigger = _triggerBox.Text.Trim();
        var name = _nameBox.Text.Trim();

        if (trigger.Length == 0)
        {
            Reject(args, "A trigger is required.", _triggerBox);
            return;
        }

        if (!trigger.All(character => char.IsLetterOrDigit(character) || character == '_'))
        {
            Reject(args, "Triggers can only contain letters, digits and underscores.", _triggerBox);
            return;
        }

        if (name.Length == 0)
        {
            Reject(args, "A name is required.", _nameBox);
            return;
        }

        if (_library.HasTriggerConflict(trigger, null))
        {
            Reject(args, $"The trigger \"{trigger}\" already exists.", _triggerBox);
            return;
        }

        try
        {
            Created = _library.Create(trigger, name, _content, description: null, category: "Custom", dialects: null);
        }
        catch (Exception ex)
        {
            Reject(args, $"Could not save the snippet: {ex.Message}", _triggerBox);
        }
    }

    private void Reject(ContentDialogButtonClickEventArgs args, string message, Control focus)
    {
        args.Cancel = true;
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        focus.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// RichEdit paragraphs arrive as CR; storing LF matches the catalog and keeps the Settings
    /// preview (which splits on LF) readable.
    /// </summary>
    private static string Normalize(string sql) =>
        (sql ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>
    /// A trigger the user can accept without editing: the first word of the SQL, made safe and made
    /// unique. Selection stays on the box so typing replaces it in one keystroke.
    /// </summary>
    private static string SuggestTrigger(string sql, SnippetLibrary library)
    {
        var builder = new StringBuilder();
        foreach (var character in sql)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(char.ToLowerInvariant(character));
                if (builder.Length >= MaxTriggerLength)
                {
                    break;
                }
            }
            else if (builder.Length > 0)
            {
                break;
            }
        }

        var baseTrigger = builder.Length > 0 ? builder.ToString() : "snip";
        var candidate = baseTrigger;
        for (var suffix = 2; library.HasTriggerConflict(candidate, null); suffix++)
        {
            candidate = $"{baseTrigger}{suffix}";
        }

        return candidate;
    }

    /// <summary>The first line is the best one-line description of a query.</summary>
    private static string SuggestName(string sql)
    {
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd(';');
            if (trimmed.Length == 0)
            {
                continue;
            }

            return trimmed.Length <= 60 ? trimmed : $"{trimmed[..59]}…";
        }

        return "Selection";
    }
}
