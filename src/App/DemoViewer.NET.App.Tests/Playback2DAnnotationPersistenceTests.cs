#region

using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The session controller: load on attach, debounced autosave, flush on deactivate, and the claim
///     that a gated-off feature touches no disk at all.
/// </summary>
[NotInParallel]
public class Playback2DAnnotationPersistenceTests
{
    [Test]
    public async Task Controller_LoadsOnDemoAttach()
    {
        using TempDemo demo = new();
        AnnotationStore store = new(demo.AppData);
        AnnotationElement seeded = Stroke();
        await store.SaveAsync(demo.DemoPath, AnnotationStore.IdentityFor(demo.DemoPath), demo.Clock, [seeded]);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null);
        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);

        await Assert.That(controller.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(controller.Document.Elements[0]).IsEqualTo(seeded);
        await Assert.That(controller.Document.UndoDepth).IsEqualTo(0)
            .Because("loading is not an action the user can undo into the previous demo's ink");
    }

    /// <summary>
    ///     Opening a demo must not litter a sidecar beside it. Loading raises the document's Changed,
    ///     which resets the element list, and without suppressing autosave there, every demo the user ever
    ///     opened would grow an empty <c>.dvann.json</c> next to it. Caught by a stray file appearing in
    ///     the repo's own <c>assets/tour/</c> after a test run.
    /// </summary>
    [Test]
    public async Task Controller_AttachingToADemoWithNoAnnotations_WritesNothing()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Task.Delay(200);
        await controller.FlushAsync();

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse();
        await Assert.That(controller.SaveCount).IsEqualTo(0);
    }

    /// <summary>
    ///     ...but erasing the LAST stroke must still rewrite an existing sidecar. "Nothing to save" is
    ///     only true when there is also nothing on disk to correct.
    /// </summary>
    [Test]
    public async Task Controller_ErasingTheLastStroke_StillClearsAnExistingSidecar()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        AnnotationElement stroke = Stroke();
        controller.Document.Apply(new DocDelta.Add(stroke, 0));
        await controller.FlushAsync();
        await Assert.That(File.Exists(demo.SidecarPath)).IsTrue();

        controller.Document.Apply(new DocDelta.Remove(stroke.Id));
        await controller.FlushAsync();

        AnnotationStore reader = new(demo.AppData);
        AnnotationLoadResult loaded = await reader.LoadAsync(demo.DemoPath, demo.Clock);
        await Assert.That(loaded.Elements).IsEmpty();
    }

    [Test]
    public async Task Controller_AutosavesAfterDebounce()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(30)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));

        await WaitFor(() => controller.SaveCount > 0, TimeSpan.FromSeconds(5));

        await Assert.That(File.Exists(demo.SidecarPath)).IsTrue();

        AnnotationStore reader = new(demo.AppData);
        AnnotationLoadResult loaded = await reader.LoadAsync(demo.DemoPath, demo.Clock);
        await Assert.That(loaded.Elements.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     The debounce is the point: a stroke and a drag-erase both commit one document change, but a
    ///     rapid burst must still cost ONE write rather than one per delta.
    /// </summary>
    [Test]
    public async Task Controller_CoalescesABurstIntoOneSave()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(120)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        for (int i = 0; i < 25; i++)
        {
            controller.Document.Apply(new DocDelta.Add(Stroke(), i));
        }

        await WaitFor(() => controller.SaveCount > 0, TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        await Assert.That(controller.SaveCount).IsEqualTo(1);
    }

    [Test]
    public async Task Controller_FlushesOnDeactivate()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            // Long enough that the debounce would NOT have fired on its own.
            AutoSaveDelay = TimeSpan.FromSeconds(30)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse();

        // The BLOCKING overload, which is what OnDeactivated calls: the shell deactivates the tab on
        // its way out of MainViewModel.Dispose, where a fire-and-forget write races the process exit.
        controller.Flush();

        await Assert.That(File.Exists(demo.SidecarPath)).IsTrue()
            .Because("a debounced autosave that had not fired yet is the difference between a stroke " +
                "surviving a tab switch and vanishing");
    }

    /// <summary>
    ///     A flush writes what was drawn, and with nothing drawn since the load it writes nothing. The document
    ///     is then only a copy of the file as it was read: a transfer import that wrote the demo's annotations
    ///     meanwhile would be replaced by that copy on the next tab switch, or erased when the copy is empty.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Controller_AFlushWithNothingDrawn_LeavesWhatWasWrittenSinceTheLoad(bool hadAnnotations)
    {
        using TempDemo demo = new();
        AnnotationElement mine = Stroke();
        AnnotationElement imported = Stroke();
        AnnotationStore other = new(demo.AppData);
        if (hadAnnotations)
        {
            await other.SaveAsync(demo.DemoPath, AnnotationStore.IdentityFor(demo.DemoPath), demo.Clock, [mine]);
        }

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null);
        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);

        AnnotationElement[] merged = hadAnnotations ? [mine, imported] : [imported];
        await other.SaveAsync(demo.DemoPath, AnnotationStore.IdentityFor(demo.DemoPath), demo.Clock, merged);
        await controller.FlushAsync();

        AnnotationLoadResult loaded = await new AnnotationStore(demo.AppData).LoadAsync(demo.DemoPath, demo.Clock);
        using (Assert.Multiple())
        {
            await Assert.That(loaded.Elements.Select(e => e.Id)).IsEquivalentTo(merged.Select(e => e.Id));
            await Assert.That(controller.SaveCount).IsEqualTo(0);
        }

        // What is drawn afterwards is still saved.
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
        await controller.FlushAsync();
        await Assert.That(controller.SaveCount).IsEqualTo(1);
    }

    [Test]
    public async Task Controller_GateOff_NeverTouchesDisk()
    {
        using TempDemo demo = new();
        FakeModuleFeatureGate gate = new();
        gate.SetEnabled(AnnotationSessionController.FeatureId, false);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };
        controller.SetFeatures(gate);

        await Assert.That(controller.IsEnabled).IsFalse();

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
        await Task.Delay(200);
        await controller.FlushAsync();

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse();
        await Assert.That(controller.SaveCount).IsEqualTo(0);
    }

    [Test]
    public async Task Controller_ReattachToTheSameDemo_KeepsTheInMemoryDocument()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), null)
        {
            AutoSaveDelay = TimeSpan.FromSeconds(30)
        };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock, false);

        await Assert.That(controller.Document.Elements.Count).IsEqualTo(1)
            .Because("a tab RE-activation must not throw away what has not been autosaved yet");
    }

    [Test]
    public async Task Controller_SeedsStyleFromSettings()
    {
        using TempDemo demo = new();
        SettingsService settings = new(demo.SettingsDir);
        settings.Write(s =>
        {
            s.Playback2D.AnnotationColorArgb = 0xFF00FF00;
            s.Playback2D.AnnotationWidth = 21;
            s.Playback2D.AnnotationDefaultVisibility = "Fade";
            s.Playback2D.AnnotationAnchorToEntities = true;
        });

        using AnnotationSessionController controller = new(null, settings);

        await Assert.That(controller.Session.Style.ColorArgb).IsEqualTo(0xFF00FF00u);
        await Assert.That(controller.Session.Style.WidthWorld).IsEqualTo(21f);
        await Assert.That(controller.Session.DefaultVisibility).IsEqualTo(EnvelopeMode.Fade);
        await Assert.That(controller.Session.AnchorToEntities).IsTrue();
    }

    /// <summary>
    ///     On the browser head the status line must NOT name a sidecar path. System.IO writes there land
    ///     in the WASM runtime's in-memory virtual FS, so the store finds a "writable" location, reports
    ///     it, and the user reads a filename as a promise the next reload breaks. Annotations
    ///     work in session, a reload loses them, and the UI says so.
    ///     <para>
    ///         Found by a WASM verification pass on the published head: with a demo attached, the
    ///         panel read "saving to /sample-de_nuke.dem.dvann.json" in a browser tab.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Controller_OnBrowser_SaysTheTabForgets_RatherThanNamingAPath()
    {
        using TempDemo demo = new();

        using AnnotationSessionController desktop =
            new(new AnnotationStore(demo.AppData), null, static () => false);
        await desktop.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(desktop.StatusText).StartsWith("saving to ")
            .Because("a desktop user gets a real path they can find the file at");

        using AnnotationSessionController browser =
            new(new AnnotationStore(demo.AppData), null, static () => true);
        await browser.AttachDemoAsync(demo.DemoPath, demo.Clock);

        await Assert.That(browser.StatusText).DoesNotContain(".dvann.json");
        await Assert.That(browser.StatusText).Contains("session only");
        await Assert.That(browser.StatusText).Contains("reload");
    }

    /// <summary>
    ///     The browser head has one thread, so the blocking flush a tab deactivation calls cannot wait on
    ///     a save that resumes on the pool: the wait throws or never ends. Holding the save serializer
    ///     stands in for that missing second thread; the flush has to return anyway, and the save still
    ///     lands once the serializer frees up.
    /// </summary>
    [Test]
    public async Task Controller_OnBrowser_BlockingFlushDoesNotWaitOnTheSave()
    {
        using TempDemo demo = new();
        using AnnotationSessionController controller =
            new(new AnnotationStore(demo.AppData), null, static () => true)
            {
                AutoSaveDelay = TimeSpan.FromSeconds(30)
            };

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));

        SemaphoreSlim serializer = (SemaphoreSlim)typeof(AnnotationSessionController)
            .GetField("_saveSerializer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(controller)!;
        await serializer.WaitAsync();
        bool returned;
        try
        {
            Task flush = Task.Run(controller.Flush);
            returned = await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(5))) == flush;
        }
        finally
        {
            serializer.Release();
        }

        await Assert.That(returned).IsTrue()
            .Because("a blocking wait on the single-threaded WASM runtime throws or hangs the tab switch");

        for (int i = 0; i < 100 && controller.SaveCount == 0; i++)
        {
            await Task.Delay(20);
        }

        await Assert.That(controller.SaveCount).IsEqualTo(1)
            .Because("the flush still writes, it just does not wait for the write");
    }

    /// <summary>
    ///     <b><c>AnnotationAutoSave</c> had a reader and no writer.</b> The key was honoured at runtime,
    ///     carried a <c>WriteInMemory</c> row, and nothing in the app could set it: a user who wanted
    ///     session-only ink had to hand-edit <c>settings.json</c>, and every reader only ever saw the
    ///     default. It now has the toolbar toggle and the writer.
    ///     <para>
    ///         The check also MOVED, from the schedule to <see cref="AnnotationSessionController" />'s save
    ///         itself: <c>FlushAsync</c> is called on a demo swap, on tab deactivation and at shutdown, and
    ///         it went straight past the schedule-time guard. "Session only" that still writes the sidecar
    ///         when you close the tab is not session only. It is the same file arriving at a moment the
    ///         user is even less likely to notice.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Controller_AutoSaveOff_WritesNothing_NotEvenOnFlush()
    {
        using TempDemo demo = new();
        SettingsService settings = new(demo.SettingsDir);
        settings.Write(s => s.Playback2D.AnnotationAutoSave = false);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), settings)
        {
            AutoSaveDelay = TimeSpan.FromMilliseconds(20)
        };

        await Assert.That(controller.AutoSave).IsFalse()
            .Because("ApplySettings seeds it, so the persisted key reaches the live controller");

        await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
        controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
        await Task.Delay(200);

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse()
            .Because("the debounce must not arm at all");

        await controller.FlushAsync();

        Console.WriteLine($"[autosave-off] saves={controller.SaveCount} "
            + $"sidecar={File.Exists(demo.SidecarPath)} status='{controller.StatusText}'");

        await Assert.That(File.Exists(demo.SidecarPath)).IsFalse()
            .Because("the flush path — demo swap, deactivate, shutdown — has to honour it too, or the "
                + "setting only delays the write it was asked to prevent");
        await Assert.That(controller.SaveCount).IsEqualTo(0);

        // And the status line stops promising a destination nothing is going to.
        await Assert.That(controller.StatusText).Contains("auto-save off");
    }

    /// <summary>
    ///     The other direction: the toggle WRITES the key. Previously nothing did, so the branch above
    ///     could only ever be reached by hand-editing the file.
    /// </summary>
    [Test]
    public async Task Controller_TogglingAutoSave_PersistsTheKey()
    {
        using TempDemo demo = new();
        SettingsService settings = new(demo.SettingsDir);

        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData), settings)
        {
            StylePersistDelay = TimeSpan.Zero // write inline; the debounce is PersistSettings' own test
        };

        await Assert.That(settings.Current.Playback2D.AnnotationAutoSave).IsTrue();

        controller.AutoSave = false;
        controller.PersistSettings();

        Console.WriteLine("[autosave-write] persisted="
            + settings.Current.Playback2D.AnnotationAutoSave);

        await Assert.That(settings.Current.Playback2D.AnnotationAutoSave).IsFalse()
            .Because("the key shipped with a reader, a WriteInMemory row, and no writer anywhere");

        // And it comes back on the next controller, which is what "persisted" has to mean.
        using AnnotationSessionController reopened = new(new AnnotationStore(demo.AppData), settings);
        await Assert.That(reopened.AutoSave).IsFalse();
    }

    /// <summary>
    ///     <c>CanAutoSave</c> drives the toggle's enabled state, and it has to be false wherever no
    ///     sidecar is possible: no demo, no store, or the browser head, whose "writable" path is a
    ///     virtual FS that dies with the tab. A checkbox offering to control saving where nothing can be
    ///     saved is this audit's own defect class one layer down.
    /// </summary>
    [Test]
    public async Task Controller_CanAutoSave_IsFalseWhereNoSidecarIsPossible()
    {
        using TempDemo demo = new();

        using AnnotationSessionController detached = new(new AnnotationStore(demo.AppData), null);
        await Assert.That(detached.CanAutoSave).IsFalse().Because("no demo is attached");

        using AnnotationSessionController storeless = new(null, null);
        await storeless.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(storeless.CanAutoSave).IsFalse().Because("there is no store to write through");

        using AnnotationSessionController browser =
            new(new AnnotationStore(demo.AppData), null, static () => true);
        await browser.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(browser.CanAutoSave).IsFalse()
            .Because("the browser head's writable path is an in-memory FS that dies with the tab");

        using AnnotationSessionController desktop =
            new(new AnnotationStore(demo.AppData), null, static () => false);
        await desktop.AttachDemoAsync(demo.DemoPath, demo.Clock);
        await Assert.That(desktop.CanAutoSave).IsTrue();
    }

    /// <summary>
    ///     The document is UI-thread state: the timeline rebuilds bound collections off its Changed. An
    ///     attach started on the UI thread has to come back to it, even when the flush it waits on was
    ///     finished by a pool thread, which is what an autosave still writing the previous ink does.
    /// </summary>
    [Test]
    public async Task Controller_AttachStartedOnTheUiThread_TouchesTheDocumentOnlyThere()
    {
        using TempDemo demo = new();
        using KeyGate gate = new();

        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData, gate.Resolve), null)
            {
                AutoSaveDelay = TimeSpan.FromMilliseconds(10)
            };
            await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);

            // An autosave that is inside the store, holding the save serializer, on its own thread.
            gate.Arm();
            controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
            await WaitFor(() => gate.Entered, TimeSpan.FromSeconds(10));

            List<bool> onUiThread = [];
            controller.Document.Changed += () => onUiThread.Add(Dispatcher.UIThread.CheckAccess());

            Task attach = controller.AttachDemoAsync(demo.DemoPath, demo.Clock);
            await Assert.That(attach.IsCompleted).IsFalse().Because("its flush is queued behind the autosave");
            gate.Release();
            await attach;

            await Assert.That(onUiThread).IsNotEmpty();
            await Assert.That(onUiThread).DoesNotContain(false);
            await Assert.That(controller.Document.Elements.Count).IsEqualTo(1);
        });
    }

    /// <summary>
    ///     The debounced save comes back to the UI thread to copy the element list, which is only safe to
    ///     read where it is written. The write that follows must not stay there: it stats, may hash, and
    ///     serializes the whole document.
    /// </summary>
    [Test]
    public async Task Controller_DebouncedSave_DoesNotWriteOnTheUiThread()
    {
        using TempDemo demo = new();
        using KeyGate gate = new();

        await HeadlessSession.RunOnUi(async () =>
        {
            using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData, gate.Resolve), null)
            {
                AutoSaveDelay = TimeSpan.FromMilliseconds(10)
            };
            await controller.AttachDemoAsync(demo.DemoPath, demo.Clock);

            gate.Arm();
            controller.Document.Apply(new DocDelta.Add(Stroke(), 0));
            await WaitFor(() => gate.Entered, TimeSpan.FromSeconds(10));

            // Still on the UI thread, and the save is parked inside the store: it is not being written here.
            await Assert.That(gate.EnteredOnUiThread).IsFalse();
            gate.Release();
            await WaitFor(() => controller.SaveCount > 0, TimeSpan.FromSeconds(10));
            await Assert.That(File.Exists(demo.SidecarPath)).IsTrue();
        });
    }

    /// <summary>
    ///     Between an attach emptying the document and its sidecar arriving, the document is a
    ///     placeholder. A flush in that window (a tab switch, shutdown) must not write it: an empty
    ///     document over an existing sidecar is how the user clearing their ink is spelled.
    /// </summary>
    [Test]
    public async Task Controller_AFlushWhileTheSidecarIsLoading_DoesNotWriteTheEmptyPlaceholder()
    {
        using TempDemo demo = new();
        const string Sha = "5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e";
        AnnotationElement seeded = Stroke();
        await new AnnotationStore(demo.AppData).SaveAsync(demo.DemoPath,
            new DemoIdentity(Sha, "match.dem", 8), demo.Clock, [seeded]);

        using KeyGate gate = new() { Keys = _ => Sha };
        using AnnotationSessionController controller = new(new AnnotationStore(demo.AppData, gate.Resolve), null);

        // The load has read the file and not yet returned.
        gate.Arm();
        Task attach = Task.Run(() => controller.AttachDemoAsync(demo.DemoPath, demo.Clock));
        await Assert.That(gate.WaitEntered(TimeSpan.FromSeconds(10))).IsTrue();

        controller.Flush();

        gate.Release();
        await attach;

        AnnotationLoadResult onDisk =
            await new AnnotationStore(demo.AppData, _ => Sha).LoadAsync(demo.DemoPath, demo.Clock);
        using (Assert.Multiple())
        {
            await Assert.That(onDisk.Elements.Count).IsEqualTo(1);
            await Assert.That(controller.Document.Elements.Count).IsEqualTo(1);
            await Assert.That(controller.Document.Elements[0]).IsEqualTo(seeded);
        }
    }

    /// <summary>
    ///     An attach that another attach has overtaken stands down: its sidecar belongs to a demo the
    ///     controller is no longer on, and applying it would put that demo's ink under this one's name for
    ///     the next save to write there.
    /// </summary>
    [Test]
    public async Task Controller_ALoadThatFinishesAfterTheNextDemoAttached_IsNotApplied()
    {
        using TempDemo first = new();
        using TempDemo second = new();
        const string FirstSha = "aa1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e";
        const string SecondSha = "bb1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e";
        string Sha(string path) =>
            string.Equals(path, first.DemoPath, StringComparison.OrdinalIgnoreCase) ? FirstSha : SecondSha;

        AnnotationElement firstInk = Stroke();
        AnnotationElement secondInk = Stroke();
        AnnotationStore seed = new(first.AppData);
        await seed.SaveAsync(first.DemoPath, new DemoIdentity(FirstSha, "match.dem", 8), first.Clock, [firstInk]);
        await seed.SaveAsync(second.DemoPath, new DemoIdentity(SecondSha, "match.dem", 8), second.Clock,
            [secondInk]);

        using KeyGate gate = new() { Keys = Sha, Only = first.DemoPath };
        using AnnotationSessionController controller = new(new AnnotationStore(first.AppData, gate.Resolve), null);

        gate.Arm();
        Task slow = Task.Run(() => controller.AttachDemoAsync(first.DemoPath, first.Clock));
        await Assert.That(gate.WaitEntered(TimeSpan.FromSeconds(10))).IsTrue();

        // The user opens the next demo before the first one's sidecar has come back.
        await controller.AttachDemoAsync(second.DemoPath, second.Clock);

        gate.Release();
        await slow;

        await Assert.That(controller.Document.Elements.Count).IsEqualTo(1);
        await Assert.That(controller.Document.Elements[0]).IsEqualTo(secondInk);

        await controller.FlushAsync();
        AnnotationStore reader = new(first.AppData, Sha);
        AnnotationLoadResult firstOnDisk = await reader.LoadAsync(first.DemoPath, first.Clock);
        AnnotationLoadResult secondOnDisk = await reader.LoadAsync(second.DemoPath, second.Clock);
        using (Assert.Multiple())
        {
            await Assert.That(firstOnDisk.Elements.Count).IsEqualTo(1);
            await Assert.That(firstOnDisk.Elements[0]).IsEqualTo(firstInk);
            await Assert.That(secondOnDisk.Elements.Count).IsEqualTo(1);
            await Assert.That(secondOnDisk.Elements[0]).IsEqualTo(secondInk);
        }
    }

    /// <summary>
    ///     A load that throws leaves the document a placeholder no save will write, so nothing drawn from then
    ///     on reaches a file. The status line has to say so rather than keep naming the last demo's sidecar.
    /// </summary>
    [Test]
    public async Task Controller_ALoadThatThrows_SaysTheInkIsSessionOnly()
    {
        using TempDemo good = new();
        using TempDemo bad = new();
        const string Sha = "cc1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e5c1f0b7e";
        AnnotationStore seed = new(good.AppData);
        await seed.SaveAsync(good.DemoPath, new DemoIdentity(Sha, "match.dem", 8), good.Clock, [Stroke()]);
        await seed.SaveAsync(bad.DemoPath, new DemoIdentity(Sha, "match.dem", 8), bad.Clock, [Stroke()]);

        string Key(string path) =>
            string.Equals(path, bad.DemoPath, StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("no key for this demo")
                : Sha;

        using AnnotationSessionController controller = new(new AnnotationStore(good.AppData, Key), null);
        await controller.AttachDemoAsync(good.DemoPath, good.Clock);
        await Assert.That(controller.Document.Elements.Count).IsEqualTo(1);

        bool threw = false;
        try
        {
            await controller.AttachDemoAsync(bad.DemoPath, bad.Clock);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        using (Assert.Multiple())
        {
            await Assert.That(threw).IsTrue();
            await Assert.That(controller.StatusText).IsEqualTo("annotations could not be loaded — session only");
        }
    }

    // Parks the store's first key lookup after Arm() until Release(). The store asks for a demo's key from
    // inside a load (once the file is read) and from inside a save (once it holds the save serializer), so
    // this is the one place a test can hold either open without a seam in the controller.
    private sealed class KeyGate : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _state;

        public Func<string, string> Keys { get; init; } = static _ => "";

        /// <summary>The only demo path whose lookup is parked, or null for any.</summary>
        public string? Only { get; init; }

        public bool Entered => _entered.IsSet;

        public bool EnteredOnUiThread { get; private set; }

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }

        public void Arm() => Volatile.Write(ref _state, 1);

        public bool WaitEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void Release() => _release.Set();

        public string Resolve(string path)
        {
            if ((Only is null || string.Equals(path, Only, StringComparison.OrdinalIgnoreCase))
                && Interlocked.CompareExchange(ref _state, 2, 1) == 1)
            {
                EnteredOnUiThread = Dispatcher.UIThread.CheckAccess();
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            }

            return Keys(path);
        }
    }

    private static AnnotationElement Stroke() =>
        new(Guid.NewGuid(), AnnotationKind.Freehand, AnnotationStyle.Default, new SpaceRef.World(0),
            TimeEnvelope.Static,
            [new InkPoint(0, 0, 0.5f), new InkPoint(40, 10, 0.5f), new InkPoint(80, 0, 0.5f)], null);

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout,
        [CallerArgumentExpression(nameof(condition))]
        string? expression = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException($"timed out waiting for `{expression}`");
    }

    private sealed class TempDemo : IDisposable
    {
        private readonly string _root;

        public TempDemo()
        {
            _root = Path.Combine(Path.GetTempPath(), "dv-p2d-ann-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "demos"));
            AppData = Path.Combine(_root, "appdata");
            SettingsDir = Path.Combine(_root, "settings");
            Directory.CreateDirectory(AppData);
            Directory.CreateDirectory(SettingsDir);

            DemoPath = Path.Combine(_root, "demos", "match.dem");
            File.WriteAllBytes(DemoPath, "PBDEMS2\0"u8);
            SidecarPath = DemoPath + AnnotationStore.SidecarExtension;
            Clock = new ClockIdentity(ClockIdentity.DvFrameClock, 64, 1000, 0, 0);
        }

        public string AppData { get; }

        public string SettingsDir { get; }

        public string DemoPath { get; }

        public string SidecarPath { get; }

        public ClockIdentity Clock { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // A temp tree that outlives the test is noise, not a failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
