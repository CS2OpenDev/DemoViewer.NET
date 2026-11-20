#region

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Update;

#endregion

namespace DemoViewer.NET.ViewModels.Update;

/// <summary>
///     Backs the update-notice pop-up window: the richer replacement for the old banner-only
///     offer. Wraps the shared <see cref="UpdateViewModel" /> (which owns check/download/apply
///     state) and adds the release notes for the offered version, fetched lazily when the window
///     opens so the pop-up never waits on the network to appear.
///     <para>
///         One instance lives per shell (created on first show, reused after), so the notes fetch
///         happens once per offered version and re-opening the window via the banner's "Details…" is
///         instant. A later check that offers another version reloads them.
///     </para>
/// </summary>
public sealed partial class UpdateNoticeViewModel : ViewModelBase
{
    private readonly IReleaseNotesService _notesService;

    /// <summary>True while the notes fetch is in flight (shows the loading line).</summary>
    [ObservableProperty]
    private bool _isLoadingNotes;

    private Task? _loadTask;

    // The version _loadTask loaded the notes for; null for a load made while none was offered.
    private string? _loadedFor;

    /// <summary>Shown instead of notes when the fetch failed (offline, no release body).</summary>
    [ObservableProperty]
    private string? _notesFallback;

    /// <summary>The fetched release-note markdown, rendered by <see cref="MarkdownBlock" />.</summary>
    [ObservableProperty]
    private string? _notesMarkdown;

    /// <summary>"Published 2 Aug 2026" line under the headline; null hides it.</summary>
    [ObservableProperty]
    private string? _publishedDisplay;

    /// <summary>Browser URL of the release page; null hides the "View on GitHub" button.</summary>
    [ObservableProperty]
    private string? _releaseUrl;

    /// <summary>Constructs over the shared updater VM and a notes source.</summary>
    public UpdateNoticeViewModel(UpdateViewModel update, IReleaseNotesService notesService)
    {
        Update = update;
        _notesService = notesService;
        Update.PropertyChanged += OnUpdatePropertyChanged;
    }

    /// <summary>
    ///     The shared updater VM: the window binds its Update &amp; Restart command, download
    ///     progress, and status text straight through, so the pop-up and the banner can never
    ///     disagree about update state.
    /// </summary>
    public UpdateViewModel Update { get; }

    /// <summary>Window headline: offered version comes from the shared updater VM.</summary>
    public string HeadlineText => $"DemoViewer.NET {Update.AvailableVersion} is available";

    /// <summary>Secondary line: what the user is running now.</summary>
    public string SubHeadlineText => $"You're running {Update.CurrentVersionDisplay}.";

    /// <summary>Raised when the window hosting this VM should close ("Later").</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    ///     Fetches the notes once per offered version; safe to call on every window open. Called by the
    ///     window's <c>OnOpened</c> rather than the constructor, so tests and the designer never touch the
    ///     network.
    /// </summary>
    public Task EnsureNotesLoadedAsync()
    {
        string? version = Update.AvailableVersion;
        if (_loadTask is not null && string.Equals(_loadedFor, version, StringComparison.Ordinal))
        {
            return _loadTask;
        }

        _loadedFor = version;
        NotesMarkdown = null;
        NotesFallback = null;
        PublishedDisplay = null;
        ReleaseUrl = null;
        return _loadTask = LoadNotesAsync(version);
    }

    // A new offered version renames the headline, and reloads notes already shown for the old one, so an
    // open window does not describe a version Update & Restart no longer installs.
    private void OnUpdatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdateViewModel.AvailableVersion))
        {
            return;
        }

        OnPropertyChanged(nameof(HeadlineText));
        if (_loadTask is not null && Update.AvailableVersion is not null)
        {
            _ = EnsureNotesLoadedAsync();
        }
    }

    private async Task LoadNotesAsync(string? version)
    {
        if (version is null)
        {
            NotesFallback = "Release notes unavailable.";
            return;
        }

        IsLoadingNotes = true;
        try
        {
            ReleaseNotes? notes = await _notesService.GetForVersionAsync(version).ConfigureAwait(true);
            if (!string.Equals(_loadedFor, version, StringComparison.Ordinal))
            {
                return; // another version was offered while this one loaded; its load owns the fields
            }

            if (notes is null)
            {
                NotesFallback = "Couldn't fetch the release notes — you can read them on the GitHub releases page.";
                ReleaseUrl = "https://github.com/CS2OpenDev/DemoViewer.NET/releases";
                return;
            }

            NotesMarkdown = notes.BodyMarkdown;
            ReleaseUrl = notes.HtmlUrl;
            if (notes.PublishedAt is { } published)
            {
                PublishedDisplay = $"Published {published.ToLocalTime():d MMMM yyyy}";
            }
        }
        finally
        {
            if (string.Equals(_loadedFor, version, StringComparison.Ordinal))
            {
                IsLoadingNotes = false;
            }
        }
    }

    /// <summary>Closes the pop-up without dismissing the banner: the offer stays visible.</summary>
    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the release page in the default browser.</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (ReleaseUrl is { } url)
        {
            OpenExternal.OpenUri(url);
        }
    }
}
