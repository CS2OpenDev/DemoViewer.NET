#region

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Playback2D.Levels;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Levels;

/// <summary>
///     The level strip: manual floor picking, the Stacked ⇄ Single toggle, and the AUTO chip.
///     <para>
///         <b>Chips are ordered highest floor first</b>, matching the stacked view's own top-to-bottom
///         reading order (the pre-v2 band layout puts the highest floor on the top band) so the two
///         representations of "which floor" cannot disagree about which way is up.
///     </para>
///     <para>
///         <b>A single-floor map sees none of this.</b> <see cref="HasMultipleLevels" /> collapses the
///         whole strip. That is most maps, and why the tab looks unchanged on them.
///     </para>
/// </summary>
public sealed partial class LevelStripViewModel : ObservableObject
{
    private readonly List<LevelChipViewModel> _scratch = [];
    private bool _applying;

    [ObservableProperty]
    private bool _hasMultipleLevels;

    // A command that persists at the end of its own body owns the save for that whole gesture. Without
    // this the IsAutoEnabled it flips on the way would raise a SECOND SettingsChanged, and every raise is
    // a full read-serialize-temp-write-move-reload of settings.json, two of those per chip click.
    private bool _inGesture;

    /// <summary>
    ///     Whether AutoFollow is offered at all: the <c>playback2d.levels.auto</c> feature gate. With the
    ///     gate off the strip still picks levels; only the AUTO chip disappears.
    /// </summary>
    [ObservableProperty]
    private bool _isAutoAvailable = true;

    [ObservableProperty]
    private bool _isAutoEnabled = true;

    [ObservableProperty]
    private bool _isSingleMode;

    // What the user asked for, as opposed to IsAutoEnabled, which the gate forces off. Follows the flag
    // while the gate is on and stands still while it is off, so it is what gets saved and what comes
    // back with the gate.
    private bool _preferredAuto = true;

    private ILevelSurface? _surface;

    // The floor the user last picked, and the map it is a floor of. The surface is rebuilt with the view
    // on every tab activation and starts on its own default floor; with AUTO off nothing moves it back,
    // so the pick survives a tab switch only here. Restored once the new surface has resolved that floor.
    private MapLevelId _pickedLevel = MapLevelId.None;
    private string? _pickedMap;
    private bool _restorePick;

    /// <summary>The chips, highest level first.</summary>
    public ObservableCollection<LevelChipViewModel> Chips { get; } = [];

    /// <summary>The label on the display-mode toggle.</summary>
    public string DisplayModeLabel => IsSingleMode ? "SINGLE" : "STACK";

    /// <summary>Tooltip for the display-mode toggle.</summary>
    public string DisplayModeTooltip => IsSingleMode
        ? "Showing one floor at full height. Switch back to stacked bands."
        : "Showing every floor as a band. Switch to one floor at full height.";

    /// <summary>The display mode to persist.</summary>
    public LevelDisplayMode DisplayMode =>
        IsSingleMode ? LevelDisplayMode.Single : LevelDisplayMode.Stacked;

    /// <summary>
    ///     The AutoFollow flag to persist: the user's choice, not <see cref="IsAutoEnabled" />, which reads
    ///     false for as long as the feature gate is off whatever they chose.
    /// </summary>
    internal bool AutoFollowPreference => _preferredAuto;

    /// <summary>Raised when the user changed a setting worth persisting.</summary>
    public event Action? SettingsChanged;

    /// <summary>Picks a level. Switches to a single pane and turns AutoFollow off.</summary>
    [RelayCommand]
    private void Select(LevelChipViewModel? chip)
    {
        if (chip is null || _surface is null)
        {
            return;
        }

        // Both halves of the gesture are persisted by the ONE raise below: PickLevel also puts the
        // surface into Single, which Refresh mirrors onto IsSingleMode.
        _inGesture = true;
        try
        {
            _surface.PickLevel(chip.Id);
            _pickedLevel = chip.Id;
            _pickedMap = _surface.Levels.MapName;
            _restorePick = false;
            IsAutoEnabled = false;
            Refresh();
        }
        finally
        {
            _inGesture = false;
        }

        SettingsChanged?.Invoke();
    }

    /// <summary>Flips between stacked bands and a single floor.</summary>
    [RelayCommand]
    private void ToggleDisplayMode()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.DisplayMode = _surface.DisplayMode == LevelDisplayMode.Single
            ? LevelDisplayMode.Stacked
            : LevelDisplayMode.Single;
        Refresh();
        SettingsChanged?.Invoke();
    }

    /// <summary>
    ///     Re-arms AutoFollow.
    ///     <para>
    ///         <b>Deliberately NOT a <c>[RelayCommand]</c>.</b> The AUTO chip is a <c>ToggleButton</c> whose
    ///         <c>IsChecked</c> binds straight to <see cref="IsAutoEnabled" />, and a command on it would
    ///         fight that binding on the un-check half, so a command here could never be the user's path.
    ///         See <see cref="OnIsAutoEnabledChanged(bool)" /> for where persistence actually lives.
    ///     </para>
    /// </summary>
    public void EnableAuto()
    {
        if (!IsAutoAvailable)
        {
            return;
        }

        IsAutoEnabled = true; // applies to the surface, refreshes and persists; see the handler
        Refresh();
    }

    /// <summary>
    ///     Binds to the mounted surface. Idempotent, and unbinding (null) drops the subscription: the
    ///     tab's view is destroyed and rebuilt on every activation while this view-model is cached.
    /// </summary>
    /// <param name="surface">The v2 host, or null.</param>
    public void Bind(ILevelSurface? surface)
    {
        if (ReferenceEquals(_surface, surface))
        {
            return;
        }

        if (_surface is not null)
        {
            _surface.LevelStateChanged -= Refresh;
        }

        _surface = surface;

        if (_surface is null)
        {
            Chips.Clear();
            HasMultipleLevels = false;
            return;
        }

        _surface.LevelStateChanged += Refresh;
        _restorePick = !_pickedLevel.IsNone;
        ApplyToSurface();
        Refresh();
    }

    /// <summary>
    ///     Restores the persisted display mode and AutoFollow flag, and applies them to the surface.
    /// </summary>
    /// <param name="mode">The persisted <c>Playback2D:LevelDisplayMode</c>.</param>
    /// <param name="autoFollow">The persisted <c>Playback2D:AutoLevelFollow</c>.</param>
    public void ApplySettings(LevelDisplayMode mode, bool autoFollow)
    {
        _applying = true;
        try
        {
            IsSingleMode = mode == LevelDisplayMode.Single;
            _preferredAuto = autoFollow;
            IsAutoEnabled = autoFollow;
        }
        finally
        {
            _applying = false;
        }

        ApplyToSurface();
        Refresh();
    }

    /// <summary>Re-reads the surface's level set and active level.</summary>
    private void Refresh()
    {
        if (_surface is null)
        {
            return;
        }

        RestorePick();

        IReadOnlyList<MapLevel> levels = _surface.Levels.Levels;
        MapLevelId active = _surface.ActiveLevelId;

        _scratch.Clear();
        for (int i = levels.Count - 1; i >= 0; i--) // highest first
        {
            _scratch.Add(new LevelChipViewModel(levels[i], levels[i].Id == active));
        }

        if (!SameChips(_scratch))
        {
            Chips.Clear();
            foreach (LevelChipViewModel chip in _scratch)
            {
                Chips.Add(chip);
            }
        }
        else
        {
            for (int i = 0; i < Chips.Count; i++)
            {
                Chips[i].IsActive = _scratch[i].IsActive;
            }
        }

        HasMultipleLevels = levels.Count > 1;

        _applying = true;
        try
        {
            IsSingleMode = _surface.DisplayMode == LevelDisplayMode.Single;
            IsAutoEnabled = IsAutoAvailable && _surface.AutoLevelFollow;

            // The surface turns AutoFollow off by itself on a manual pick, and that is the user's doing.
            // With the gate off its flag is only the gate's, and says nothing about what they want.
            if (IsAutoAvailable)
            {
                _preferredAuto = IsAutoEnabled;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnIsAutoAvailableChanged(bool value)
    {
        if (!value)
        {
            // Under _applying on purpose: the GATE went off, which is not the user changing their mind.
            // Persisting it would overwrite a real preference with the state of a feature flag, and a
            // release that shipped the gate off would take AutoFollow away for good.
            _applying = true;
            try
            {
                IsAutoEnabled = false;
            }
            finally
            {
                _applying = false;
            }
        }
        else
        {
            // And the gate coming back gives the preference back, for the same reason and as quietly.
            _applying = true;
            try
            {
                IsAutoEnabled = _preferredAuto;
            }
            finally
            {
                _applying = false;
            }
        }

        ApplyToSurface();
    }

    /// <summary>
    ///     Where AUTO's persistence lives. The AUTO chip is a <c>ToggleButton</c> bound straight to
    ///     <see cref="IsAutoEnabled" />, so <b>every real AUTO flip arrives here</b> and not through a
    ///     command. A command on that chip would never be reached.
    /// </summary>
    partial void OnIsAutoEnabledChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        _preferredAuto = value;
        if (value)
        {
            _pickedLevel = MapLevelId.None;
            _restorePick = false;
        }

        ApplyToSurface();
        Refresh();

        if (!_inGesture)
        {
            SettingsChanged?.Invoke();
        }
    }

    partial void OnIsSingleModeChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayModeLabel));
        OnPropertyChanged(nameof(DisplayModeTooltip));
        if (!_applying)
        {
            ApplyToSurface();
        }
    }

    private void ApplyToSurface()
    {
        if (_surface is null)
        {
            return;
        }

        // Both read BEFORE either write. Each write raises LevelStateChanged, and Refresh reads the
        // surface back into these properties: on a freshly built surface the first write would otherwise
        // copy its default AutoFollow over the user's, and the second would push that default back.
        LevelDisplayMode mode = IsSingleMode ? LevelDisplayMode.Single : LevelDisplayMode.Stacked;
        bool autoFollow = IsAutoAvailable && IsAutoEnabled;
        _surface.DisplayMode = mode;
        _surface.AutoLevelFollow = autoFollow;
    }

    // Puts a rebuilt surface back on the floor the user picked. Waits, rather than gives up, while the
    // surface has not resolved that floor yet: a new host builds its level set from the frames it sees.
    // Only while the surface is in Single with AutoFollow off, which is the state the pick left it in.
    private void RestorePick()
    {
        if (!_restorePick || _surface is null || _surface.AutoLevelFollow
            || _surface.DisplayMode != LevelDisplayMode.Single || _surface.Levels.ById(_pickedLevel) is null)
        {
            return;
        }

        _restorePick = false;
        if (!string.Equals(_surface.Levels.MapName, _pickedMap, StringComparison.OrdinalIgnoreCase)
            || _surface.ActiveLevelId == _pickedLevel)
        {
            return;
        }

        // Posted: the level set resolves inside the host's render pass, which is where this is raised
        // from, and a pick invalidates the visual.
        ILevelSurface surface = _surface;
        MapLevelId picked = _pickedLevel;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_surface, surface) && _pickedLevel == picked && !surface.AutoLevelFollow
                && surface.Levels.ById(picked) is not null)
            {
                surface.PickLevel(picked);
            }
        });
    }

    // Rebuilding the collection resets the ListBox/ItemsControl containers, which loses focus and blinks
    // the strip. The level set changes at most a handful of times per demo, so compare first.
    private bool SameChips(List<LevelChipViewModel> fresh)
    {
        if (Chips.Count != fresh.Count)
        {
            return false;
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            if (Chips[i].Id != fresh[i].Id || Chips[i].HasRadar != fresh[i].HasRadar ||
                !string.Equals(Chips[i].ZRange, fresh[i].ZRange, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
