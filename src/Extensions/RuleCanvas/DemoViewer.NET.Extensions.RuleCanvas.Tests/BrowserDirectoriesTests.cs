#region

using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The browser build reads no user rules folder for analysis, so the canvas offers none there: every
///     ruleset is shipped and read-only, as MergedRulesBuild has it.
/// </summary>
[NotInParallel]
public class BrowserDirectoriesTests
{
    [Test]
    public async Task OnTheBrowser_OnlyTheShippedDirectoryIsOffered()
    {
        string user = Directory.CreateTempSubdirectory("rulecanvas-browser-").FullName;
        string? was = Environment.GetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar);
        Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, user);
        try
        {
            IReadOnlyList<(string Directory, string Source)> desktop = RulesetCorpus.DefaultDirectories(browser: false);
            IReadOnlyList<(string Directory, string Source)> browser = RulesetCorpus.DefaultDirectories(browser: true);
            using (Assert.Multiple())
            {
                await Assert.That(desktop.Select(d => d.Source)).Contains("shipped");
                await Assert.That(desktop.Any(d => RulesetCorpus.IsUserSource(d.Source))).IsTrue().Because("the desktop offers the user folder");
                await Assert.That(browser.Select(d => d.Source)).IsEquivalentTo(["shipped"]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuleSetLocator.UserRulesDirEnvVar, was);
            Directory.Delete(user, true);
        }
    }
}
