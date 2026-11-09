#region

using Avalonia.Input;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Step mode with the canned run, a fake shell clock and a clock the test drives for the flash: the
///     toggle is off without a run and says so, a step picks the next or previous tick over the union of
///     the drawn nodes' fires, seeks the shell there, flashes the wires that fired at that tick, and the
///     flash clears when the clock fires, on the next step, when step mode goes off, and when the run goes.
/// </summary>
public class CanvasTabViewModelStepTests
{
    /// <summary>A flash clock the test fires by hand, counting what was scheduled and what was cancelled.</summary>
    private sealed class HandClock
    {
        public List<Action> Pending { get; } = [];

        public int Scheduled { get; private set; }

        public int Cancelled { get; private set; }

        public TimeSpan? LastDelay { get; private set; }

        public IDisposable Schedule(TimeSpan after, Action action)
        {
            Scheduled++;
            LastDelay = after;
            Pending.Add(action);
            return new Entry(this, action);
        }

        /// <summary>Fires every pending action, as the delay elapsing would.</summary>
        public void Elapse()
        {
            foreach (Action action in Pending.ToArray())
            {
                Pending.Remove(action);
                action();
            }
        }

        private sealed class Entry(HandClock clock, Action action) : IDisposable
        {
            public void Dispose()
            {
                if (clock.Pending.Remove(action))
                {
                    clock.Cancelled++;
                }
            }
        }
    }

    private static (CanvasTabViewModel Vm, FakeShell Shell, HandClock Clock) Open(RulesTree rules)
    {
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        HandClock clock = new();
        vm.FlashScheduler = clock.Schedule;
        return (vm, shell, clock);
    }

    private static List<CanvasConnectionViewModel> Flashing(CanvasTabViewModel vm) => [.. vm.Connections.Where(w => w.IsFlashing)];

    private static IEnumerable<string> FlashTargets(CanvasTabViewModel vm) => Flashing(vm).Select(w => w.Target.Owner?.Id ?? "").Distinct();

    [Test]
    public async Task WithoutARun_StepIsOff_AndSaysSo()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell, HandClock clock) = Open(rules);
        using (Assert.Multiple())
        {
            await Assert.That(vm.CanStep).IsFalse();
            await Assert.That(vm.StepTip).IsEqualTo("Step is off: no run yet");
            await Assert.That(vm.StepTicks).IsEmpty();
        }

        vm.IsStepMode = true;
        vm.StepForwardCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Seeks).IsEmpty();
            await Assert.That(clock.Scheduled).IsEqualTo(0);
        }

        vm.ShowLiveRun(LiveCanned.Run(vm));
        using (Assert.Multiple())
        {
            await Assert.That(vm.CanStep).IsTrue();
            await Assert.That(vm.StepTip).StartsWith("Step the playhead");
        }
    }

    [Test]
    public async Task TheStepTicks_AreTheUnionOfTheDrawnNodesFires_ForTheSelectedInstance()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _, _) = Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        await Assert.That(vm.StepTicks).IsEquivalentTo([100, 120, 300, 500, 700, 800, 900]).Because("kills, deaths and kast for Alice, merged and sorted");

        vm.SelectedInstance = vm.Instances.Single(i => i.Name == "Bob");
        await Assert.That(vm.StepTicks).IsEquivalentTo([500]);

        vm.SelectedInstance = vm.Instances.Single(i => i.Name == "Alice");
        vm.FocusNode(LiveCanned.Node(vm, "round_kills"));
        await Assert.That(vm.StepTicks).IsEquivalentTo([100, 120, 500, 800, 900]).Because("the slice holds kills and the kast that reads it, not deaths");
    }

    [Test]
    public async Task AStep_SeeksTheNextTick_AndFlashesTheWiresThatFiredThere_UntilTheClockFires()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell, HandClock clock) = Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        vm.IsStepMode = true;

        vm.StepForwardCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Seeks).IsEquivalentTo([100]);
            await Assert.That(LiveCanned.TriggerInto(vm, "round_kills").IsFlashing).IsTrue().Because("the trigger wire's own track fires at 100");
            await Assert.That(FlashTargets(vm)).IsEquivalentTo(["round_kills"]).Because("a wire with no edge of its own flashes with its target, and only kills fired at 100");
            await Assert.That(vm.FlashingWires.Count).IsEqualTo(Flashing(vm).Count);
            await Assert.That(clock.Scheduled).IsEqualTo(1);
            await Assert.That(clock.LastDelay).IsEqualTo(CanvasTabViewModel.FlashDuration);
        }

        clock.Elapse();
        using (Assert.Multiple())
        {
            await Assert.That(Flashing(vm)).IsEmpty();
            await Assert.That(vm.FlashingWires).IsEmpty();
        }

        shell.MoveTo(100);
        vm.StepForwardCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Seeks).IsEquivalentTo([100, 120]);
            await Assert.That(FlashTargets(vm)).IsEquivalentTo(["round_kast"]).Because("kast turned on at 120; its logic wires have no edge of their own and flash with it");
            await Assert.That(Flashing(vm)).IsNotEmpty();
        }

        // A second step before the clock fires cancels the first flash and starts another.
        shell.MoveTo(120);
        vm.StepForwardCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Seeks).IsEquivalentTo([100, 120, 300]);
            await Assert.That(clock.Cancelled).IsEqualTo(1);
            await Assert.That(clock.Scheduled).IsEqualTo(3);
            await Assert.That(LiveCanned.GateInto(vm, "round_deaths").IsFlashing).IsTrue().Because("the gate's own track fires at 300");
            await Assert.That(LiveCanned.TriggerInto(vm, "round_deaths").IsFlashing).IsTrue().Because("no track of its own: it fires with its target");
            await Assert.That(FlashTargets(vm)).IsEquivalentTo(["round_deaths"]);
        }
    }

    [Test]
    public async Task StepBack_SeeksThePreviousTick_AndNothingPastTheEnds()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell, _) = Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        vm.IsStepMode = true;
        shell.MoveTo(500);
        vm.StepBackCommand.Execute(null);
        await Assert.That(shell.Seeks).IsEquivalentTo([300]).Because("a fire at the playhead is not previous");

        shell.MoveTo(100);
        vm.StepBackCommand.Execute(null);
        await Assert.That(shell.Seeks).IsEquivalentTo([300]).Because("nothing before the first fire");

        shell.MoveTo(900);
        vm.StepForwardCommand.Execute(null);
        await Assert.That(shell.Seeks).IsEquivalentTo([300]).Because("nothing after the last fire");
    }

    [Test]
    public async Task TheFlash_ClearsWhenStepModeGoesOff_AndWhenTheRunGoes()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _, HandClock clock) = Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        vm.IsStepMode = true;
        vm.StepForwardCommand.Execute(null);
        await Assert.That(Flashing(vm)).IsNotEmpty();

        vm.IsStepMode = false;
        using (Assert.Multiple())
        {
            await Assert.That(Flashing(vm)).IsEmpty();
            await Assert.That(clock.Cancelled).IsEqualTo(1);
        }

        vm.IsStepMode = true;
        vm.StepForwardCommand.Execute(null);
        await Assert.That(Flashing(vm)).IsNotEmpty();
        vm.ClearLive();
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsStepMode).IsFalse().Because("no run, no stepping");
            await Assert.That(Flashing(vm)).IsEmpty();
            await Assert.That(clock.Pending).IsEmpty();
        }
    }

    [Test]
    public async Task AStepWithStepModeOff_DoesNothing_AndTheKeysMapToTheSteps()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell, _) = Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        vm.StepForwardCommand.Execute(null);
        await Assert.That(shell.Seeks).IsEmpty();
        using (Assert.Multiple())
        {
            await Assert.That(CanvasKeyMap.Resolve(Key.OemPeriod, KeyModifiers.None)).IsEqualTo(CanvasKeyAction.StepForward);
            await Assert.That(CanvasKeyMap.Resolve(Key.OemComma, KeyModifiers.None)).IsEqualTo(CanvasKeyAction.StepBack);
            await Assert.That(CanvasKeyMap.Resolve(Key.OemPeriod, KeyModifiers.Shift)).IsEqualTo(CanvasKeyAction.None);
        }
    }
}
