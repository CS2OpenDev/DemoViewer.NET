#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Models;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Common;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     Bookmarks panel. In-memory model with best-effort desktop persistence via
///     <see cref="BookmarkStore" /> (no-ops on WASM). Selecting a bookmark seeks to its frame through
///     the shared <see cref="FrameNavigationViewModel" />, and so does clicking a row, which also reaches the row already
///     selected. The list is the open demo's: a bookmark is a frame
///     index, and <see cref="SetDemo" /> swaps the list when the demo changes.
/// </summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    private readonly FrameNavigationViewModel _nav;
    private readonly BookmarkStore _store;

    // The open demo's content key, or null with none open: bookmarks set then are kept in memory only.
    private string? _demoKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBookmarks))]
    private Bookmark? _selected;

    /// <summary>Initializes a new <see cref="BookmarksViewModel" /> instance.</summary>
    public BookmarksViewModel(FrameNavigationViewModel nav) : this(nav, new BookmarkStore())
    {
    }

    /// <summary>A panel over <paramref name="store" />: for tests.</summary>
    internal BookmarksViewModel(FrameNavigationViewModel nav, BookmarkStore store)
    {
        _nav = nav;
        _store = store;
    }

    /// <summary>Bookmarks.</summary>
    public ObservableCollection<Bookmark> Bookmarks { get; } = [];

    /// <summary>Has bookmarks.</summary>
    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>
    ///     Adds a bookmark for the given frame (with a label) and persists. Replaces any existing
    ///     bookmark on the same frame so re-bookmarking just updates the label.
    /// </summary>
    public void Add(int frameIndex, int tick, string label)
    {
        if (frameIndex < 0)
        {
            return;
        }

        Bookmark? existing = Bookmarks.FirstOrDefault(b => b.FrameIndex == frameIndex);
        if (existing is not null)
        {
            Bookmarks.Remove(existing);
        }

        Bookmarks.Add(new Bookmark(frameIndex, tick, string.IsNullOrWhiteSpace(label) ? $"frame {frameIndex}" : label));
        Persist();
    }

    /// <summary>
    ///     Shows the bookmarks of the demo with content key <paramref name="demoKey" /> (the lowercase hex
    ///     SHA-256 of its bytes), or none for null: no demo is open. The selection is dropped without a seek.
    /// </summary>
    public void SetDemo(string? demoKey)
    {
        _demoKey = demoKey;
        Selected = null;
        Bookmarks.Clear();
        if (demoKey is not null)
        {
            foreach (Bookmark b in _store.Load(demoKey))
            {
                Bookmarks.Add(b);
            }
        }

        OnPropertyChanged(nameof(HasBookmarks));
    }

    partial void OnSelectedChanged(Bookmark? value)
    {
        if (value is not null)
        {
            _nav.SeekToFrame(value.FrameIndex);
        }
    }

    // A click on a row jumps through this rather than through the selection: the list keeps the row it
    // last selected, so picking it again is no change and the bookmark could be jumped to only once.
    // Clearing Selected from the handler does not help, the list ignores a source change during its own write.
    [RelayCommand]
    private void Jump(Bookmark? bookmark)
    {
        if (bookmark is not null)
        {
            _nav.SeekToFrame(bookmark.FrameIndex);
        }
    }

    private void Persist()
    {
        OnPropertyChanged(nameof(HasBookmarks));
        if (_demoKey is not null)
        {
            _store.Save(_demoKey, Bookmarks);
        }
    }

    [RelayCommand]
    private void Remove(Bookmark? bookmark)
    {
        if (bookmark is null)
        {
            return;
        }

        Bookmarks.Remove(bookmark);
        if (ReferenceEquals(Selected, bookmark))
        {
            Selected = null;
        }

        Persist();
    }
}
