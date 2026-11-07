#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>A throwaway rules tree: a shipped directory, a user directory, and an own folder for sidecars.</summary>
internal sealed class TempRules : IDisposable
{
    private readonly string _root;

    private TempRules(string root) => _root = root;

    private string Shipped => Path.Combine(_root, "shipped");

    private string User => Path.Combine(_root, "user");

    public string ShippedPath(string id) => Path.Combine(Shipped, id + ".rules.yaml");

    public string UserPath(string id) => Path.Combine(User, id + ".rules.yaml");

    /// <summary>The shipped rules directory of this checkout.</summary>
    public static string RepoRulesDirectory => Path.Combine(RepoRoot(), "rules");

    public static TempRules WithUser(string id)
    {
        TempRules rules = New();
        File.Copy(Path.Combine(RepoRoot(), "rules", id + ".rules.yaml"), rules.UserPath(id));
        return rules;
    }

    public static TempRules WithShipped(string id)
    {
        TempRules rules = New();
        File.Copy(Path.Combine(RepoRoot(), "rules", id + ".rules.yaml"), rules.ShippedPath(id));
        return rules;
    }

    public RulesetCorpus Load() => RulesetCorpus.Load([(Shipped, "shipped"), (User, "user")]);

    public CanvasTabViewModel NewViewModel() => new(_ => true, Load, () => Path.Combine(_root, "own"));

    private static TempRules New()
    {
        string root = Path.Combine(Path.GetTempPath(), "rulecanvas-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "shipped"));
        Directory.CreateDirectory(Path.Combine(root, "user"));
        return new TempRules(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A temp tree that outlives the test is tolerable.
        }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
