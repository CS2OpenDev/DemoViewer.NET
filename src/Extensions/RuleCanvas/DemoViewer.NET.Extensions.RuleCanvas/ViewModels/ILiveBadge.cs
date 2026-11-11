namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Something that carries a live value badge the tab writes: a node's header, or a show node's column
///     row, which reads the surfaced stat's track so a table reads as a table.
/// </summary>
public interface ILiveBadge
{
    /// <summary>The badge's text; empty hides it.</summary>
    string LiveBadge { set; }

    /// <summary>What the badge says on hover.</summary>
    string LiveTip { set; }

    /// <summary>True when the badge comes from a run over text that has since changed.</summary>
    bool IsLiveStale { set; }
}
