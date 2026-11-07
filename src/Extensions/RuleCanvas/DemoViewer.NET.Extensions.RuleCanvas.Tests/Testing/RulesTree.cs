#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A throwaway rules tree: a shipped directory, a user directory, and an own folder for sidecars, the
///     way the corpus reads them beside the app. Every test that needs a ruleset on disk writes it here,
///     copied from the repo's <c>rules/</c> or given as text, and nothing touches the real corpus.
/// </summary>
internal sealed class RulesTree : IDisposable
{
    private RulesTree(string root)
    {
        Root = root;
        Directory.CreateDirectory(ShippedDirectory);
        Directory.CreateDirectory(UserDirectory);
    }

    /// <summary>The shipped rules directory of this checkout, for a test that reads the real corpus read-only.</summary>
    public static string RepoRulesDirectory => Path.Combine(RepoRoot(), "rules");

    public string Root { get; }

    public string ShippedDirectory => Path.Combine(Root, "shipped");

    public string UserDirectory => Path.Combine(Root, "user");

    /// <summary>The extension's own folder, where a shipped ruleset's arrangement is kept.</summary>
    public string OwnFolder => Path.Combine(Root, "own");

    public string ShippedPath(string id) => Path.Combine(ShippedDirectory, id + ".rules.yaml");

    public string UserPath(string id) => Path.Combine(UserDirectory, id + ".rules.yaml");

    /// <summary>An empty tree.</summary>
    public static RulesTree New() =>
        new(Path.Combine(Path.GetTempPath(), "rulecanvas-" + Guid.NewGuid().ToString("N")));

    /// <summary>A tree with the repo's <paramref name="id" /> copied into the user directory.</summary>
    public static RulesTree WithUser(string id)
    {
        RulesTree tree = New();
        tree.UserCopyOf(id);
        return tree;
    }

    /// <summary>A tree with the repo's <paramref name="id" /> copied into the shipped directory.</summary>
    public static RulesTree WithShipped(string id)
    {
        RulesTree tree = New();
        tree.ShippedCopyOf(id);
        return tree;
    }

    /// <summary>Writes <paramref name="yamlText" /> as the shipped ruleset <paramref name="id" />; returns its path.</summary>
    public string Shipped(string id, string yamlText) => Touch(ShippedPath(id), yamlText);

    /// <summary>Writes <paramref name="yamlText" /> as the user ruleset <paramref name="id" />; returns its path.</summary>
    public string User(string id, string yamlText) => Touch(UserPath(id), yamlText);

    /// <summary>Copies the repo's <paramref name="id" /> into the shipped directory; returns its path.</summary>
    public string ShippedCopyOf(string id)
    {
        File.Copy(Path.Combine(RepoRulesDirectory, id + ".rules.yaml"), ShippedPath(id));
        return ShippedPath(id);
    }

    /// <summary>Copies the repo's <paramref name="id" /> into the user directory; returns its path.</summary>
    public string UserCopyOf(string id)
    {
        File.Copy(Path.Combine(RepoRulesDirectory, id + ".rules.yaml"), UserPath(id));
        return UserPath(id);
    }

    /// <summary>Writes a user file under <paramref name="id" /> whose text does not load as a ruleset; returns its path.</summary>
    public string Broken(string id, string text) => Touch(UserPath(id), text);

    /// <summary>Writes <paramref name="text" /> at <paramref name="path" />, creating the directory; returns the path.</summary>
    public static string Touch(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>The corpus over the two directories, as the app loads it.</summary>
    public RulesetCorpus Load() => RulesetCorpus.Load([(ShippedDirectory, "shipped"), (UserDirectory, "user")]);

    /// <summary>A tab view model over this tree, with the own folder for sidecars.</summary>
    public CanvasTabViewModel NewViewModel() => new(_ => true, Load, () => OwnFolder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
