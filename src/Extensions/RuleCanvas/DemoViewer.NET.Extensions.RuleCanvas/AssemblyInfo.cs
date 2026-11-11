#region

using System.Runtime.CompilerServices;

#endregion

// The app's tests and the UiCapture variants read the pack's internals the same way they read the Strat
// Book's; see the app's AssemblyInfo.
[assembly: InternalsVisibleTo("DemoViewer.NET.App.Tests")]
[assembly: InternalsVisibleTo("DemoViewer.NET.UiCapture")]
[assembly: InternalsVisibleTo("DemoViewer.NET.Extensions.RuleCanvas.Tests")]
