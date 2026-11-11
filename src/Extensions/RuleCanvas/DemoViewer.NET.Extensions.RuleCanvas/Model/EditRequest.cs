namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     One edit the canvas wants made: a text-to-text transform through the splice writer and the words
///     for it, or a refusal with its reason. A refusal with no reason is one the surface stays quiet about,
///     because the gesture could not reach anything to edit.
/// </summary>
/// <param name="Transform">The transform, or null for a refusal.</param>
/// <param name="Description">What the edit does, for the status when it fails: <c>set round_kills.per</c>.</param>
/// <param name="Refusal">Why nothing is written, or null.</param>
public sealed record EditRequest(Func<string, string>? Transform, string Description, string? Refusal = null)
{
    public bool IsRefused => Transform is null;

    public static EditRequest Of(Func<string, string> transform, string description) => new(transform, description);

    public static EditRequest Refuse(string reason) => new(null, "", reason);

    /// <summary>A refusal the surface does not report: nothing to edit under the gesture.</summary>
    public static EditRequest Ignore { get; } = new(null, "");
}
