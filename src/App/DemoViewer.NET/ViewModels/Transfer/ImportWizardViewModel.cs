#region

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.ViewModels.Transfer;

/// <summary>One conflict-policy choice on the import preview.</summary>
public sealed record ConflictPolicyOption(ConflictPolicy Value, string Title, string Description);

/// <summary>
///     The import wizard: Bundle → Preview → Demo files (only when the bundle carries .dem files the library
///     lacks) → Progress → Done. It reads the bundle through <see cref="ITransferImporter" /> and builds an
///     <see cref="ImportRequest" />; validating and writing are the importer's.
/// </summary>
public sealed partial class ImportWizardViewModel : TransferWizardViewModel
{
    private enum WizardStep
    {
        Bundle,
        Preview,
        DemFiles,
        Progress,
        Done
    }

    private static readonly string[] _stepState =
    [
        nameof(IsBundleStep), nameof(IsPreviewStep), nameof(IsDemFilesStep), nameof(IsProgressStep),
        nameof(IsDoneStep), nameof(CanGoBack), nameof(ShowNext), nameof(CanGoNext), nameof(ShowStart),
        nameof(CanStart), nameof(ShowClose), nameof(StepProgress), nameof(StepIndicatorText),
        nameof(ShowInFlightWarning), nameof(HasDemDestination)
    ];

    private readonly ITransferImporter _importer;

    private WizardStep[] _steps = [WizardStep.Bundle, WizardStep.Preview, WizardStep.Progress, WizardStep.Done];

    // The folder a run copied .dem files into, for Open folder on the Done step.
    private string? _demDestinationUsed;

    // Bumped per bundle load, so a slow preview of a folder the user has since replaced is dropped.
    private int _loadGeneration;
    private string? _loadingFolder;

    [ObservableProperty]
    private int _currentStep;

    /// <summary>The bundle folder, typed or picked.</summary>
    [ObservableProperty]
    private string _bundleFolder = "";

    /// <summary>True while the bundle's manifest is read and previewed.</summary>
    [ObservableProperty]
    private bool _isLoadingBundle;

    /// <summary>Why the bundle cannot be imported, in the user's terms, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBundleError))]
    private string? _bundleError;

    /// <summary>The preview of the loaded bundle, or null until one has loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(BundleSummary))]
    [NotifyPropertyChangedFor(nameof(PreviewCountsText))]
    [NotifyPropertyChangedFor(nameof(ShowStaleBanner))]
    [NotifyPropertyChangedFor(nameof(ConflictCount))]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    [NotifyPropertyChangedFor(nameof(PreviewWarnings))]
    [NotifyPropertyChangedFor(nameof(HasPreviewWarnings))]
    private ImportPreview? _preview;

    /// <summary>The chosen conflict policy (Keep mine by default).</summary>
    [ObservableProperty]
    private ConflictPolicyOption _selectedPolicy;

    /// <summary>Copy the .dem files into a new folder (true) or an existing library folder (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseLibraryRoot))]
    private bool _useNewFolder;

    [ObservableProperty]
    private string? _selectedLibraryRoot;

    [ObservableProperty]
    private string _newDemFolder = "";

    /// <param name="importer">Previews and runs the import.</param>
    /// <param name="libraryFolders">The library's folders, offered as .dem destinations.</param>
    /// <param name="post">Runs an action on the UI thread; tests pass an inline one.</param>
    /// <param name="demosInFlight">How many demos the processing queue holds right now.</param>
    /// <param name="openFolder">Opens the folder the .dem files went to.</param>
    public ImportWizardViewModel(
        ITransferImporter importer,
        Func<IReadOnlyList<string>> libraryFolders,
        Action<Action>? post = null,
        Func<int>? demosInFlight = null,
        Action<string>? openFolder = null)
        : base(post, demosInFlight, openFolder)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(libraryFolders);
        _importer = importer;

        Policies =
        [
            new ConflictPolicyOption(ConflictPolicy.KeepMine, "Keep mine",
                "Where this machine already has annotations or notes for a demo, keep them and skip the imported ones."),
            new ConflictPolicyOption(ConflictPolicy.TakeImported, "Take imported",
                "Replace this machine's annotations and notes with the imported ones."),
            new ConflictPolicyOption(ConflictPolicy.KeepBoth, "Keep both",
                "Keep both copies where the data allows it; elsewhere this keeps yours and says so in the summary.")
        ];
        _selectedPolicy = Policies[0];

        LibraryRoots = [.. libraryFolders()];
        _selectedLibraryRoot = LibraryRoots.Count > 0 ? LibraryRoots[0] : null;
        _useNewFolder = LibraryRoots.Count == 0;
    }

    /// <inheritdoc />
    public override string Title => "Import library data";

    /// <inheritdoc />
    protected override string DoneVerb => "imported";

    /// <inheritdoc />
    protected override string? SummaryFolder => _demDestinationUsed;

    /// <summary>The three conflict policies, Keep mine first.</summary>
    public IReadOnlyList<ConflictPolicyOption> Policies { get; }

    /// <summary>One row per demo in the bundle, with its status.</summary>
    public ObservableCollection<TransferDemoRowViewModel> PreviewRows { get; } = [];

    /// <summary>One checkable row per .dem file the bundle offers and the library lacks.</summary>
    public ObservableCollection<TransferDemoRowViewModel> DemRows { get; } = [];

    /// <summary>The library's folders.</summary>
    public IReadOnlyList<string> LibraryRoots { get; }

    /// <summary>True when the library has a folder to copy into.</summary>
    public bool HasLibraryRoots => LibraryRoots.Count > 0;

    /// <summary>The inverse of <see cref="UseNewFolder" />, for the first radio button.</summary>
    public bool UseLibraryRoot
    {
        get => !UseNewFolder;
        set => UseNewFolder = !value;
    }

    /// <summary>True when the bundle could not be read.</summary>
    public bool HasBundleError => !string.IsNullOrEmpty(BundleError);

    /// <summary>True once a bundle has previewed.</summary>
    public bool HasPreview => Preview is not null;

    /// <summary>Where the bundle came from: app version, source OS and date.</summary>
    public string BundleSummary => Preview is null
        ? ""
        : $"{Plural(Preview.Entries.Count, "demo")} exported {Preview.Manifest.CreatedUtc.ToLocalTime():g} "
          + $"on {Preview.Manifest.SourceOs}, app {Preview.Manifest.AppVersion}.";

    /// <summary>New, already here and conflicting counts.</summary>
    public string PreviewCountsText
    {
        get
        {
            if (Preview is null)
            {
                return "";
            }

            int fresh = Preview.Entries.Count(e => e.Status == ImportEntryStatus.New);
            int here = Preview.Entries.Count(e => e.Status == ImportEntryStatus.AlreadyHere);
            return $"{fresh} new · {here} already here · {ConflictCount} with your own notes · "
                   + $"{Bytes(Preview.DataBytes)} of data";
        }
    }

    /// <summary>Demos where this machine already has user data the bundle also carries.</summary>
    public int ConflictCount => Preview?.Entries.Count(e => e.Status == ImportEntryStatus.UserDataConflict) ?? 0;

    /// <summary>True when the conflict policy applies to anything.</summary>
    public bool HasConflicts => ConflictCount > 0;

    /// <summary>
    ///     The staleness banner: the source ran other rules or extension versions, so some imported data is
    ///     valid but will be recomputed here.
    /// </summary>
    public bool ShowStaleBanner => Preview?.AnyStale == true;

    /// <summary>The staleness banner's text.</summary>
    public string StaleBannerText { get; } =
        "The other machine used different rules or extension versions. Its data still imports, and the demos "
        + "marked stale are re-analysed here in the background.";

    /// <summary>The preview's warnings, blocking ones first.</summary>
    public IReadOnlyList<TransferWarning> PreviewWarnings =>
        Preview?.Warnings.Where(w => w.Kind != TransferWarningKind.DemosInFlight)
            .OrderByDescending(w => w.Blocking).ToList() ?? [];

    /// <summary>True when <see cref="PreviewWarnings" /> is not empty.</summary>
    public bool HasPreviewWarnings => PreviewWarnings.Count > 0;

    /// <summary>How many .dem rows are ticked.</summary>
    public int SelectedDemCount => DemRows.Count(r => r.IsChecked);

    /// <summary>The demo-files step's size note.</summary>
    public string DemFilesSummary =>
        $"{SelectedDemCount} of {Plural(DemRows.Count, ".dem file")} · {Bytes(DemRows.Where(r => r.IsChecked).Sum(r => r.SizeBytes))}";

    /// <summary>True when the chosen .dem destination is filled in.</summary>
    public bool HasDemDestination => UseNewFolder
        ? !string.IsNullOrWhiteSpace(NewDemFolder)
        : !string.IsNullOrWhiteSpace(SelectedLibraryRoot);

    // ── Steps ────────────────────────────────────────────────────────────────────────────────────

    private WizardStep Step => _steps[Math.Clamp(CurrentStep, 0, _steps.Length - 1)];

    private int LastInputStep => Array.IndexOf(_steps, WizardStep.Progress) - 1;

    /// <summary>The Bundle step.</summary>
    public bool IsBundleStep => Step == WizardStep.Bundle;

    /// <summary>The Preview step.</summary>
    public bool IsPreviewStep => Step == WizardStep.Preview;

    /// <summary>The Demo files step, present only when the bundle offers .dem files.</summary>
    public bool IsDemFilesStep => Step == WizardStep.DemFiles;

    /// <summary>The Progress step.</summary>
    public bool IsProgressStep => Step == WizardStep.Progress;

    /// <summary>The Done step.</summary>
    public bool IsDoneStep => Step == WizardStep.Done;

    /// <summary>True when the bundle's .dem files get a step of their own.</summary>
    public bool HasDemFilesStep => _steps.Contains(WizardStep.DemFiles);

    /// <summary>Back works up to Bundle, and never out of a run or its summary.</summary>
    public bool CanGoBack => CurrentStep > 0 && CurrentStep <= LastInputStep && !IsLoadingBundle;

    /// <summary>Next shows on every input step but the last, which has Start instead.</summary>
    public bool ShowNext => CurrentStep < LastInputStep;

    /// <summary>Whether this step's input is enough to move on.</summary>
    public bool CanGoNext => Step switch
    {
        WizardStep.Bundle => !IsLoadingBundle && Preview is not null && !HasBundleError,
        WizardStep.Preview => Preview?.CanRun == true,
        _ => false
    };

    /// <summary>The last input step's Import button.</summary>
    public bool ShowStart => CurrentStep == LastInputStep && Step != WizardStep.Bundle;

    /// <summary>The preview can run and, with .dem files ticked, they have somewhere to go.</summary>
    public bool CanStart => Preview is { CanRun: true } && Preview.Entries.Count > 0
        && (!HasDemFilesStep || SelectedDemCount == 0 || HasDemDestination);

    /// <summary>Close shows on every step but Progress.</summary>
    public bool ShowClose => Step != WizardStep.Progress;

    /// <summary>The header bar's fill.</summary>
    public double StepProgress => (double)(CurrentStep + 1) / _steps.Length;

    /// <summary>"Step 2 of 5" (or "of 4" without a Demo files step).</summary>
    public string StepIndicatorText => $"Step {CurrentStep + 1} of {_steps.Length}";

    /// <summary>The D4 warning on the Preview step.</summary>
    public bool ShowInFlightWarning => Step == WizardStep.Preview && (DemosInFlight
        || Preview?.Warnings.Any(w => w.Kind == TransferWarningKind.DemosInFlight) == true);

    // ── Changes ──────────────────────────────────────────────────────────────────────────────────

    partial void OnCurrentStepChanged(int value) => RaiseStepState();

    partial void OnIsLoadingBundleChanged(bool value) => RaiseStepState();

    partial void OnBundleErrorChanged(string? value) => RaiseStepState();

    partial void OnPreviewChanged(ImportPreview? value) => RaiseStepState();

    partial void OnUseNewFolderChanged(bool value) => RaiseStepState();

    partial void OnSelectedLibraryRootChanged(string? value) => RaiseStepState();

    partial void OnNewDemFolderChanged(string value) => RaiseStepState();

    // A new path drops the old preview: Next must not carry on with a bundle that is no longer named.
    partial void OnBundleFolderChanged(string value)
    {
        string folder = value.Trim();
        if (Preview is not null && !string.Equals(Preview.BundleFolder, folder, StringComparison.Ordinal))
        {
            SetPreview(null);
        }

        // Nor may a load still running for the old path land afterwards.
        if (IsLoadingBundle && !string.Equals(_loadingFolder, folder, StringComparison.Ordinal))
        {
            _loadGeneration++;
            _loadingFolder = null;
            IsLoadingBundle = false;
        }

        BundleError = null;
    }

    private void RaiseStepState()
    {
        foreach (string name in _stepState)
        {
            OnPropertyChanged(name);
        }
    }

    private void OnDemRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferDemoRowViewModel.IsChecked))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedDemCount));
        OnPropertyChanged(nameof(DemFilesSummary));
        RaiseStepState();
    }

    // ── Bundle ───────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BrowseBundleAsync()
    {
        if (await PickFolderAsync("Choose an export folder to import") is { Length: > 0 } folder)
        {
            BundleFolder = folder;
            await LoadBundleAsync(folder);
        }
    }

    /// <summary>The Bundle step's Check button, for a typed path.</summary>
    [RelayCommand]
    private Task CheckBundle() => LoadBundleAsync(BundleFolder);

    /// <summary>
    ///     Reads and previews <paramref name="folder" /> straight away. A bad bundle or another app version is
    ///     refused here with the importer's message, before any other step.
    /// </summary>
    public async Task LoadBundleAsync(string folder)
    {
        folder = folder.Trim();
        int generation = ++_loadGeneration;
        SetPreview(null);
        BundleError = null;
        if (folder.Length == 0)
        {
            BundleError = "Choose the export folder, the one that holds transfer.json.";
            return;
        }

        if (!Directory.Exists(folder))
        {
            BundleError = "That folder does not exist.";
            return;
        }

        _loadingFolder = folder;
        IsLoadingBundle = true;
        try
        {
            ImportPreview preview = await Task.Run(() => _importer.PreviewAsync(folder, CancellationToken.None));
            if (generation == _loadGeneration)
            {
                SetPreview(preview);
            }
        }
        catch (TransferManifestException ex)
        {
            if (generation == _loadGeneration)
            {
                BundleError = ex.Message;
            }
        }
#pragma warning disable CA1031 // an unreadable folder is reported on the step, like a bad manifest
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (generation == _loadGeneration)
            {
                BundleError = "Could not read that folder: " + ex.Message;
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoadingBundle = false;
            }
        }
    }

    private void SetPreview(ImportPreview? preview)
    {
        foreach (TransferDemoRowViewModel row in DemRows)
        {
            row.PropertyChanged -= OnDemRowChanged;
        }

        PreviewRows.Clear();
        DemRows.Clear();
        if (preview is not null)
        {
            foreach (ImportPreviewEntry entry in preview.Entries)
            {
                PreviewRows.Add(new TransferDemoRowViewModel(entry.Sha256, entry.Demo.OriginalFileName,
                    entry.Demo.Map, entry.Demo.SizeBytes, StatusOf(entry)));
                if (entry.HasDem && !entry.DemAlreadyInLibrary)
                {
                    TransferDemoRowViewModel dem = new(entry.Sha256, entry.Demo.OriginalFileName, entry.Demo.Map,
                        entry.Demo.Dem?.Size ?? entry.Demo.SizeBytes, isChecked: true);
                    dem.PropertyChanged += OnDemRowChanged;
                    DemRows.Add(dem);
                }
            }
        }

        _steps = preview?.OffersDemFiles == true && DemRows.Count > 0
            ? [WizardStep.Bundle, WizardStep.Preview, WizardStep.DemFiles, WizardStep.Progress, WizardStep.Done]
            : [WizardStep.Bundle, WizardStep.Preview, WizardStep.Progress, WizardStep.Done];
        Preview = preview;
        OnPropertyChanged(nameof(HasDemFilesStep));
        OnPropertyChanged(nameof(SelectedDemCount));
        OnPropertyChanged(nameof(DemFilesSummary));
        RaiseStepState();
    }

    private static string StatusOf(ImportPreviewEntry entry)
    {
        string status = entry.Status switch
        {
            ImportEntryStatus.New => "New",
            ImportEntryStatus.AlreadyHere => "Already here",
            ImportEntryStatus.UserDataConflict => "Your notes differ",
            _ => entry.Status.ToString()
        };
        return entry.Stale ? status + " · stale" : status;
    }

    // ── Demo files ───────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAllDems()
    {
        foreach (TransferDemoRowViewModel row in DemRows)
        {
            row.IsChecked = true;
        }
    }

    [RelayCommand]
    private void ClearDems()
    {
        foreach (TransferDemoRowViewModel row in DemRows)
        {
            row.IsChecked = false;
        }
    }

    [RelayCommand]
    private async Task BrowseNewDemFolderAsync()
    {
        if (await PickFolderAsync("Choose a folder for the demo files") is { Length: > 0 } folder)
        {
            NewDemFolder = folder;
            UseNewFolder = true;
        }
    }

    // ── Navigation and run ───────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Next()
    {
        if (CanGoNext && ShowNext)
        {
            CurrentStep++;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
        {
            CurrentStep--;
        }
    }

    /// <summary>The request the current choices make.</summary>
    public ImportRequest BuildRequest()
    {
        if (Preview is null)
        {
            throw new InvalidOperationException("No bundle is loaded.");
        }

        HashSet<string> dems = HasDemFilesStep
            ? DemRows.Where(r => r.IsChecked).Select(r => r.Key).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (dems.Count == 0)
        {
            return ImportRequest.DataOnly(Preview.BundleFolder, SelectedPolicy.Value);
        }

        string destination = UseNewFolder ? NewDemFolder.Trim() : SelectedLibraryRoot!.Trim();
        return new ImportRequest(Preview.BundleFolder, SelectedPolicy.Value, dems, destination, UseNewFolder);
    }

    /// <summary>Runs the import.</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (!ShowStart || !CanStart || Preview is not { } preview)
        {
            return;
        }

        ImportRequest request = BuildRequest();
        _demDestinationUsed = request.DemFilesToCopy.Count > 0 ? request.DemDestination : null;
        CurrentStep = Array.IndexOf(_steps, WizardStep.Progress);
        await RunTransferAsync(preview.BundleFolder,
            (progress, ct) => _importer.RunAsync(request, preview, progress, ct));
    }

    /// <inheritdoc />
    protected override void OnRunFinished() => CurrentStep = Array.IndexOf(_steps, WizardStep.Done);
}
