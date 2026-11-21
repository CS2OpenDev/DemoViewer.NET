#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.EntityTracking;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Auto-select after a rebuild of the Entity tab. A delta rebuild picks the first changed entity; a
///     rebuild without a snapshot has no "changed" to speak of, so it must not re-select the entity the
///     previous delta rebuild picked on the same tracker.
/// </summary>
[Category("RealDemo")]
public class EntityTrackingAutoSelectTests
{
    [Test]
    public async Task NonDeltaRebuild_DoesNotReselect_ThePreviousFirstChangedEntity()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        EntityTracker tracker = new();
        int warm = Math.Min(frames.Count - 200, 20_000);
        for (int f = 0; f < warm; f++)
        {
            tracker.AdvanceOneFrame(frames[f]);
        }

        Dictionary<int, Dictionary<string, object?>> before = tracker.SnapshotCurrentFields();
        for (int f = warm; f < warm + 128; f++)
        {
            tracker.AdvanceOneFrame(frames[f]);
        }

        EntityTrackingTabViewModel vm = new();
        vm.RebuildFromSteppedTracker(tracker, before);
        await Assert.That(vm.SelectedEntityListItem).IsNotNull()
            .Because("two seconds of play change at least one entity, and the delta rebuild selects it");

        vm.RebuildFromSteppedTracker(tracker, null);
        await Assert.That(vm.SelectedEntityListItem).IsNull()
            .Because("a rebuild with no snapshot has no changed entity to select");
    }

    [Test]
    public async Task RebuildOnTheSameTracker_RefreshesTheSelectedEntitysFieldTable()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        EntityTracker tracker = new();
        int warm = Math.Min(frames.Count - 2_000, 20_000);
        for (int f = 0; f < warm; f++)
        {
            tracker.AdvanceOneFrame(frames[f]);
        }

        Dictionary<int, Dictionary<string, object?>> before = tracker.SnapshotCurrentFields();
        int next = warm;
        for (; next < warm + 128; next++)
        {
            tracker.AdvanceOneFrame(frames[next]);
        }

        // As the shell wires it: the tab reads the controller's tracker, which a step advances in place.
        EntityTrackingTabViewModel vm = new() { TrackerSource = () => tracker };
        vm.RebuildFromSteppedTracker(tracker, before);
        await Assert.That(vm.SelectedEntityItem).IsTypeOf<EntityState>();
        EntityState selected = (EntityState)vm.SelectedEntityItem!;

        // Play on until the table no longer matches the entity, which the tracker mutates in place.
        while (next < frames.Count - 1 && ShowsCurrentValues(vm.EntityFieldRows, selected))
        {
            tracker.AdvanceOneFrame(frames[next++]);
        }

        // The pause snap: a rebuild with no snapshot, so nothing is auto-selected.
        vm.RebuildFromSteppedTracker(tracker, null);

        bool stillTracked = tracker.CurrentEntities.AllIndexed().Any(t => ReferenceEquals(t.Item2, selected));
        if (stillTracked)
        {
            await Assert.That(vm.SelectedEntityItem).IsSameReferenceAs(selected);
            await Assert.That(ShowsCurrentValues(vm.EntityFieldRows, selected)).IsTrue()
                .Because("the field table must show the entity at the tracker's tick, not where it was selected");
        }
        else
        {
            await Assert.That(vm.SelectedEntityItem).IsNull()
                .Because("an entity the tracker no longer holds must not stay on show");
        }
    }

    private static bool ShowsCurrentValues(IReadOnlyList<KvpRow> rows, EntityState entity)
    {
        if (rows.Count != entity.Fields.Count)
        {
            return false;
        }

        foreach (KvpRow row in rows)
        {
            if (!entity.Fields.TryGetValue(row.Key, out object? value)
                || MainViewModel.FormatValue(value) != row.Value)
            {
                return false;
            }
        }

        return true;
    }
}
