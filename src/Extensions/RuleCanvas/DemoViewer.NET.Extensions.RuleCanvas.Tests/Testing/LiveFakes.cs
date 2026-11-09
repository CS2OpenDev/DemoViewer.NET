#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>The shell's clock as the host hands it: a tick, the playhead subscribers, and the seeks asked for.</summary>
internal sealed class FakeShell
{
    public int Tick { get; set; }

    public List<Action> Subscribers { get; } = [];

    public List<int> Seeks { get; } = [];

    public IDisposable Watch(Action handler)
    {
        Subscribers.Add(handler);
        return new Unwatch(this, handler);
    }

    /// <summary>Moves the clock and raises the playhead, as the shell does once per rendered frame.</summary>
    public void MoveTo(int tick)
    {
        Tick = tick;
        foreach (Action handler in Subscribers.ToArray())
        {
            handler();
        }
    }

    private sealed class Unwatch(FakeShell shell, Action handler) : IDisposable
    {
        public void Dispose() => shell.Subscribers.Remove(handler);
    }
}

/// <summary>A module context that records the event navigation asked of it.</summary>
internal sealed class FakeModuleContext : IModuleContext
{
    public List<string[]> NextEvents { get; } = [];

    public List<string[]> PrevEvents { get; } = [];

    public List<int> SeekTicks { get; } = [];

    public bool HasDemo => true;

    public string? DemoPath => LiveCanned.DemoPath;

    public int TickRate => 64;

    public int CurrentFrameIndex => 0;

    public int CurrentTick => 0;

    public bool IsPlaying => false;

    public double Speed => 1;

    public IReadOnlyEntityView Entities => throw new NotSupportedException();

    public IReadOnlyList<PlayerRosterEntry> Players => [];

    public IReadOnlyList<IPlayerState> CurrentPlayers => [];

    public event Action<IPlaybackSnapshot>? Advanced
    {
        add { }
        remove { }
    }

    public double CurtimeSeconds(int tick) => tick / 64.0;

    public void RequestSeekToFrame(int frameIndex)
    {
    }

    public void RequestSeekToTick(int tick) => SeekTicks.Add(tick);

    public void RequestPlay()
    {
    }

    public void RequestPause()
    {
    }

    public void RequestNextEvent(IReadOnlyCollection<string>? eventNames) => NextEvents.Add([.. eventNames ?? []]);

    public void RequestPrevEvent(IReadOnlyCollection<string>? eventNames) => PrevEvents.Add([.. eventNames ?? []]);
}

/// <summary>
///     A canned stage 2 run over player_round, and a tab opened on a host with a fake clock: Alice's kills
///     at 100, 500 and 900 with their trigger wire tracked; her deaths gated by MatchLive, open on 2 of 40
///     death events; KAST on at 120 and off at 800.
/// </summary>
internal static class LiveCanned
{
    public const string DemoPath = "/demos/one.dem";

    public static readonly RulesetDocumentNodeKey Kills = new("player_round", "round_kills");
    public static readonly RulesetDocumentNodeKey Deaths = new("player_round", "round_deaths");
    public static readonly RulesetDocumentNodeKey Kast = new("player_round", "round_kast");

    /// <summary>A series counting 1, 2, 3 at the given ticks.</summary>
    public static ValueSeries Counting(params int[] ticks) =>
        ValueSeries.From([.. ticks.Select((t, i) => new ValuePoint(t, true, i + 1, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))]);

    public static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    public static CanvasConnectionViewModel TriggerInto(CanvasTabViewModel vm, string id) =>
        vm.Connections.First(w => w.Target.Owner?.Id == id && w.LiveKind == LiveWireKind.Trigger);

    public static CanvasConnectionViewModel GateInto(CanvasTabViewModel vm, string id) =>
        vm.Connections.First(w => w.Target.Owner?.Id == id && w.LiveKind == LiveWireKind.Gate);

    public static LiveWireKey KeyOf(CanvasConnectionViewModel wire) => new(wire.Source.Owner!.Key, wire.Target.Owner!.Key, wire.LiveKind);

    /// <summary>The run, keyed to the wires <paramref name="vm" /> drew. It starts at tick 1, so a clock still at 0 reads as outside it.</summary>
    public static LiveValuesRun Run(CanvasTabViewModel vm)
    {
        List<LiveInstance> instances = [new LiveInstance(0, null, null), new LiveInstance(1, 0, "Alice"), new LiveInstance(2, 1, "Bob")];
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes = new()
        {
            [(Kills, 1)] = new NodeTrack(3, 100, 900, "3", 3f, true) { Series = Counting(100, 500, 900) },
            [(Kills, 2)] = new NodeTrack(1, 500, 500, "1", 1f, true) { Series = Counting(500) },
            [(Deaths, 1)] = new NodeTrack(2, 300, 700, "2", 2f, true) { Series = Counting(300, 700) },
            [(Kast, 1)] = new NodeTrack(2, 120, 800, "0", 0f, false)
            {
                Series = ValueSeries.From([new ValuePoint(120, true, 1, null), new ValuePoint(800, false, 0, null)]), OnCount = 1, OffCount = 1
            }
        };
        Dictionary<(LiveWireKey, int), WireTrack> wires = new()
        {
            [(KeyOf(TriggerInto(vm, "round_kills")), 1)] = new WireTrack(3, 100, 900, TickList.From([100, 500, 900])),
            [(KeyOf(GateInto(vm, "round_deaths")), 1)] = new WireTrack(2, 300, 700, TickList.From([300, 700])) { TriggerCount = 40 }
        };
        return new LiveValuesRun(DemoPath, "Cs2GotvProfile", 64, 1, 90000, instances, nodes, [],
            TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(7), 0) { Wires = wires };
    }

    /// <summary>A tab over <paramref name="rules" /> on a host with <paramref name="shell" />'s clock, activated with <paramref name="context" />.</summary>
    public static (CanvasTabViewModel Vm, FakeShell Shell) Open(RulesTree rules, bool demo = true, FakeShell? shell = null, IModuleContext? context = null)
    {
        shell ??= new FakeShell();
        LiveValuesHost host = new(() => null, () => demo ? DemoPath : null, () => "hash", a => a(), null, null,
            () => shell.Tick, shell.Watch, shell.Seeks.Add);
        CanvasTabViewModel vm = new(_ => true, rules.Load, () => rules.OwnFolder, null, host);
        vm.OnActivated(context!);
        return (vm, shell);
    }
}
