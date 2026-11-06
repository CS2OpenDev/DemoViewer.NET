#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The Canvas section. Nothing to draw yet: the view is the empty state that says what the canvas will
///     be and where rulesets are authored meanwhile.
/// </summary>
/// <param name="selectTab">Selects a host tab by id; the Authoring tab's id is <see cref="AuthoringTabId" />.</param>
public sealed partial class CanvasTabViewModel(Func<string, bool> selectTab) : ExtensionViewModel, IWorkspaceTabViewModel
{
    /// <summary>The Rule Workbench's tab id on the main strip, as the host declares it.</summary>
    public const string AuthoringTabId = "ruleworkbench.editor";

    private readonly Func<string, bool> _selectTab = selectTab ?? throw new ArgumentNullException(nameof(selectTab));

    /// <summary>True after a click found no Authoring tab to select: it is switched off, or the host has none.</summary>
    [ObservableProperty]
    private bool _authoringUnavailable;

    public void OnActivated(IModuleContext context)
    {
    }

    public void OnDeactivated()
    {
    }

    [RelayCommand]
    private void OpenAuthoring() => AuthoringUnavailable = !_selectTab(AuthoringTabId);
}
