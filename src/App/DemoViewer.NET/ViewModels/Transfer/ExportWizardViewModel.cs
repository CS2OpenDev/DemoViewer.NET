#region

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Transfer;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Transfer;

/// <summary>
///     The export wizard: Demos → Options → Destination → Review → Progress → Done. It only builds an
///     <see cref="ExportRequest" /> and hands it to <see cref="ITransferExporter" />; planning, target checks and
///     copying are the exporter's.
/// </summary>
public sealed partial class ExportWizardViewModel : TransferWizardViewModel
{
    private enum WizardStep
    {
        Demos,
        Options,
        Destination,
        Review,
        Progress,
        Done
    }

    private static readonly WizardStep[] _steps =
    [
        WizardStep.Demos, WizardStep.Options, WizardStep.Destination, WizardStep.Review, WizardStep.Progress,
        WizardStep.Done
    ];

    // Everything the footer and step panels read, raised together whenever the step, the selection or the
    // plan changes. Cheaper to reason about than a NotifyPropertyChangedFor on every source.
    private static readonly string[] _stepState =
    [
        nameof(IsDemosStep), nameof(IsOptionsStep), nameof(IsDestinationStep), nameof(IsReviewStep),
        nameof(IsProgressStep), nameof(IsDoneStep), nameof(CanGoBack), nameof(ShowNext), nameof(CanGoNext),
        nameof(ShowStart), nameof(CanStart), nameof(ShowClose), nameof(StepProgress), nameof(StepIndicatorText),
        nameof(ShowInFlightWarning)
    ];

    private readonly ITransferExporter _exporter;
    private readonly Func<DateTime> _now;

    [ObservableProperty]
    private int _currentStep;

    /// <summary>"All library demos" (true) or "Choose…" (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseDemos))]
    [NotifyPropertyChangedFor(nameof(DemFilesSizeText))]
    private bool _exportAll = true;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _includeDemFiles;

    /// <summary>Annotations and other user-authored data. On by default.</summary>
    [ObservableProperty]
    private bool _includeUserData = true;

    /// <summary>The extensions' library-wide data (strats, teams, lineups, notes). On by default.</summary>
    [ObservableProperty]
    private bool _includeLibraryData = true;

    /// <summary>The folder the bundle folder is created in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BundleFolderPreview))]
    private string _destinationFolder = "";

    /// <summary>True while the exporter plans the Review step.</summary>
    [ObservableProperty]
    private bool _isPlanning;

    /// <summary>The plan the Review step shows and Start runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IncludedCount))]
    [NotifyPropertyChangedFor(nameof(NotHashedCount))]
    [NotifyPropertyChangedFor(nameof(MissingCount))]
    [NotifyPropertyChangedFor(nameof(HasMissing))]
    [NotifyPropertyChangedFor(nameof(InFlightCount))]
    [NotifyPropertyChangedFor(nameof(NotHashedNames))]
    [NotifyPropertyChangedFor(nameof(HasNotHashed))]
    [NotifyPropertyChangedFor(nameof(ReviewHeadline))]
    [NotifyPropertyChangedFor(nameof(ReviewBytesText))]
    [NotifyPropertyChangedFor(nameof(PlanWarnings))]
    [NotifyPropertyChangedFor(nameof(HasPlanWarnings))]
    [NotifyPropertyChangedFor(nameof(ShowLibraryDataSize))]
    [NotifyPropertyChangedFor(nameof(LibraryDataSizeText))]
    private ExportPlan? _plan;

    /// <param name="exporter">Plans and runs the export.</param>
    /// <param name="libraryDemos">The library's demos, read once for the Choose list.</param>
    /// <param name="post">Runs an action on the UI thread; tests pass an inline one.</param>
    /// <param name="demosInFlight">How many demos the processing queue holds right now.</param>
    /// <param name="openFolder">Opens the finished bundle in the file manager.</param>
    /// <param name="now">The clock for the bundle folder name.</param>
    /// <param name="libraryDataExtensions">The names of the enabled extensions that carry library-wide data.</param>
    public ExportWizardViewModel(
        ITransferExporter exporter,
        Func<IReadOnlyList<DemoEntry>> libraryDemos,
        Action<Action>? post = null,
        Func<int>? demosInFlight = null,
        Action<string>? openFolder = null,
        Func<DateTime>? now = null,
        IReadOnlyList<string>? libraryDataExtensions = null)
        : base(post, demosInFlight, openFolder)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(libraryDemos);
        _exporter = exporter;
        _now = now ?? (() => DateTime.Now);
        LibraryDataExtensions = libraryDataExtensions ?? [];

        IReadOnlyList<DemoEntry> demos = libraryDemos();
        Rows = [.. demos.Select(e => new TransferDemoRowViewModel(e))];
        foreach (TransferDemoRowViewModel row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }

        foreach ((string display, string mapKey) in LibraryFilterPredicate.MapGroups(demos))
        {
            MapFilterItem chip = new(display, mapKey);
            chip.PropertyChanged += (_, _) => ApplyFilter();
            MapFilters.Add(chip);
        }

        ApplyFilter();
    }

    /// <inheritdoc />
    public override string Title => "Export library data";

    /// <inheritdoc />
    protected override string DoneVerb => "exported";

    /// <summary>Every library demo, one row each.</summary>
    public IReadOnlyList<TransferDemoRowViewModel> Rows { get; }

    /// <summary>The rows the search and map chips leave.</summary>
    public ObservableCollection<TransferDemoRowViewModel> FilteredRows { get; } = [];

    /// <summary>One chip per map in the library.</summary>
    public ObservableCollection<MapFilterItem> MapFilters { get; } = [];

    /// <summary>The inverse of <see cref="ExportAll" />, for the second radio button.</summary>
    public bool ChooseDemos
    {
        get => !ExportAll;
        set => ExportAll = !value;
    }

    /// <summary>True when the library has any demos.</summary>
    public bool HasLibraryDemos => Rows.Count > 0;

    /// <summary>How many rows are ticked.</summary>
    public int SelectedCount => Rows.Count(r => r.IsChecked);

    /// <summary>"3 of 120 demos selected".</summary>
    public string SelectionSummary => $"{SelectedCount} of {Plural(Rows.Count, "demo")} selected";

    /// <summary>The Options step's size note for the .dem checkbox.</summary>
    public string DemFilesSizeText
    {
        get
        {
            IEnumerable<TransferDemoRowViewModel> chosen = ExportAll ? Rows : Rows.Where(r => r.IsChecked);
            return $"Adds about {Bytes(chosen.Sum(r => r.SizeBytes))}.";
        }
    }

    /// <summary>The enabled extensions that carry library-wide data, by name.</summary>
    public IReadOnlyList<string> LibraryDataExtensions { get; }

    /// <summary>The Options step shows the library data choice only when some extension has any.</summary>
    public bool HasLibraryDataOption => LibraryDataExtensions.Count > 0;

    /// <summary>The Options step's note under the library data checkbox: who contributes and how it travels.</summary>
    public string LibraryDataSourcesText =>
        $"From {string.Join(", ", LibraryDataExtensions)}. Carried whole, even when only some demos are exported.";

    /// <summary>The Review step shows the library data size when the plan carries any.</summary>
    public bool ShowLibraryDataSize => Plan is { Request.IncludeLibraryData: true, LibraryBytes: > 0 };

    /// <summary>"Extension library data: about 2.1 MB."</summary>
    public string LibraryDataSizeText => Plan is { } plan
        ? $"Extension library data: about {Bytes(plan.LibraryBytes)}."
        : "";

    /// <summary>The bundle folder a run would create, for the Destination step.</summary>
    public string BundleFolderPreview => string.IsNullOrWhiteSpace(DestinationFolder)
        ? ""
        : Path.Combine(DestinationFolder.Trim(), TransferLayout.BundleFolderName(_now()));

    // ── Steps ────────────────────────────────────────────────────────────────────────────────────

    private WizardStep Step => _steps[Math.Clamp(CurrentStep, 0, _steps.Length - 1)];

    /// <summary>The Demos step.</summary>
    public bool IsDemosStep => Step == WizardStep.Demos;

    /// <summary>The Options step.</summary>
    public bool IsOptionsStep => Step == WizardStep.Options;

    /// <summary>The Destination step.</summary>
    public bool IsDestinationStep => Step == WizardStep.Destination;

    /// <summary>The Review step.</summary>
    public bool IsReviewStep => Step == WizardStep.Review;

    /// <summary>The Progress step.</summary>
    public bool IsProgressStep => Step == WizardStep.Progress;

    /// <summary>The Done step.</summary>
    public bool IsDoneStep => Step == WizardStep.Done;

    /// <summary>Back goes as far as Demos, and never out of a run or its summary.</summary>
    public bool CanGoBack => Step is WizardStep.Options or WizardStep.Destination or WizardStep.Review
        && !IsPlanning;

    /// <summary>Next shows up to the Destination step; Review has Start instead.</summary>
    public bool ShowNext => Step is WizardStep.Demos or WizardStep.Options or WizardStep.Destination;

    /// <summary>Whether this step's input is enough to move on.</summary>
    public bool CanGoNext => !IsPlanning && Step switch
    {
        WizardStep.Demos => ExportAll ? HasLibraryDemos : SelectedCount > 0,
        WizardStep.Options => true,
        WizardStep.Destination => !string.IsNullOrWhiteSpace(DestinationFolder),
        _ => false
    };

    /// <summary>The Review step's Export button.</summary>
    public bool ShowStart => Step == WizardStep.Review;

    /// <summary>The plan has something to copy and nothing blocking.</summary>
    public bool CanStart => Plan is { CanRun: true } plan
        && plan.Entries.Any(e => e.ExportsData || (plan.Request.IncludeDemFiles && e.DemBytes > 0));

    /// <summary>Close shows on every step but Progress, where Cancel stops the run instead.</summary>
    public bool ShowClose => Step != WizardStep.Progress;

    /// <summary>The header bar's fill.</summary>
    public double StepProgress => (double)(CurrentStep + 1) / _steps.Length;

    /// <summary>"Step 2 of 6".</summary>
    public string StepIndicatorText => $"Step {CurrentStep + 1} of {_steps.Length}";

    // ── Review ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Demos whose analysis data goes into the bundle (in-flight ones included, best effort).</summary>
    public int IncludedCount => Plan?.Entries.Count(e => e.ExportsData) ?? 0;

    /// <summary>Demos not analysed yet, so with no data to export.</summary>
    public int NotHashedCount => Count(ExportEntryStatus.NotHashed);

    /// <summary>Demos the library lists but whose files are gone.</summary>
    public int MissingCount => Count(ExportEntryStatus.Missing);

    /// <summary>True when the plan found missing demo files.</summary>
    public bool HasMissing => MissingCount > 0;

    /// <summary>Demos the queue is working on now.</summary>
    public int InFlightCount => Count(ExportEntryStatus.InFlight);

    /// <summary>The not-yet-analysed demos by name.</summary>
    public IReadOnlyList<string> NotHashedNames =>
        Plan?.Entries.Where(e => e.Status == ExportEntryStatus.NotHashed).Select(e => e.DisplayName).ToList()
        ?? [];

    /// <summary>True when the review lists demos it cannot export data for.</summary>
    public bool HasNotHashed => NotHashedCount > 0;

    /// <summary>The review's first line.</summary>
    public string ReviewHeadline
    {
        get
        {
            if (Plan is null)
            {
                return "";
            }

            string text = $"{Plural(IncludedCount, "demo")} with analysis data";
            if (Plan.Request.IncludeDemFiles)
            {
                int dems = Plan.Entries.Count(e => e.DemBytes > 0);
                text += $", {Plural(dems, ".dem file")}";
            }

            return text + ".";
        }
    }

    /// <summary>The bytes to copy and where.</summary>
    public string ReviewBytesText => Plan is null
        ? ""
        : $"About {Bytes(Plan.TotalBytes)} into {Plan.BundleFolder}";

    /// <summary>The plan's warnings, blocking ones first.</summary>
    public IReadOnlyList<TransferWarning> PlanWarnings =>
        Plan?.Warnings.Where(w => w.Kind != TransferWarningKind.DemosInFlight)
            .OrderByDescending(w => w.Blocking).ToList() ?? [];

    /// <summary>True when <see cref="PlanWarnings" /> is not empty.</summary>
    public bool HasPlanWarnings => PlanWarnings.Count > 0;

    /// <summary>
    ///     The D4 warning: shown when the queue is busy now, or the plan found demos in flight. Those are still
    ///     exported as they stand when the export reaches them.
    /// </summary>
    public bool ShowInFlightWarning => Step == WizardStep.Review && (DemosInFlight || InFlightCount > 0
        || Plan?.Warnings.Any(w => w.Kind == TransferWarningKind.DemosInFlight) == true);

    private int Count(ExportEntryStatus status) => Plan?.Entries.Count(e => e.Status == status) ?? 0;

    // ── Changes ──────────────────────────────────────────────────────────────────────────────────

    partial void OnCurrentStepChanged(int value) => RaiseStepState();

    partial void OnExportAllChanged(bool value) => RaiseStepState();

    partial void OnDestinationFolderChanged(string value) => RaiseStepState();

    partial void OnIsPlanningChanged(bool value) => RaiseStepState();

    partial void OnPlanChanged(ExportPlan? value) => RaiseStepState();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferDemoRowViewModel.IsChecked))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(DemFilesSizeText));
        RaiseStepState();
    }

    private void RaiseStepState()
    {
        foreach (string name in _stepState)
        {
            OnPropertyChanged(name);
        }
    }

    private void ApplyFilter()
    {
        LibraryFilterPredicate predicate = LibraryFilterPredicate.From(SearchText, MapFilters, null);
        FilteredRows.Clear();
        foreach (TransferDemoRowViewModel row in Rows)
        {
            if (row.Entry is null || predicate.Matches(row.Entry))
            {
                FilteredRows.Add(row);
            }
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ticks every row the filter shows, leaving rows it hides as they were.</summary>
    [RelayCommand]
    private void SelectAllFiltered()
    {
        foreach (TransferDemoRowViewModel row in FilteredRows)
        {
            row.IsChecked = true;
        }
    }

    /// <summary>Unticks every row, shown or not.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (TransferDemoRowViewModel row in Rows)
        {
            row.IsChecked = false;
        }
    }

    [RelayCommand]
    private void ClearMapFilters()
    {
        foreach (MapFilterItem chip in MapFilters)
        {
            chip.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        if (await PickFolderAsync("Choose where to save the export") is { Length: > 0 } folder)
        {
            DestinationFolder = folder;
        }
    }

    /// <summary>Moves on one step. Leaving Destination plans the export, which also checks the target.</summary>
    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        ErrorText = null;
        if (Step == WizardStep.Destination)
        {
            if (await PlanNowAsync())
            {
                CurrentStep++;
            }

            return;
        }

        CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
        {
            ErrorText = null;
            CurrentStep--;
        }
    }

    /// <summary>Runs the planned export.</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart || Plan is not { } plan)
        {
            return;
        }

        CurrentStep = Array.IndexOf(_steps, WizardStep.Progress);
        await RunTransferAsync(plan.BundleFolder, (progress, ct) => _exporter.RunAsync(plan, progress, ct));
    }

    /// <inheritdoc />
    protected override void OnRunFinished() => CurrentStep = Array.IndexOf(_steps, WizardStep.Done);

    /// <summary>The request the current choices make.</summary>
    public ExportRequest BuildRequest()
    {
        string destination = DestinationFolder.Trim();
        bool library = IncludeLibraryData && HasLibraryDataOption;
        return ExportAll
            ? ExportRequest.All(destination, IncludeDemFiles, IncludeUserData, library)
            : ExportRequest.Chosen([.. Rows.Where(r => r.IsChecked).Select(r => r.Key)], destination,
                IncludeDemFiles, IncludeUserData, library);
    }

    private async Task<bool> PlanNowAsync()
    {
        IsPlanning = true;
        Plan = null;
        try
        {
            ExportRequest request = BuildRequest();
            Plan = await Task.Run(() => _exporter.PlanAsync(request, CancellationToken.None));
            return true;
        }
#pragma warning disable CA1031 // the step shows the reason and stays put
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = "Could not prepare the export: " + ex.Message;
            return false;
        }
        finally
        {
            IsPlanning = false;
        }
    }
}
