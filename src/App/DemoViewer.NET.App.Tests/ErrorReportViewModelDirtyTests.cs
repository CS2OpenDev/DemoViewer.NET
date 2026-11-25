#region

using DemoViewer.NET.ViewModels.ErrorReport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The unsaved-notes guard on the error-report window. The notes live only in the view-model until Save,
///     so a close with edits pending must ask first, and a load or a save must not count as an edit.
/// </summary>
public class ErrorReportViewModelDirtyTests
{
    private static ErrorReportViewModel Vm() => new("/demos/a.dem", null, null, () => (null, null));

    [Test]
    public async Task ANewReport_IsNotDirty_AndClosesWithoutAsking()
    {
        ErrorReportViewModel vm = Vm();
        int closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        vm.CloseCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsDirty).IsFalse();
            await Assert.That(vm.IsClosePromptOpen).IsFalse();
            await Assert.That(closes).IsEqualTo(1);
        }
    }

    [Test]
    public async Task EditingTheNotes_MakesItDirty_AndCloseAsksInsteadOfClosing()
    {
        ErrorReportViewModel vm = Vm();
        int closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        vm.Notes = "half-written";
        vm.CloseCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsDirty).IsTrue();
            await Assert.That(vm.IsClosePromptOpen).IsTrue();
            await Assert.That(closes).IsEqualTo(0);
        }

        vm.KeepEditingCommand.Execute(null);
        await Assert.That(vm.IsClosePromptOpen).IsFalse();

        vm.CloseCommand.Execute(null);
        vm.DiscardAndCloseCommand.Execute(null);
        await Assert.That(closes).IsEqualTo(1);
    }

    [Test]
    public async Task TryClose_HoldsAWindowCloseWhileDirty()
    {
        ErrorReportViewModel vm = Vm();
        vm.Notes = "unsaved";

        bool first = vm.TryClose();
        vm.IsDirty = false;
        bool afterSave = vm.TryClose();

        using (Assert.Multiple())
        {
            await Assert.That(first).IsFalse();
            await Assert.That(afterSave).IsTrue();
        }
    }
}
