#region

using System.Reflection;
using Avalonia.Controls;
using DemoViewer.NET.Modules;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.Analysis;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The breakpoint-condition box's autocomplete, through the real view: the token under the caret and
///     the splice are code-behind, because the caret lives on the control.
/// </summary>
[NotInParallel]
[Category("Render")]
public class AnalysisConditionSuggestionTests
{
    // Accepting a suggestion sets the text programmatically and skips the one TextChanged that causes.
    // With the caret mid-token the chosen id can be the token that is already there: the text does not
    // change, nothing is raised, and the skip used to stay armed and swallow the user's next keystroke.
    [Test]
    public async Task AcceptingASuggestionThatChangesNothing_DoesNotSwallowTheNextEdit()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel shell = new(null, new ModuleRegistry(), TestLibraries.Empty());
            AnalysisViewModel analysis = shell.Analysis;
            try
            {
                // The pool an open editor would have; opening one needs an evaluated demo.
                typeof(AnalysisViewModel)
                    .GetField("_activeConditionIdentifiers", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(analysis, (IReadOnlyList<string>)["abc", "abd"]);

                AnalysisTabView view = new() { DataContext = shell };
                Window window = new() { Width = 1400, Height = 800, Content = view };
                window.Show();
                Playback2DTimelineHarness.Pump();

                TextBox box = view.FindControl<TextBox>("ConditionTextBox")!;
                ListBox list = view.FindControl<ListBox>("ConditionSuggestionsList")!;

                box.Text = "abc";
                box.CaretIndex = 2;
                Playback2DTimelineHarness.Pump();
                box.Text = "abc ";
                box.CaretIndex = 2;
                Playback2DTimelineHarness.Pump();
                box.Text = "abc";
                box.CaretIndex = 2;
                Playback2DTimelineHarness.Pump();
                await Assert.That(analysis.ConditionSuggestions).Contains("abc")
                    .Because("the prefix is 'ab', and the whole token is one of its completions");

                list.SelectedItem = "abc";
                Playback2DTimelineHarness.Pump();
                await Assert.That(box.Text).IsEqualTo("abc");
                await Assert.That(analysis.HasConditionSuggestions).IsFalse();

                box.Text = "ab";
                box.CaretIndex = 2;
                Playback2DTimelineHarness.Pump();
                await Assert.That(analysis.HasConditionSuggestions).IsTrue()
                    .Because("this edit is the user's, and it has two completions");

                window.Close();
            }
            finally
            {
                analysis.Dispose();
            }
        });
    }
}
