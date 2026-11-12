#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>The wizard's conflict policy and the one extensions are handed mean the same thing, value for value.</summary>
public class ConflictPoliciesTests
{
    [Test]
    public async Task EveryPolicy_MapsOneToOne()
    {
        ConflictPolicy[] core = Enum.GetValues<ConflictPolicy>();
        ImportConflictPolicy[] sdk = Enum.GetValues<ImportConflictPolicy>();
        await Assert.That(core.Length).IsEqualTo(sdk.Length);
        foreach (ConflictPolicy policy in core)
        {
            ImportConflictPolicy mapped = policy.ToSdk();
            await Assert.That(mapped.ToString()).IsEqualTo(policy.ToString());
            await Assert.That((int)mapped).IsEqualTo((int)policy);
            await Assert.That(mapped.FromSdk()).IsEqualTo(policy);
        }
    }

    [Test]
    public async Task UndefinedValues_Throw()
    {
        await Assert.That(() => ((ConflictPolicy)7).ToSdk()).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ((ImportConflictPolicy)7).FromSdk()).Throws<ArgumentOutOfRangeException>();
    }
}
