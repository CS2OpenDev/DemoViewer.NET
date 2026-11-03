#region

using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>What the theme editor needs from the app around it.</summary>
public sealed class ThemeEditorHost
{
    /// <summary>The theme id Settings has selected.</summary>
    public required Func<string?> ActiveThemeId { get; init; }

    /// <summary>Selects and saves a theme id, as picking it in Settings does.</summary>
    public required Action<string> UseTheme { get; init; }

    /// <summary>Shows the whole app in <c>variant</c> (the preview), repainting the code-drawn surfaces too.</summary>
    public required Action<ThemeVariant> ShowAppIn { get; init; }

    /// <summary>Puts the app back on the selected theme.</summary>
    public required Action RestoreApp { get; init; }
}

/// <summary>One theme in the editor's list.</summary>
public sealed partial class ThemeEditorListItem(Theme theme, int missingCount) : ObservableObject
{
    public Theme Theme { get; } = theme;

    public string Name => Theme.DisplayName;

    public bool IsBuiltIn => Theme.IsBuiltIn;

    public int MissingCount { get; } = missingCount;

    public bool HasMissing => MissingCount > 0;

    public string MissingText => MissingCount == 1 ? "1 missing" : $"{MissingCount} missing";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>One section of tokens, with the rows the search and filter leave showing.</summary>
public sealed partial class ThemeEditorSection(string title, IReadOnlyList<ThemeTokenRowViewModel> rows)
    : ObservableObject
{
    public string Title { get; } = title;

    public IReadOnlyList<ThemeTokenRowViewModel> Rows { get; } = rows;

    public ObservableCollection<ThemeTokenRowViewModel> VisibleRows { get; } = [];

    [ObservableProperty]
    private bool _isVisible = true;
}

/// <summary>Which question the editor is asking, if any.</summary>
public enum ThemeEditorPrompt
{
    None,
    UnsavedChanges,
    NameCopy,
    ConfirmDelete
}

/// <summary>
///     The Theme Customization window. Lists every theme with a palette; a built-in is shown read-only and can
///     be customized into a named copy, a user theme is edited in place. Every edit recolours a preview variant
///     the window's preview pane shows, and the whole app too while "Apply to the app while editing" is on. The
///     app goes back to the selected theme when the window closes without saving.
///     <para>
///         A token the theme's file does not set is "missing": it shows the base's value, is flagged, and stays
///         unset on save unless the user edits it or uses "Fill missing from base".
///     </para>
/// </summary>
public sealed partial class ThemeEditorViewModel : ObservableObject, IDisposable
{
    private readonly ThemeEditorHost _host;
    private readonly ThemeRegistry _registry;
    private readonly UserThemeStore _store;
    private readonly List<ThemeTokenRowViewModel> _rows = [];
    private readonly Dictionary<ThemeTokenRowViewModel, (ThemeTokenValue Value, bool IsSet)> _savedRows = [];
    private readonly Stack<(ThemeTokenRowViewModel Row, ThemeTokenValue Old, DateTime At)> _undo = new();
    private Action? _afterPrompt;
    private bool _closing;
    private bool _disposed;
    private DispatcherTimer? _repaint;
    private ThemeDefinition? _definition;
    private string _savedName = "";
    private ThemeVariant _savedBase = ThemeVariant.Dark;
    private bool _restoring;

    public ThemeEditorViewModel(UserThemeStore store, ThemeEditorHost host, string? themeId = null,
        bool missingOnly = false)
    {
        _store = store;
        _registry = store.Registry;
        _host = host;
        _missingOnly = missingOnly;
        RebuildList();
        string? id = themeId ?? host.ActiveThemeId();
        ThemeEditorListItem? start =
            Themes.FirstOrDefault(t => string.Equals(t.Theme.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? Themes.FirstOrDefault(t => t.Theme.Id == ThemeBaseIdForSystem())
            ?? Themes[0];
        Load(start.Theme);
    }

    /// <summary>Every theme with a palette: the built-ins first, then the user's.</summary>
    public ObservableCollection<ThemeEditorListItem> Themes { get; } = [];

    /// <summary>The token sections, in palette order.</summary>
    public ObservableCollection<ThemeEditorSection> Sections { get; } = [];

    /// <summary>Files in the themes folder that could not be loaded, as "name: reason" lines.</summary>
    public ObservableCollection<string> LoadProblems { get; } = [];

    public IReadOnlyList<string> BaseOptions { get; } = ["Dark", "Light"];

    /// <summary>The theme being shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(CanEdit), nameof(IsActive), nameof(CanUseTheme),
        nameof(CopyButtonText))]
    private Theme _theme = null!;

    /// <summary>True for a built-in: shown, copied, never changed.</summary>
    public bool IsReadOnly => Theme.IsBuiltIn;

    public bool CanEdit => !Theme.IsBuiltIn;

    /// <summary>"Customize" for a built-in, "Duplicate" for a user theme.</summary>
    public string CopyButtonText => Theme.IsBuiltIn ? "Customize" : "Duplicate";

    /// <summary>The name box. Saving writes it, so this is also how a theme is renamed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(NameError))]
    private string _name = "";

    public string? NameError => CanEdit ? _store.ValidateName(Name, Theme.Id) : null;

    /// <summary>"Dark" or "Light".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _selectedBase = "Dark";

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Shows only tokens the theme does not set.</summary>
    [ObservableProperty]
    private bool _missingOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissing), nameof(MissingSummary))]
    private int _missingCount;

    public bool HasMissing => MissingCount > 0;

    public string MissingSummary => MissingCount == 1
        ? "1 colour is missing and uses the base value."
        : $"{MissingCount} colours are missing and use the base value.";

    /// <summary>Keys in the file this version does not use, or null.</summary>
    [ObservableProperty]
    private string? _unknownKeysText;

    /// <summary>The row the picker edits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    private ThemeTokenRowViewModel? _selectedRow;

    public bool HasSelectedRow => SelectedRow is not null;

    /// <summary>How many tokens differ from the saved theme.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private int _editCount;

    /// <summary>True when there is something Save would write.</summary>
    public bool IsDirty => CanEdit && (EditCount > 0 || Name.Trim() != _savedName || BaseVariant != _savedBase);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    /// <summary>Shows the edit across the whole app, not just the preview pane.</summary>
    [ObservableProperty]
    private bool _applyToApp;

    /// <summary>The variant the preview pane renders under.</summary>
    [ObservableProperty]
    private ThemeVariant? _previewVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt), nameof(IsUnsavedPrompt), nameof(IsNamePrompt), nameof(IsDeletePrompt))]
    private ThemeEditorPrompt _prompt;

    public bool HasPrompt => Prompt != ThemeEditorPrompt.None;

    public bool IsUnsavedPrompt => Prompt == ThemeEditorPrompt.UnsavedChanges;

    public bool IsNamePrompt => Prompt == ThemeEditorPrompt.NameCopy;

    public bool IsDeletePrompt => Prompt == ThemeEditorPrompt.ConfirmDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyNameError))]
    private string _copyName = "";

    public string? CopyNameError => _store.ValidateName(CopyName);

    [ObservableProperty]
    private string _copyPromptTitle = "";

    // True when the copy prompt saves the current edits (Save As) rather than the theme as saved (Duplicate).
    private bool _copyFromEdits;

    /// <summary>The last thing that happened, for the status line.</summary>
    [ObservableProperty]
    private string? _status;

    public bool IsActive => string.Equals(_host.ActiveThemeId(), Theme.Id, StringComparison.OrdinalIgnoreCase);

    public bool CanUseTheme => !IsActive;

    public string DeletePromptText =>
        $"Delete \"{Theme.DisplayName}\"? This removes {DeletedFileNames()} from your themes folder and can't be undone.";

    // Delete takes every file with the theme's id, so the prompt names them all.
    private string DeletedFileNames()
    {
        List<string> names = [.. UserThemeStore.FilesFor(Theme.Id).Select(f => Path.GetFileName(f))];
        if (names.Count == 0)
        {
            names.Add(Path.GetFileName(Theme.FilePath ?? ""));
        }

        return names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    public string UnsavedPromptText => $"You have unsaved changes to \"{_savedName}\".";

    private ThemeVariant BaseVariant => SelectedBase == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    private static string ThemeBaseIdForSystem() => ThemeJson.BaseName(
        Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Light
            ? ThemeVariant.Light
            : ThemeVariant.Dark);

    private void RebuildList()
    {
        string? active = _host.ActiveThemeId();
        Themes.Clear();
        foreach (Theme theme in _registry.Themes.Where(t => t.CanCustomize))
        {
            Themes.Add(new ThemeEditorListItem(theme, theme.MissingTokens.Count)
            {
                IsActive = string.Equals(theme.Id, active, StringComparison.OrdinalIgnoreCase)
            });
        }

        LoadProblems.Clear();
        foreach (ThemeLoadProblem problem in _registry.LoadProblems)
        {
            LoadProblems.Add($"{problem.FileName}: {problem.Reason}");
        }

        OnPropertyChanged(nameof(HasLoadProblems));
    }

    public bool HasLoadProblems => LoadProblems.Count > 0;

    // Shows `theme`: its palette as rows, its preview, and the list selection.
    private void Load(Theme theme)
    {
        ThemePalette palette = _registry.PaletteFor(theme.Id) ?? BuiltInThemes.Dark;
        _definition = _registry.UserDefinitionFor(theme.Id);
        _restoring = true;
        try
        {
            Theme = theme;
            _savedName = theme.DisplayName;
            _savedBase = palette.BaseVariant;
            Name = theme.DisplayName;
            SelectedBase = palette.BaseVariant == ThemeVariant.Light ? "Light" : "Dark";
        }
        finally
        {
            _restoring = false;
        }

        ThemePalette basePalette = BuiltInThemes.BaseFor(palette.BaseVariant);
        _rows.Clear();
        _savedRows.Clear();
        Sections.Clear();
        foreach (ThemeSection section in ThemeExport.Sections)
        {
            List<ThemeTokenRowViewModel> rows = [];
            foreach (string token in section.Tokens)
            {
                ThemeTokenRowViewModel row = new(token, ThemeTokenValue.From(palette, token),
                    theme.IsBuiltIn || !palette.Inherited.Contains(token),
                    ThemeTokenValue.From(basePalette, token), OnRowChanged);
                rows.Add(row);
                _rows.Add(row);
                _savedRows[row] = (row.Value, row.IsSet);
            }

            Sections.Add(new ThemeEditorSection(section.Title, rows));
        }

        _undo.Clear();
        CanUndo = false;
        EditCount = 0;
        UnknownKeysText = theme.UnknownKeys.Count == 0
            ? null
            : "Not used by this version, kept when you save: " + string.Join(", ", theme.UnknownKeys);
        foreach (ThemeEditorListItem item in Themes)
        {
            item.IsSelected = item.Theme.Id == theme.Id;
        }

        SelectedRow = _rows.FirstOrDefault(r => r.IsMissing) ?? _rows.FirstOrDefault();
        UpdateMissing();
        Refilter();
        StartPreview();
    }

    private void StartPreview()
    {
        PreviewVariant = _registry.BeginPreview(CurrentPalette());
        if (ApplyToApp)
        {
            _host.ShowAppIn(PreviewVariant);
        }
    }

    /// <summary>The theme as edited so far.</summary>
    public ThemePalette CurrentPalette()
    {
        Dictionary<string, Color> colors = new(StringComparer.Ordinal);
        Dictionary<string, ThemeGradient> gradients = new(StringComparer.Ordinal);
        foreach (ThemeTokenRowViewModel row in _rows)
        {
            if (row.Value.Gradient is { } g)
            {
                gradients[row.Key] = g;
            }
            else
            {
                colors[row.Key] = row.Value.Color;
            }
        }

        return new ThemePalette(Theme.Id, Name.Trim(), BaseVariant, colors, gradients,
            _rows.Where(r => r.IsMissing).Select(r => r.Key), _definition?.UnknownTokens);
    }

    private void OnRowChanged(ThemeTokenRowViewModel row, ThemeTokenValue old)
    {
        if (!CanEdit)
        {
            // A built-in never changes, even if something gets an edit past the read-only view.
            _restoring = true;
            try
            {
                row.Restore(old with { WasSet = true }, true);
            }
            finally
            {
                _restoring = false;
            }

            return;
        }

        if (!_restoring)
        {
            // A drag through the picker sends many values; one undo step per token per second is enough.
            if (_undo.TryPeek(out (ThemeTokenRowViewModel Row, ThemeTokenValue Old, DateTime At) last)
                && last.Row == row && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(1))
            {
                _undo.Pop();
                _undo.Push((row, last.Old, DateTime.UtcNow));
            }
            else
            {
                _undo.Push((row, old, DateTime.UtcNow));
            }

            CanUndo = true;
        }

        CountEdits();

        PushPreview(row);
        UpdateMissing();
    }

    private void PushPreview(ThemeTokenRowViewModel row)
    {
        if (row.Value.Gradient is { } g)
        {
            _registry.UpdatePreview(row.Key, g);
        }
        else
        {
            _registry.UpdatePreview(row.Key, row.Value.Color);
        }

        if (ApplyToApp)
        {
            ScheduleAppRepaint();
        }
    }

    // DynamicResource users repaint on their own when a preview brush changes; the code-drawn surfaces (2D
    // canvas, graph, syntax colours) only re-read on a variant change, so the app gets one, a moment after
    // the last edit rather than on every step of a picker drag.
    private void ScheduleAppRepaint()
    {
        if (_repaint is null)
        {
            _repaint = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _repaint.Tick += (_, _) =>
            {
                _repaint.Stop();
                if (ApplyToApp && PreviewVariant is { } v)
                {
                    _host.ShowAppIn(v);
                }
            };
        }

        _repaint.Stop();
        _repaint.Start();
    }

    // Dirty is a comparison with the saved theme, not a tally of edits: a picker drag is many edits and one
    // undo step, and a colour edited back by hand is no change at all. A token the file does not set follows
    // its base, which the base comparison in IsDirty covers.
    private void CountEdits() =>
        EditCount = _rows.Count(r => _savedRows.TryGetValue(r, out (ThemeTokenValue Value, bool IsSet) saved)
                                     && (r.IsSet != saved.IsSet || (r.IsSet && !r.Value.Equals(saved.Value))));

    private void UpdateMissing() => MissingCount = CanEdit ? _rows.Count(r => r.IsMissing) : 0;

    partial void OnSelectedRowChanged(ThemeTokenRowViewModel? oldValue, ThemeTokenRowViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        newValue?.IsSelected = true;
    }

    partial void OnSearchTextChanged(string value) => Refilter();

    partial void OnMissingOnlyChanged(bool value) => Refilter();

    partial void OnSelectedBaseChanged(string value)
    {
        if (_restoring)
        {
            return;
        }

        ThemePalette basePalette = BuiltInThemes.BaseFor(BaseVariant);
        foreach (ThemeTokenRowViewModel row in _rows)
        {
            row.Rebase(ThemeTokenValue.From(basePalette, row.Key));
        }

        StartPreview();
    }

    partial void OnApplyToAppChanged(bool value)
    {
        if (value && PreviewVariant is { } v)
        {
            _host.ShowAppIn(v);
        }
        else if (!value)
        {
            _repaint?.Stop();
            _host.RestoreApp();
        }
    }

    private void Refilter()
    {
        string search = SearchText.Trim();
        foreach (ThemeEditorSection section in Sections)
        {
            section.VisibleRows.Clear();
            foreach (ThemeTokenRowViewModel row in section.Rows)
            {
                bool matches = search.Length == 0
                               || row.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || (row.Family?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                               || (row.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                               || section.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || row.Hex.Contains(search, StringComparison.OrdinalIgnoreCase);
                if (matches && (!MissingOnly || row.IsMissing))
                {
                    section.VisibleRows.Add(row);
                }
            }

            section.IsVisible = section.VisibleRows.Count > 0;
        }
    }

    /// <summary>Shows another theme, asking first when there are unsaved edits.</summary>
    [RelayCommand]
    private void Open(ThemeEditorListItem? item)
    {
        if (item is null || item.Theme.Id == Theme.Id)
        {
            return;
        }

        Guard(() => Load(_registry.Find(item.Theme.Id) ?? item.Theme));
    }

    /// <summary>Switches to <paramref name="themeId" /> (asking first about unsaved edits), and turns on "Missing only" when asked.</summary>
    public void Reveal(string? themeId, bool missingOnly) =>
        Guard(() =>
        {
            if (_registry.Find(themeId) is { CanCustomize: true } theme && theme.Id != Theme.Id)
            {
                Load(theme);
            }

            if (missingOnly)
            {
                MissingOnly = true;
            }
        });

    [RelayCommand]
    private void SelectRow(ThemeTokenRowViewModel? row) => SelectedRow = row;

    [RelayCommand]
    private void SelectStop(int index)
    {
        if (SelectedRow is { } row)
        {
            row.ActiveStop = index;
        }
    }

    /// <summary>Gives every missing token its base value, so the file sets it.</summary>
    [RelayCommand]
    private void FillMissing()
    {
        foreach (ThemeTokenRowViewModel row in _rows.Where(r => r.IsMissing).ToList())
        {
            row.Apply(row.BaseValue);
        }

        Status = "Missing colours filled from the base. Save to keep them.";
        Refilter();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!_undo.TryPop(out (ThemeTokenRowViewModel Row, ThemeTokenValue Old, DateTime At) step))
        {
            return;
        }

        _restoring = true;
        try
        {
            step.Row.Restore(step.Old with { WasSet = true }, step.Old.WasSet);
        }
        finally
        {
            _restoring = false;
        }

        PushPreview(step.Row);
        UpdateMissing();
        SelectedRow = step.Row;
        CountEdits();
        CanUndo = _undo.Count > 0;
    }

    /// <summary>Opens the name prompt for a copy of the theme as saved.</summary>
    [RelayCommand]
    private void Copy() => Guard(() =>
        AskCopyName(false, Theme.IsBuiltIn ? $"Customize {Theme.DisplayName}" : $"Duplicate {Theme.DisplayName}"));

    /// <summary>Opens the name prompt for a copy of the current edits.</summary>
    [RelayCommand]
    private void SaveAs() => AskCopyName(true, "Save as a new theme");

    private void AskCopyName(bool fromEdits, string title)
    {
        _copyFromEdits = fromEdits;
        CopyPromptTitle = title;
        CopyName = _store.SuggestName(fromEdits ? Name : Theme.DisplayName);
        Prompt = ThemeEditorPrompt.NameCopy;
    }

    [RelayCommand]
    private void ConfirmCopy()
    {
        if (CopyNameError is not null)
        {
            return;
        }

        ThemePalette source = _copyFromEdits ? CurrentPalette() : _registry.PaletteFor(Theme.Id) ?? CurrentPalette();
        Theme copy;
        try
        {
            copy = _store.Create(source, CopyName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = "The theme could not be saved: " + e.Message;
            return;
        }

        Prompt = ThemeEditorPrompt.None;
        if (_copyFromEdits)
        {
            EditCount = 0; // the edits now live in the copy; the original stays as saved
        }

        RebuildList();
        Load(copy);
        Status = $"Saved \"{copy.DisplayName}\" to your themes folder.";
    }

    [RelayCommand]
    private void Save() => TrySave();

    private bool TrySave()
    {
        if (!CanEdit)
        {
            return true;
        }

        if (NameError is { } problem)
        {
            Status = problem;
            return false;
        }

        Theme saved;
        try
        {
            saved = _store.Save(Theme.Id, CurrentPalette(), Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = "The theme could not be saved: " + e.Message;
            return false;
        }

        RebuildList();
        Load(saved);
        Status = $"Saved \"{saved.DisplayName}\".";
        return true;
    }

    [RelayCommand]
    private void Delete()
    {
        OnPropertyChanged(nameof(DeletePromptText));
        Prompt = ThemeEditorPrompt.ConfirmDelete;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        Prompt = ThemeEditorPrompt.None;
        Theme doomed = Theme;
        bool wasActive = IsActive;
        try
        {
            _store.Delete(doomed.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = "The theme could not be deleted: " + e.Message;
            return;
        }

        string fallback = ThemeJson.BaseName(_savedBase);
        if (wasActive)
        {
            _host.UseTheme(fallback);
        }

        EditCount = 0;
        RebuildList();
        Load(_registry.Find(fallback)!);
        Status = $"Deleted \"{doomed.DisplayName}\".";
    }

    /// <summary>Selects the shown theme in Settings.</summary>
    [RelayCommand]
    private void UseTheme()
    {
        Guard(() =>
        {
            _host.UseTheme(Theme.Id);
            foreach (ThemeEditorListItem item in Themes)
            {
                item.IsActive = item.Theme.Id == Theme.Id;
            }

            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(CanUseTheme));
            Status = $"\"{Theme.DisplayName}\" is now your theme.";
        });
    }

    /// <summary>Close, asking first when there are unsaved edits. The Cancel button and the window's close box.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (TryClose())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     Asks to close. Returns true when the window may close now; false when the unsaved-changes prompt is
    ///     up, and the window closes later through <see cref="CloseRequested" /> if the user lets it.
    /// </summary>
    public bool TryClose()
    {
        if (_closing || !IsDirty)
        {
            _closing = true;
            return true;
        }

        Guard(() =>
        {
            _closing = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        return false;
    }

    // Runs `next` now, or after the user answers the unsaved-changes prompt.
    private void Guard(Action next)
    {
        if (!IsDirty)
        {
            next();
            return;
        }

        _afterPrompt = next;
        OnPropertyChanged(nameof(UnsavedPromptText));
        Prompt = ThemeEditorPrompt.UnsavedChanges;
    }

    [RelayCommand]
    private void SaveAndContinue()
    {
        if (TrySave())
        {
            Continue();
        }
    }

    [RelayCommand]
    private void DiscardAndContinue()
    {
        // Back to the theme as saved, rows and preview included: Use theme, or a Reveal of the same theme,
        // stays on it after the prompt, and a later Save must not write what was discarded.
        Load(_registry.Find(Theme.Id) ?? Theme);
        Continue();
    }

    private void Continue()
    {
        Prompt = ThemeEditorPrompt.None;
        Action? next = _afterPrompt;
        _afterPrompt = null;
        next?.Invoke();
    }

    /// <summary>Closes whichever prompt is up without doing anything.</summary>
    [RelayCommand]
    private void DismissPrompt()
    {
        _afterPrompt = null;
        Prompt = ThemeEditorPrompt.None;
    }

    /// <summary>Ends the preview and puts the app back on the selected theme.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _repaint?.Stop();
        if (ApplyToApp)
        {
            _host.RestoreApp();
        }

        _registry.EndPreview();
    }
}
