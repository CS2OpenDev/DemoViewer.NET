#region

using DemoViewer.NET.Extensions.Sdk.Ui;

#endregion

namespace ThirdPartyFake.ViewModels;

/// <summary>Hosted in a bare ContentControl, so it shows only if the host finds its view by naming.</summary>
public sealed class FakeDetailViewModel : ExtensionViewModel
{
    public string Title
    {
        get;
        set => SetProperty(ref field, value);
    } = "Resolved from ThirdPartyFake.Views";

    public string Notes { get; } =
        "Drawn with the **host's** tokens and classes.\n\n- `TextMid` label\n- `ghost` and `primary` buttons";
}
