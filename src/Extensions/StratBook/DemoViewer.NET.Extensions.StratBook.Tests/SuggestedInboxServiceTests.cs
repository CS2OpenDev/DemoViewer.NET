#region

using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Suggested section's library read: a demo whose suggestions change while the first read is still
///     going is read again once it lands, not left at what the read saw.
/// </summary>
public class SuggestedInboxServiceTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    [Test]
    public async Task AVerdictDuringTheFirstRead_ShowsOnceTheReadLands()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        const string OtherPath = "/d/other.dem";
        h.Cache.Upsert(RoundIndexTestData.ParsedRecord(OtherPath, SuggestedTagsTestData.Map, new string('b', 64),
            Rows()));
        h.Service.Evaluate(OtherPath, Parse());

        // The second demo the read looks up is the moment to change the first one, which it has already read.
        List<string> found = [];
        string? changed = null;
        string? proposal = null;
        HookedLibrary library = new(h.Cache.Library(), path =>
        {
            found.Add(path);
            if (found.Count == 2)
            {
                changed = found[0];
                proposal = h.Service.Load(changed).Pending[0].Proposal.Id;
                h.Service.Reject(changed, proposal);
            }
        });
        using SuggestedInboxService inbox = new(h.Service, library, run: _inline);

        await inbox.LoadAsync();

        SuggestedInboxItem item = inbox.Items.Single(i =>
            i.DemoPath == changed && i.Entry.Proposal.Id == proposal);
        await Assert.That(item.Entry.Verdict).IsNotNull()
            .Because("the dismiss landed after the read had that demo, and its Changed must not be dropped");
    }

    [Test]
    public async Task AListPostedAfterANewerOne_IsNotShownOverIt()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        const string OtherPath = "/d/other.dem";
        h.Cache.Upsert(RoundIndexTestData.ParsedRecord(OtherPath, SuggestedTagsTestData.Map, new string('b', 64),
            Rows()));
        h.Service.Evaluate(OtherPath, Parse());

        // A worker that took its list and lost the processor before posting it: its post is held until released.
        bool hold = false;
        List<Action> held = [];
        using SuggestedInboxService inbox = new(h.Service, h.Cache.Library(), run: _inline, post: a =>
        {
            if (hold)
            {
                hold = false;
                held.Add(a);
                return;
            }

            a();
        });
        await inbox.LoadAsync();

        // Another surface changes the first demo: that worker's list has the other demo still pending.
        hold = true;
        h.Service.Reject(DemoPath, h.Service.Load(DemoPath).Pending[0].Proposal.Id);
        await Assert.That(held.Count).IsEqualTo(1);

        // Meanwhile the user dismisses a proposal of the other demo here, and that list is shown.
        SuggestedInboxItem item = inbox.Items.First(i => i.DemoPath == OtherPath && i.Entry.Verdict is null);
        await Assert.That(await inbox.DismissAsync(item)).IsTrue();

        held[0]();

        await Assert.That(inbox.Items.Single(i => i.DemoPath == OtherPath
                && i.Entry.Proposal.Id == item.Entry.Proposal.Id).Entry.Verdict).IsNotNull()
            .Because("the held list was taken before the dismiss, and must not replace the one that shows it");
    }

    private sealed class HookedLibrary(IExtensionLibrary inner, Action<string> onFind) : IExtensionLibrary
    {
        public IReadOnlyList<LibraryDemo> Demos => inner.Demos;

        public IAnalysisFacts Facts => inner.Facts;

        public LibraryDemo? Find(string path)
        {
            onFind(path);
            return inner.Find(path);
        }

        public LibraryDemo? FindBySha256(string sha256) => inner.FindBySha256(sha256);

        public LibraryPage Query(LibraryQuery query) => inner.Query(query);

        public Task<LibraryDemoDetail?> GetDetailAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetDetailAsync(path, cancellationToken);

        public event Action<LibraryChange>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }
    }
}
