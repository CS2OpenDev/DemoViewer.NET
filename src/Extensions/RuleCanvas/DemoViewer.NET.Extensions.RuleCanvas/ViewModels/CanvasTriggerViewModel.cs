#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The trigger picker on a stat face: the catalog's views beside their wire event, every event under
///     <c>raw.</c>, and the open document's defines. It shows the spelling the author wrote and writes the
///     key the kind keeps its trigger under: the kind's own key for a count, a streak, a bucket or a burst,
///     <c>on:</c> for the rest. The chip beside it carries the resolved view and the wire events as its tip.
/// </summary>
public sealed partial class CanvasTriggerViewModel : ObservableObject
{
    private readonly Action<string, string?> _write;
    private readonly bool _loading;

    /// <param name="key">The YAML key the picker writes: <c>count</c>, <c>streak</c>, <c>bucket</c>, <c>burst</c> or <c>on</c>.</param>
    /// <param name="spelling">The trigger as the author wrote it (<c>kill</c>, <c>raw.bomb_exploded</c>, a define), or null for none yet.</param>
    /// <param name="resolvedView">The catalog view the resolver reached, or null.</param>
    /// <param name="events">The wire events the trigger fires on.</param>
    /// <param name="options">What the picker offers, the current spelling among them.</param>
    /// <param name="write">Writes the key; the value is never null here.</param>
    /// <param name="prefix">The word before the spelling on the chip: <c>on</c>, or <c>off</c> for a flag's deactivator.</param>
    public CanvasTriggerViewModel(string key, string? spelling, string? resolvedView, IReadOnlyList<string> events,
        IReadOnlyList<TriggerOption> options, Action<string, string?> write, string prefix = "on")
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        _loading = true;
        Key = key;
        Prefix = prefix;
        Spelling = spelling ?? "";
        ResolvedView = resolvedView;
        Events = events;
        Options = options;
        _write = write;
        _selected = options.FirstOrDefault(o => string.Equals(o.Written, Spelling, StringComparison.Ordinal));
        _loading = false;
    }

    public string Key { get; }

    /// <summary>The trigger as written; empty when the entry has none yet.</summary>
    public string Spelling { get; }

    public string? ResolvedView { get; }

    public IReadOnlyList<string> Events { get; }

    public IReadOnlyList<TriggerOption> Options { get; }

    /// <summary>The word before the spelling: <c>on</c>, or <c>off</c> on a flag's deactivator row.</summary>
    public string Prefix { get; }

    /// <summary>The header chip: the prefix and the spelling, or empty when there is none.</summary>
    public string Chip => Spelling.Length == 0 ? "" : Prefix + " " + Spelling;

    /// <summary>Whether the spelling names a define of the open document, drawn as the node in front of this one.</summary>
    public bool IsDefine { get; init; }

    /// <summary>What the chip and the picker say on hover: the view it resolves to and the events it fires on.</summary>
    public string Tip
    {
        get
        {
            string fires = Events.Count == 0 ? "fires on no wire event" : "fires on " + string.Join(", ", Events);
            if (IsDefine)
            {
                string resolves = ResolvedView is { } resolved ? $"; resolves to the view {resolved}" : "";
                return $"{Spelling} is a define of this ruleset, drawn as the node in front{resolves}; {fires}";
            }

            return ResolvedView is { } view && !string.Equals(view, Spelling, StringComparison.Ordinal)
                ? $"{Spelling} resolves to the view {view}; {fires}"
                : $"{Spelling}: {fires}";
        }
    }

    [ObservableProperty]
    private TriggerOption? _selected;

    [ObservableProperty]
    private bool _isEditable;

    // The loaded selection never writes; a pick that names what the file already says writes nothing either.
    partial void OnSelectedChanged(TriggerOption? value)
    {
        if (!_loading && IsEditable && value is not null && !string.Equals(value.Written, Spelling, StringComparison.Ordinal))
        {
            _write(Key, value.Written);
        }
    }
}
