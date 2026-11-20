#region

using DemoViewer.NET.Services.Diagnostics;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The sentence a failure is shown as. Several of the exception families nest, so the order the
///     arms are tried in decides which sentence a subclass gets.
/// </summary>
public class UserFacingErrorTests
{
    /// <summary>
    ///     <see cref="EndOfStreamException" /> is an <see cref="IOException" />: a cut-short file must still
    ///     read as damaged data, not as a disk or permissions problem.
    /// </summary>
    [Test]
    public async Task ATruncatedFile_ReadsAsDamagedData_NotAsAnIoFailure()
    {
        string truncated = UserFacingError.Describe("load the demo", new EndOfStreamException());
        string invalid = UserFacingError.Describe("load the demo", new InvalidDataException());

        using (Assert.Multiple())
        {
            await Assert.That(truncated).Contains("damaged or truncated");
            await Assert.That(invalid).Contains("damaged or truncated");
        }
    }

    [Test]
    public async Task TheOtherIoFamilies_KeepTheirOwnSentences()
    {
        using (Assert.Multiple())
        {
            await Assert.That(UserFacingError.Describe("load the demo", new FileNotFoundException()))
                .Contains("a file it needs is missing");
            await Assert.That(UserFacingError.Describe("load the demo", new DirectoryNotFoundException()))
                .Contains("a file it needs is missing");
            await Assert.That(UserFacingError.Describe("load the demo", new PathTooLongException()))
                .Contains("a file path is too long");
            await Assert.That(UserFacingError.Describe("load the demo", new IOException()))
                .Contains("could not be read or written");
        }
    }
}
