#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.EntityTracking;

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
}
