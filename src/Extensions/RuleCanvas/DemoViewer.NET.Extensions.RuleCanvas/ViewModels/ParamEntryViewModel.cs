#region

using System.Globalization;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One <c>params:</c> entry in the Inspector's PARAMS section: its type as a picker over the language's
///     types, its <c>min</c> and <c>max</c> as text. The default stays a row on the node. Every write goes
///     through the Inspector under the key the semantics address it by (<c>param.&lt;name&gt;.min</c>).
/// </summary>
public sealed class ParamEntryViewModel
{
    public ParamEntryViewModel(ParamDef param, bool editable, Action<InspectorFieldViewModel, string, string?> write)
    {
        ArgumentNullException.ThrowIfNull(param);
        ArgumentNullException.ThrowIfNull(write);
        Name = param.Name;
        Type = new InspectorFieldViewModel("type", "type", (field, value) => write(field, Key("type"), value), CanvasEditSemantics.ParamTypeOptions)
        {
            IsEditable = editable
        };
        Type.Load(param.Type == ParamType.None ? null : param.Type.ToString().ToLowerInvariant());
        Min = new InspectorFieldViewModel("min", "min", (field, value) => write(field, Key("min"), value), hint: "inclusive lower bound; empty for none") { IsEditable = editable };
        Min.Load(param.Min?.ToString(CultureInfo.InvariantCulture));
        Max = new InspectorFieldViewModel("max", "max", (field, value) => write(field, Key("max"), value), hint: "inclusive upper bound; empty for none") { IsEditable = editable };
        Max.Load(param.Max?.ToString(CultureInfo.InvariantCulture));
        Default = CanvasNodeFactory.ParamText(param.Default) ?? "";
        IsEditable = editable;
    }

    public string Name { get; }

    /// <summary>The default as the file spells it, for the row's header; it is edited on the node.</summary>
    public string Default { get; }

    public InspectorFieldViewModel Type { get; }

    public InspectorFieldViewModel Min { get; }

    public InspectorFieldViewModel Max { get; }

    public bool IsEditable { get; }

    private string Key(string property) => $"param.{Name}.{property}";
}
