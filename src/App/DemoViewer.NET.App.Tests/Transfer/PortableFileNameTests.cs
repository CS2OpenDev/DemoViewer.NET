#region

using System.Text;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     A name from another machine becomes one every target file system accepts, and a name already in the
///     folder, in any case, gets a <c> (n)</c> suffix instead of overwriting it.
/// </summary>
public class PortableFileNameTests
{
    [Test]
    [Arguments("match.dem", "match.dem")]
    [Arguments("a<b>c:d\"e/f\\g|h?i*j.dem", "a_b_c_d_e_f_g_h_i_j.dem")]
    [Arguments("tab\there\u0001.dem", "tab_here_.dem")]
    [Arguments("del\u007F.dem", "del_.dem")]
    [Arguments("trailing. . .", "trailing")]
    [Arguments("  leading.dem", "leading.dem")]
    [Arguments("CON", "_CON")]
    [Arguments("con.dem", "_con.dem")]
    [Arguments("Nul.tar.gz", "_Nul.tar.gz")]
    [Arguments("COM1.dem", "_COM1.dem")]
    [Arguments("lpt9", "_lpt9")]
    [Arguments("COM10.dem", "COM10.dem")]
    [Arguments("CONSOLE.dem", "CONSOLE.dem")]
    [Arguments("", "demo.dem")]
    [Arguments("...", "demo.dem")]
    [Arguments("???", "demo.dem")]
    [Arguments("navi-vs-faze_ü.dem", "navi-vs-faze_ü.dem")]
    public async Task Sanitize_Table(string input, string expected)
    {
        await Assert.That(PortableFileName.Sanitize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task Sanitize_NormalizesToNfc_SoADecomposedNameMatchesItsComposedTwin()
    {
        string decomposed = "é.dem";
        await Assert.That(PortableFileName.Sanitize(decomposed)).IsEqualTo("é.dem");
    }

    [Test]
    public async Task Sanitize_CutsALongName_KeepingTheExtension_AndNeverSplittingACharacter()
    {
        string longName = string.Concat(Enumerable.Repeat("😀", 120)) + ".dem";
        string result = PortableFileName.Sanitize(longName);
        using (Assert.Multiple())
        {
            await Assert.That(Encoding.UTF8.GetByteCount(result)).IsLessThanOrEqualTo(PortableFileName.MaxUtf8Bytes);
            await Assert.That(result).EndsWith(".dem");
            await Assert.That(result.Normalize()).IsEqualTo(result);
            await Assert.That(char.IsHighSurrogate(result[^5])).IsFalse();
        }
    }

    [Test]
    public async Task MakeUnique_AddsTheFirstFreeSuffix()
    {
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase) { "match.dem", "MATCH (2).dem" };
        await Assert.That(PortableFileName.MakeUnique("Match.dem", taken.Contains)).IsEqualTo("Match (3).dem");
    }

    [Test]
    public async Task ANameSet_ClaimsCaseInsensitively_AndRemembersWhatItHandedOut()
    {
        PortableNameSet names = new(["Final.dem"]);
        using (Assert.Multiple())
        {
            await Assert.That(names.Claim("final.dem")).IsEqualTo("final (2).dem");
            await Assert.That(names.Claim("FINAL.dem")).IsEqualTo("FINAL (3).dem");
            await Assert.That(names.Claim("other?.dem")).IsEqualTo("other_.dem");
            await Assert.That(names.Claim("other_.dem")).IsEqualTo("other_ (2).dem");
        }
    }

    [Test]
    [Arguments("net.demoviewer.pack.stratbook", true)]
    [Arguments("rounds", true)]
    [Arguments("COM10.json", true)]
    [Arguments("auxiliary.json", true)]
    [Arguments("aux", false)]
    [Arguments("con", false)]
    [Arguments("nul.json", false)]
    [Arguments("LPT1.json.gz", false)]
    [Arguments("x.", false)]
    [Arguments("..", false)]
    public async Task ABundleSegment_IsOneWindowsCanCreate(string segment, bool portable) =>
        await Assert.That(TransferLayout.IsPortableSegment(segment)).IsEqualTo(portable);

    [Test]
    public async Task MakeUnique_OnAMaximalName_StaysWithinTheLimit()
    {
        string name = new string('a', PortableFileName.MaxUtf8Bytes - 4) + ".dem";
        string unique = PortableFileName.MakeUnique(name, n => n == name);
        using (Assert.Multiple())
        {
            await Assert.That(Encoding.UTF8.GetByteCount(unique)).IsLessThanOrEqualTo(PortableFileName.MaxUtf8Bytes);
            await Assert.That(unique).EndsWith(" (2).dem");
        }
    }
}
