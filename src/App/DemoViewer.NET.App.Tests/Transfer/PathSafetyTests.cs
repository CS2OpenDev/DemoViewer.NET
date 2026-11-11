#region

using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The relative-path checks a zip entry and a transfer manifest both pass through before anything is joined
///     to a folder on disk.
/// </summary>
public class PathSafetyTests
{
    [Test]
    [Arguments("a/b.json", true)]
    [Arguments("a\\b.json", true)]
    [Arguments("dir/", true)]
    [Arguments("", false)]
    [Arguments("../x", false)]
    [Arguments("a/../../x", false)]
    [Arguments("a/./b", false)]
    [Arguments("a//b", false)]
    [Arguments("a/ /b", false)]
    [Arguments("/etc/passwd", false)]
    [Arguments("\\\\server\\share\\x", false)]
    [Arguments("C:\\x", false)]
    [Arguments("C:x", false)]
    [Arguments("file.txt:stream", false)]
    [Arguments("a\0b", false)]
    public async Task SafeRelativePath_RefusesEveryEscape(string path, bool safe)
    {
        await Assert.That(PathSafety.SafeRelativePath(path) is not null).IsEqualTo(safe);
    }

    [Test]
    public async Task SafeRelativePath_JoinsWithThePlatformSeparator()
    {
        await Assert.That(PathSafety.SafeRelativePath("a\\b/c.json")).IsEqualTo(Path.Combine("a", "b", "c.json"));
    }

    [Test]
    public async Task FullPathInside_StaysUnderTheRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvpathsafety");
        using (Assert.Multiple())
        {
            await Assert.That(PathSafety.FullPathInside(root, "data/x.json"))
                .IsEqualTo(Path.Combine(Path.GetFullPath(root), "data", "x.json"));
            await Assert.That(PathSafety.FullPathInside(root, "../dvpathsafety2/x")).IsNull();
            await Assert.That(PathSafety.FullPathInside(root, "/x")).IsNull();
        }
    }

    [Test]
    public async Task IsStrictlyInside_RefusesTheRootItselfAndASiblingWithTheSamePrefix()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvroot");
        using (Assert.Multiple())
        {
            await Assert.That(PathSafety.IsStrictlyInside(root, Path.Combine(root, "a"))).IsTrue();
            await Assert.That(PathSafety.IsStrictlyInside(root, root)).IsFalse();
            await Assert.That(PathSafety.IsStrictlyInside(root, root + "2")).IsFalse();
        }
    }

    [Test]
    public async Task FindCaseDuplicate_NamesTheSecondOfTwoCaseOnlyVariants()
    {
        using (Assert.Multiple())
        {
            await Assert.That(PathSafety.FindCaseDuplicate(["data/A.json", "data/b.json", "data/a.json"]))
                .IsEqualTo("data/a.json");
            await Assert.That(PathSafety.FindCaseDuplicate(["data/a.json", "data/b.json"])).IsNull();
        }
    }
}
