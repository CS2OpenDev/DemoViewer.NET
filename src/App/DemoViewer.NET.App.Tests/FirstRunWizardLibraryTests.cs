#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.Setup;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The wizard's folders reaching the LIVE library. The app builds the library service with the shell,
///     before the wizard opens, so every case here constructs it first and only then runs the wizard over the
///     same temp-dir <see cref="SettingsService" />. A wizard that only wrote settings.json passed every older
///     test and still left the Library tab empty until a restart.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class FirstRunWizardLibraryTests
{
    private static readonly Action<Action> _inline = a => a();

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvwiz_lib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (string dir in dirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static FirstRunWizardViewModel Wizard(SettingsService settings, string? detected) =>
        new(settings, () => new Cs2DemosLookup(detected, []), packs: []);

    private static LibraryTabViewModel LibraryTab(DemoLibraryService library) =>
        new(library, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]));

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    [Test]
    public async Task DetectedFolder_AddedInWizard_IsInTheLibraryTabAtOnce_ScannedAndKeptOnRestart()
    {
        string cfgDir = NewTempDir();
        string replays = NewTempDir();
        await File.WriteAllBytesAsync(Path.Combine(replays, "match.dem"), [1, 2, 3]);
        string libJson = Path.Combine(cfgDir, "library.json");
        try
        {
            SettingsService settings = new(cfgDir);
            using DemoLibraryService library = new(_inline, libJson, settings);
            LibraryTabViewModel tab = LibraryTab(library);
            await Assert.That(tab.HasNoFolders).IsTrue();

            FirstRunWizardViewModel wizard = Wizard(settings, replays);
            wizard.AddDetectedFolderCommand.Execute(null);
            wizard.AddDetectedFolderCommand.Execute(null);
            wizard.FinishCommand.Execute(null);

            await Assert.That(tab.Folders.Count(f => f == replays)).IsEqualTo(1)
                .Because("the Library tab shows the wizard's folder without a restart, once");
            await Assert.That(tab.HasNoFolders).IsFalse();
            await WaitForAsync(() => library.Entries.Count == 1, "the wizard's folder to be scanned");

            SettingsService restarted = new(cfgDir);
            using DemoLibraryService relaunched = new(_inline, libJson, restarted);
            await Assert.That(relaunched.Folders.Count(f => f == replays)).IsEqualTo(1)
                .Because("the folder survives a restart");
        }
        finally
        {
            Cleanup(cfgDir, replays);
        }
    }

    // The second half of the original report: the library's stale, empty list was written back over the
    // wizard's on the next Library-tab add, so the wizard's folder was lost for good.
    [Test]
    public async Task LibraryTabAdd_AfterTheWizard_KeepsTheWizardsFolder()
    {
        string cfgDir = NewTempDir();
        string replays = NewTempDir();
        string other = NewTempDir();
        try
        {
            SettingsService settings = new(cfgDir);
            using DemoLibraryService library = new(_inline, Path.Combine(cfgDir, "library.json"), settings);

            FirstRunWizardViewModel wizard = Wizard(settings, replays);
            wizard.AddDetectedFolderCommand.Execute(null);
            wizard.FinishCommand.Execute(null);

            await library.AddFoldersAsync([other]);

            await Assert.That(settings.Current.Library.Folders).IsEquivalentTo([replays, other]);
            await Assert.That(library.Folders).IsEquivalentTo([replays, other]);
        }
        finally
        {
            Cleanup(cfgDir, replays, other);
        }
    }

    [Test]
    public async Task Skip_AppliesNoPendingFolder()
    {
        string cfgDir = NewTempDir();
        string replays = NewTempDir();
        try
        {
            SettingsService settings = new(cfgDir);
            using DemoLibraryService library = new(_inline, Path.Combine(cfgDir, "library.json"), settings);

            FirstRunWizardViewModel wizard = Wizard(settings, replays);
            wizard.AddDetectedFolderCommand.Execute(null);
            wizard.SkipCommand.Execute(null);

            await Assert.That(settings.NeedsFirstRun).IsFalse();
            await Assert.That(library.Folders.Count).IsEqualTo(0)
                .Because("Skip applies no choice, so a folder picked before it is dropped");
            await Assert.That(settings.Current.Library.Folders.Length).IsEqualTo(0);
        }
        finally
        {
            Cleanup(cfgDir, replays);
        }
    }

    // A re-run from Settings that removes a folder must also clear it from library.json: SeedFolders lifts
    // that file's list when settings has none, which would bring the folder back on the next launch.
    [Test]
    public async Task RerunRemovingTheOnlyFolder_DropsIt_AndARestartDoesNotBringItBack()
    {
        string cfgDir = NewTempDir();
        string replays = NewTempDir();
        string libJson = Path.Combine(cfgDir, "library.json");
        try
        {
            SettingsService settings = new(cfgDir);
            using (DemoLibraryService library = new(_inline, libJson, settings))
            {
                await library.AddFoldersAsync([replays]);

                FirstRunWizardViewModel rerun = Wizard(settings, null);
                rerun.RemoveFolderCommand.Execute(replays);
                rerun.FinishCommand.Execute(null);

                await Assert.That(library.Folders.Count).IsEqualTo(0);
            }

            SettingsService restarted = new(cfgDir);
            using DemoLibraryService relaunched = new(_inline, libJson, restarted);
            await Assert.That(relaunched.Folders.Count).IsEqualTo(0)
                .Because("library.json's copy was brought in step, so the one-time lift has nothing to lift");
        }
        finally
        {
            Cleanup(cfgDir, replays);
        }
    }
}
