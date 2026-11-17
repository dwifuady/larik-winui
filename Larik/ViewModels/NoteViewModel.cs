using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// One note in the pane and, when open, in the editor popover. The view model owns the anchor and
/// its resolution state; the shell owns the text the anchor resolves against.
/// </summary>
public partial class NoteViewModel : ObservableObject
{
    public NoteViewModel(NoteRecord record, TabViewModel tab)
    {
        Id = record.Id;
        SpaceId = record.SpaceId;
        Tab = tab;
        _kind = record.Kind;
        _body = record.Body;
        CreatedAt = record.CreatedAt;
        UpdatedAt = record.UpdatedAt;
        Anchor = record.Anchor;

        var resolved = NoteAnchoring.Resolve(record.Anchor, tab.Content);
        State = resolved.State;
        if (resolved.State != NoteAnchorState.Lost
            && (resolved.Start != record.Anchor.Start || resolved.Length != record.Anchor.Length))
        {
            Anchor = NoteAnchoring.Capture(tab.Content, resolved.Start, resolved.Length);
        }
    }

    public string Id { get; }

    public string SpaceId { get; }

    public TabViewModel Tab { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The live range in the tab's text plus the fingerprint that re-finds it after a reload.</summary>
    public NoteAnchor Anchor { get; private set; }

    public NoteAnchorState State { get; private set; }

    public NoteKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                UpdatedAt = DateTimeOffset.UtcNow;
                NotifyKindChanged();
            }
        }
    }

    private NoteKind _kind;

    public string Body
    {
        get => _body;
        set
        {
            if (SetProperty(ref _body, value ?? string.Empty))
            {
                UpdatedAt = DateTimeOffset.UtcNow;
                OnPropertyChanged(nameof(EmptyBodyVisibility));
                OnPropertyChanged(nameof(BodyVisibility));
                OnPropertyChanged(nameof(EditedLabel));
                OnPropertyChanged(nameof(MetaText));
            }
        }
    }

    private string _body = string.Empty;

    /// <summary>The highlighted card in the notes pane.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardStripOpacity));
    }

    public Brush CardBackground => IsActive
        ? ThemeService.GetBrush("StudioAccentSoftBrush", StudioColors.Muted)
        : ThemeService.GetBrush("ElevatedSurfaceBrush", StudioColors.Muted);

    public Brush CardBorderBrush => IsActive
        ? ThemeService.GetBrush("StudioAccentBrush", StudioColors.Muted)
        : ThemeService.GetBrush("HairlineBrush", StudioColors.Muted);

    /// <summary>The 2 px accent strip on an open note's card, mirroring the rail's active tab.</summary>
    public double CardStripOpacity => IsActive ? 1 : 0;

    /// <summary>
    /// Set by the shell: "Statement 2 · lines 12–21" or the honest "Anchor lost" line. It is not
    /// derived here because only the shell has the tab's editor language.
    /// </summary>
    [ObservableProperty]
    public partial string AnchorText { get; set; } = string.Empty;

    public string TabTitle => Tab.Title;

    /// <summary>True in the workspace scope, where a card must name the tab it came from.</summary>
    [ObservableProperty]
    public partial bool ShowTab { get; set; }

    partial void OnShowTabChanged(bool value) => OnPropertyChanged(nameof(TabLabelVisibility));

    public Visibility TabLabelVisibility => ShowTab ? Visibility.Visible : Visibility.Collapsed;

    public string Quote => Anchor.Quote;

    public string QuotePreview
    {
        get
        {
            var text = Anchor.Quote.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (text.Contains("  ", StringComparison.Ordinal))
            {
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            }

            return text.Trim();
        }
    }

    public bool HasQuote => Anchor.Length > 0 && Anchor.Quote.Length > 0;

    public Visibility QuoteVisibility => HasQuote ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyBodyVisibility => string.IsNullOrEmpty(Body) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BodyVisibility => string.IsNullOrEmpty(Body) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LostVisibility => State == NoteAnchorState.Lost ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReanchorVisibility => LostVisibility;

    public string KindLabel => Kind switch
    {
        NoteKind.Todo => "TODO",
        NoteKind.Decision => "Decision",
        NoteKind.Observation => "Observation",
        _ => "Note",
    };

    public string KindGlyph => Kind switch
    {
        NoteKind.Todo => "\uE73E",
        NoteKind.Decision => "\uE735",
        NoteKind.Observation => "\uE946",
        _ => "\uE90A",
    };

    public Brush KindBrush => Kind switch
    {
        NoteKind.Todo => ThemeService.GetBrush("StudioWarningBrush", StudioColors.Warning),
        NoteKind.Decision => ThemeService.GetBrush("StudioAccentBrush", StudioColors.Muted),
        NoteKind.Observation => ThemeService.GetBrush("StudioSuccessBrush", StudioColors.Success),
        _ => ThemeService.GetBrush("StudioTextSecondaryBrush", StudioColors.Muted),
    };

    public string EditedLabel
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - UpdatedAt;
            if (elapsed < TimeSpan.FromMinutes(1))
            {
                return "just now";
            }

            if (elapsed < TimeSpan.FromHours(1))
            {
                var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
                return $"{minutes} min ago";
            }

            if (elapsed < TimeSpan.FromHours(24))
            {
                return $"{(int)elapsed.TotalHours}h ago";
            }

            if (elapsed < TimeSpan.FromHours(48))
            {
                return "yesterday";
            }

            if (elapsed < TimeSpan.FromDays(30))
            {
                return $"{(int)elapsed.TotalDays}d ago";
            }

            return UpdatedAt.ToLocalTime().ToString("d");
        }
    }

    public string MetaText => State == NoteAnchorState.Lost
        ? "The text this was attached to is gone"
        : $"Edited {EditedLabel}";

    /// <summary>Full one-line description for the note row's automation name.</summary>
    public string AutomationName => $"{KindLabel}: {AnchorText}. {(string.IsNullOrWhiteSpace(Body) ? "Empty note" : Body)}";

    /// <summary>Re-resolves the stored fingerprint against the tab's current text.</summary>
    public void ResolveAgainst(string? text)
    {
        text ??= string.Empty;
        var resolved = NoteAnchoring.Resolve(Anchor, text);
        State = resolved.State;

        if (resolved.State != NoteAnchorState.Lost
            && (resolved.Start != Anchor.Start || resolved.Length != Anchor.Length))
        {
            Anchor = NoteAnchoring.Capture(text, resolved.Start, resolved.Length);
        }

        RaiseAnchorChanged();
    }

    /// <summary>Rebases the anchor across one edit; the caller passes the text before and after.</summary>
    public void Rebase(string? oldText, string? newText)
    {
        Anchor = NoteAnchoring.Rebase(Anchor, oldText, newText);
        State = Anchor.IsTab
            ? NoteAnchorState.Tab
            : Anchor.Length == 0
                ? NoteAnchorState.Lost
                : NoteAnchorState.Anchored;
        RaiseAnchorChanged();
    }

    /// <summary>Points the note at a new range. This is the explicit recovery path for a lost note.</summary>
    public void Reanchor(NoteAnchor anchor)
    {
        Anchor = anchor;
        State = anchor.IsTab
            ? NoteAnchorState.Tab
            : anchor.Length > 0 && anchor.Quote.Length > 0
                ? NoteAnchorState.Anchored
                : NoteAnchorState.Lost;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseAnchorChanged();
        OnPropertyChanged(nameof(EditedLabel));
        OnPropertyChanged(nameof(MetaText));
    }

    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(KindBrush));
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(CardBorderBrush));
    }

    public NoteRecord ToRecord() => new()
    {
        Id = Id,
        SpaceId = SpaceId,
        TabId = Tab.Id,
        Kind = Kind,
        Body = Body,
        Anchor = Anchor,
        IsUnanchored = State == NoteAnchorState.Lost,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    private void RaiseAnchorChanged()
    {
        OnPropertyChanged(nameof(Quote));
        OnPropertyChanged(nameof(QuotePreview));
        OnPropertyChanged(nameof(HasQuote));
        OnPropertyChanged(nameof(QuoteVisibility));
        OnPropertyChanged(nameof(LostVisibility));
        OnPropertyChanged(nameof(ReanchorVisibility));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(AutomationName));
    }

    private void NotifyKindChanged()
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(KindGlyph));
        OnPropertyChanged(nameof(KindBrush));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(EditedLabel));
        OnPropertyChanged(nameof(MetaText));
    }
}
