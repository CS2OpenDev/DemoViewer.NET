#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The condition model over the shipped corpus and over hand-written shapes: every <c>where:</c>,
///     <c>when:</c>, <c>while:</c> and <c>compute:</c> text in <c>rules/</c> parses and writes back byte
///     for byte, row edits touch only the row named, and the vocabulary for a view is the catalog's.
/// </summary>
public class ConditionExpressionTests
{
    private const string PlayerRoundKast = "round_kills > 0 or round_kast_assists > 0 or player.survived or player.traded";

    private const string KastHasKast =
        "enemy_kills_round > 0 or team_assists_round > 0 or player.survived or (player.traded and round.number > 0)";

    private const string KastNadeDamage =
        "event.Attacker != event.UserId && (event.Weapon == \"hegrenade\" || event.Weapon == \"inferno\" || event.Weapon == \"molotov\")";

    // ── the corpus ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The measured shape distribution, printed so the flat-row design can be read against the
    ///     corpus it serves rather than against the grammar it could serve.
    /// </summary>
    [Test]
    public async Task Survey_PrintsTheCorpusShapeDistribution()
    {
        Corpus corpus = Corpus.Load();
        Dictionary<string, int> shapes = new(StringComparer.Ordinal);
        Dictionary<string, int> byKey = new(StringComparer.Ordinal);
        foreach (Site site in corpus.Sites)
        {
            ConditionExpression expr = ConditionExpression.Parse(site.Text);
            string shape = Shape(expr);
            shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
            byKey[site.Key] = byKey.GetValueOrDefault(site.Key) + 1;
        }

        Console.WriteLine($"[survey] sites={corpus.Sites.Count} by key: "
                          + string.Join(", ", byKey.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")));
        foreach (KeyValuePair<string, int> pair in shapes.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"[survey] {pair.Value,4}  {pair.Key}");
        }

        int flat = corpus.Sites.Count(s => ConditionExpression.Parse(s.Text).IsFlat);
        Console.WriteLine($"[survey] flat={flat}/{corpus.Sites.Count} ({flat * 100 / corpus.Sites.Count}%)");

        await Assert.That(corpus.Sites.Count).IsGreaterThanOrEqualTo(300)
            .Because($"only {corpus.Sites.Count} expression sites were read from rules/; the grep over the "
                     + "corpus counts 331 lines");
        await Assert.That(flat * 100 / corpus.Sites.Count).IsGreaterThanOrEqualTo(90)
            .Because("the row projection is the design because almost every corpus expression is flat");
    }

    /// <summary>The splice promise at expression level: parse then write, unchanged, is the author's bytes.</summary>
    [Test]
    public async Task EveryCorpusExpression_ParsesAndWritesBackByteIdentical()
    {
        Corpus corpus = Corpus.Load();
        List<string> errors = [];
        List<string> drifted = [];
        foreach (Site site in corpus.Sites)
        {
            ConditionExpression expr = ConditionExpression.Parse(site.Text);
            if (expr.Error is not null)
            {
                errors.Add($"{site}: {expr.Error}");
            }

            if (!string.Equals(expr.ToText(), site.Text, StringComparison.Ordinal))
            {
                drifted.Add($"{site} -> '{expr.ToText()}'");
            }
        }

        foreach (string line in errors.Concat(drifted))
        {
            Console.WriteLine("[roundtrip] " + line);
        }

        Console.WriteLine($"[roundtrip] sites={corpus.Sites.Count} errors={errors.Count} drifted={drifted.Count}");

        using (Assert.Multiple())
        {
            await Assert.That(errors.Count).IsEqualTo(0).Because(string.Join("; ", errors));
            await Assert.That(drifted.Count).IsEqualTo(0).Because(string.Join("; ", drifted));
        }
    }

    /// <summary>
    ///     Rewriting every row of every flat expression from its own parts is also byte-identical, so
    ///     an edit that changes one row cannot respace the others: the row writer and the parser agree
    ///     on where a row's bytes are.
    /// </summary>
    [Test]
    public async Task EveryFlatCorpusRow_RewrittenFromItsParts_IsByteIdentical()
    {
        Corpus corpus = Corpus.Load();
        List<string> drifted = [];
        int rows = 0;
        foreach (Site site in corpus.Sites)
        {
            ConditionExpression expr = ConditionExpression.Parse(site.Text);
            if (!expr.IsFlat)
            {
                continue;
            }

            for (int i = 0; i < expr.Rows.Count; i++)
            {
                rows++;
                ConditionRow row = expr.Rows[i];
                string rewritten = expr.WithRow(i, row.Path, row.Comparator, row.Literal);
                if (!string.Equals(rewritten, site.Text, StringComparison.Ordinal))
                {
                    drifted.Add($"{site} row {i} -> '{rewritten}'");
                }
            }
        }

        foreach (string line in drifted)
        {
            Console.WriteLine("[rows] " + line);
        }

        Console.WriteLine($"[rows] rows={rows} drifted={drifted.Count}");

        await Assert.That(rows).IsGreaterThan(300);
        await Assert.That(drifted.Count).IsEqualTo(0).Because(string.Join("; ", drifted));
    }

    /// <summary>
    ///     Which corpus paths the catalog vocabulary does not know, measured so the model's comment
    ///     about what the catalog does not carry is a list and not a guess.
    /// </summary>
    [Test]
    public async Task Vocabulary_CoversTheCorpusPaths_ExceptTheDocumentedFamilies()
    {
        Corpus corpus = Corpus.Load();
        SortedSet<string> unknown = new(StringComparer.Ordinal);
        int known = 0;
        foreach (Site site in corpus.Sites.Where(s => s.Key is not "compute"))
        {
            ConditionExpression expr = ConditionExpression.Parse(site.Text);
            if (!expr.IsFlat)
            {
                continue;
            }

            HashSet<string> paths = new(
                ConditionVocabulary.PathsFor(EmbeddedCatalog.Root, site.View, site.Siblings), StringComparer.Ordinal);
            foreach (ConditionRow row in expr.Rows)
            {
                foreach (string path in new[] { row.Path, row.Literal }.Where(LooksLikeAPath).Cast<string>())
                {
                    if (paths.Contains(path))
                    {
                        known++;
                    }
                    else
                    {
                        unknown.Add(path);
                    }
                }
            }
        }

        Console.WriteLine($"[vocabulary] known={known} unknown={unknown.Count}: " + string.Join(", ", unknown));

        string[] outside =
        [
            .. unknown.Where(p => !p.StartsWith("params.", StringComparison.Ordinal)
                                  && !p.StartsWith("player.", StringComparison.Ordinal)
                                  && !p.StartsWith("enrich.", StringComparison.Ordinal))
        ];

        await Assert.That(known).IsGreaterThan(200);
        await Assert.That(outside.Length).IsEqualTo(0)
            .Because("a path family the ConditionVocabulary comment does not list: " + string.Join(", ", outside));
    }

    // ── rows on real expressions ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task PlayerRound_KastWhen_IsFourRowsJoinedByOr()
    {
        ConditionExpression expr = ConditionExpression.Parse(PlayerRoundKast);
        using (Assert.Multiple())
        {
            await Assert.That(expr.IsFlat).IsTrue();
            await Assert.That(expr.Rows.Count).IsEqualTo(4);
            await Assert.That(expr.Connective).IsEqualTo(ConditionConnective.Or);
            await Assert.That(expr.ConnectiveSpelling).IsEqualTo("or");
            await Assert.That(expr.Rows[0]).IsEqualTo(new ConditionRow("round_kills", ">", "0", "round_kills > 0"));
            await Assert.That(expr.Rows[2]).IsEqualTo(new ConditionRow("player.survived", null, null, "player.survived"));
        }
    }

    [Test]
    public async Task PlayerRound_EditingOneRow_LeavesTheOthersByteIdentical()
    {
        ConditionExpression expr = ConditionExpression.Parse(PlayerRoundKast);
        using (Assert.Multiple())
        {
            await Assert.That(expr.WithRow(0, "round_kills", ">=", "2"))
                .IsEqualTo("round_kills >= 2 or round_kast_assists > 0 or player.survived or player.traded");
            await Assert.That(expr.WithRow(3, "player.alive", null, null))
                .IsEqualTo("round_kills > 0 or round_kast_assists > 0 or player.survived or player.alive");
            await Assert.That(expr.WithRow(2, "player.survived", "==", "true"))
                .IsEqualTo("round_kills > 0 or round_kast_assists > 0 or player.survived == true or player.traded");
        }
    }

    [Test]
    public async Task PlayerRound_AddingAndRemovingRows_KeepsTheAuthorsConnective()
    {
        ConditionExpression expr = ConditionExpression.Parse(PlayerRoundKast);
        using (Assert.Multiple())
        {
            await Assert.That(expr.WithRowAdded("round_deaths", "==", "0", ConditionConnective.Or))
                .IsEqualTo(PlayerRoundKast + " or round_deaths == 0");
            await Assert.That(expr.WithRowRemoved(3))
                .IsEqualTo("round_kills > 0 or round_kast_assists > 0 or player.survived");
            await Assert.That(expr.WithRowRemoved(0))
                .IsEqualTo("round_kast_assists > 0 or player.survived or player.traded");
        }

        await Assert.That(() => expr.WithRowAdded("round_deaths", "==", "0"))
            .Throws<InvalidOperationException>()
            .Because("an and row appended to an or list changes what the author's rows mean");
    }

    [Test]
    public async Task PlayerStats_PistolWhere_IsTenRowsJoinedBySymbolicOr()
    {
        const string pistols =
            "event.Weapon == \"glock\" || event.Weapon == \"hkp2000\" || event.Weapon == \"usp_silencer\" || event.Weapon == \"p250\" || event.Weapon == \"deagle\" || event.Weapon == \"fiveseven\" || event.Weapon == \"tec9\" || event.Weapon == \"cz75a\" || event.Weapon == \"elite\" || event.Weapon == \"revolver\"";
        ConditionExpression expr = ConditionExpression.Parse(pistols);
        using (Assert.Multiple())
        {
            await Assert.That(expr.Rows.Count).IsEqualTo(10);
            await Assert.That(expr.ConnectiveSpelling).IsEqualTo("||");
            await Assert.That(expr.Rows[9].Literal).IsEqualTo("\"revolver\"");
            await Assert.That(expr.WithRowAdded("event.Weapon", "==", "\"r8\"", ConditionConnective.Or))
                .IsEqualTo(pistols + " || event.Weapon == \"r8\"");
            await Assert.That(expr.WithRowRemoved(9))
                .IsEqualTo(pistols[..pistols.LastIndexOf(" || ", StringComparison.Ordinal)]);
        }
    }

    [Test]
    public async Task Kast_Where_KeepsTheSymbolicAnd_AndTheBooleanLiteral()
    {
        ConditionExpression expr = ConditionExpression.Parse("enrich.kill.was_enemy_kill == true");
        ConditionExpression and = ConditionExpression.Parse("enrich.clutch.was_clutch_won && enrich.clutch.winner_slot == player.slot");
        using (Assert.Multiple())
        {
            await Assert.That(expr.Rows.Count).IsEqualTo(1);
            await Assert.That(expr.Connective).IsNull();
            await Assert.That(expr.Rows[0]).IsEqualTo(new ConditionRow("enrich.kill.was_enemy_kill", "==", "true", expr.Text));
            await Assert.That(and.Rows.Count).IsEqualTo(2);
            await Assert.That(and.ConnectiveSpelling).IsEqualTo("&&");
            await Assert.That(and.Rows[0].Comparator).IsNull();
            await Assert.That(and.Rows[1].Literal).IsEqualTo("player.slot");
            await Assert.That(and.WithRowAdded("round.clutch.size", "==", "2"))
                .IsEqualTo(and.Text + " && round.clutch.size == 2");
        }
    }

    // ── shapes ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Kast_HasKastWhen_IsNotFlat_BecauseOfTheGroup()
    {
        ConditionExpression expr = ConditionExpression.Parse(KastHasKast);
        using (Assert.Multiple())
        {
            await Assert.That(expr.IsFlat).IsFalse();
            await Assert.That(expr.Rows.Count).IsEqualTo(0);
            await Assert.That(expr.Error).IsNull();
            await Assert.That(expr.ToText()).IsEqualTo(KastHasKast);
        }

        ConditionJunction root = (ConditionJunction)expr.Root!;
        ConditionGroup group = (ConditionGroup)root.Operands[3];
        ConditionJunction inner = (ConditionJunction)group.Inner;
        using (Assert.Multiple())
        {
            await Assert.That(root.Connective).IsEqualTo(ConditionConnective.Or);
            await Assert.That(root.Operands.Count).IsEqualTo(4);
            await Assert.That(root.Separators).IsEquivalentTo([" or ", " or ", " or "]);
            await Assert.That(group.Text).IsEqualTo("(player.traded and round.number > 0)");
            await Assert.That(inner.Connective).IsEqualTo(ConditionConnective.And);
            await Assert.That(inner.Operands[1].Text).IsEqualTo("round.number > 0");
        }

        await Assert.That(() => expr.WithRow(0, "x", null, null)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Kast_NadeDamageWhere_IsNotFlat_AndTheGroupKeepsItsSpelling()
    {
        ConditionExpression expr = ConditionExpression.Parse(KastNadeDamage);
        ConditionJunction root = (ConditionJunction)expr.Root!;
        using (Assert.Multiple())
        {
            await Assert.That(expr.IsFlat).IsFalse();
            await Assert.That(root.Connective).IsEqualTo(ConditionConnective.And);
            await Assert.That(root.Separators[0]).IsEqualTo(" && ");
            await Assert.That(root.Operands[0].Text).IsEqualTo("event.Attacker != event.UserId");
            await Assert.That(((ConditionGroup)root.Operands[1]).Inner).IsTypeOf<ConditionJunction>();
            await Assert.That(expr.ToText()).IsEqualTo(KastNadeDamage);
        }
    }

    [Test]
    public async Task MixedPrecedenceWithoutParentheses_IsNotFlat()
    {
        ConditionExpression expr = ConditionExpression.Parse("a && b || c");
        ConditionJunction root = (ConditionJunction)expr.Root!;
        using (Assert.Multiple())
        {
            await Assert.That(expr.IsFlat).IsFalse();
            await Assert.That(root.Connective).IsEqualTo(ConditionConnective.Or);
            await Assert.That(root.Operands[0].Text).IsEqualTo("a && b");
            await Assert.That(root.Operands[1].Text).IsEqualTo("c");
        }
    }

    [Test]
    public async Task Negation_IsKeptAsTextOnly()
    {
        ConditionExpression word = ConditionExpression.Parse("not player.traded");
        ConditionExpression bang = ConditionExpression.Parse("!player.traded && round.active");
        using (Assert.Multiple())
        {
            await Assert.That(word.IsFlat).IsFalse();
            await Assert.That(word.Root).IsTypeOf<ConditionNegation>();
            await Assert.That(((ConditionNegation)word.Root!).Spelling).IsEqualTo("not");
            await Assert.That(bang.IsFlat).IsFalse();
            await Assert.That(((ConditionJunction)bang.Root!).Operands[0].Text).IsEqualTo("!player.traded");
            await Assert.That(bang.ToText()).IsEqualTo("!player.traded && round.active");
        }
    }

    [Test]
    public async Task AuthorsSpacing_InsideARow_SurvivesAnEditOfThatRow()
    {
        ConditionExpression expr = ConditionExpression.Parse("  a==1&&b  ==  2 ");
        using (Assert.Multiple())
        {
            await Assert.That(expr.Rows.Count).IsEqualTo(2);
            await Assert.That(expr.ToText()).IsEqualTo("  a==1&&b  ==  2 ");
            await Assert.That(expr.WithRow(0, "a", "!=", "3")).IsEqualTo("  a!=3&&b  ==  2 ");
            await Assert.That(expr.WithRow(1, "b", ">", "4")).IsEqualTo("  a==1&&b  >  4 ");
            await Assert.That(expr.WithRowRemoved(1)).IsEqualTo("  a==1 ");
        }
    }

    [Test]
    public async Task MembershipAndArithmetic_AreOneRowEach()
    {
        ConditionExpression members = ConditionExpression.Parse("event.Weapon in [awp, ssg08]");
        ConditionExpression formula = ConditionExpression.Parse("enemy_hits / max(bullet_shots, 1) * 100");
        ConditionExpression bound = ConditionExpression.Parse("money >= 0 and money <= params.money_sane_max * players");
        ConditionExpression grouped = ConditionExpression.Parse("(a + b) > 3 and c");
        using (Assert.Multiple())
        {
            await Assert.That(members.Rows[0]).IsEqualTo(new ConditionRow("event.Weapon", "in", "[awp, ssg08]", members.Text));
            await Assert.That(formula.Rows.Count).IsEqualTo(1);
            await Assert.That(formula.Rows[0].Comparator).IsNull();
            await Assert.That(formula.Rows[0].Path).IsEqualTo(formula.Text);
            await Assert.That(bound.Rows.Count).IsEqualTo(2);
            await Assert.That(bound.ConnectiveSpelling).IsEqualTo("and");
            await Assert.That(bound.Rows[1].Literal).IsEqualTo("params.money_sane_max * players");
            await Assert.That(grouped.IsFlat).IsTrue();
            await Assert.That(grouped.Rows[0]).IsEqualTo(new ConditionRow("(a + b)", ">", "3", "(a + b) > 3"));
        }
    }

    [Test]
    public async Task EmptyAndBrokenTexts_AreTolerated()
    {
        ConditionExpression empty = ConditionExpression.Parse("");
        ConditionExpression missing = ConditionExpression.Parse("event.Weapon == ");
        ConditionExpression unclosed = ConditionExpression.Parse("(a || b");
        ConditionExpression stray = ConditionExpression.Parse("a == 1 b");
        ConditionExpression unterminated = ConditionExpression.Parse("event.Weapon == \"awp");
        using (Assert.Multiple())
        {
            await Assert.That(empty.IsFlat).IsTrue();
            await Assert.That(empty.Rows.Count).IsEqualTo(0);
            await Assert.That(empty.WithRowAdded("player.alive", null, null)).IsEqualTo("player.alive");
            await Assert.That(missing.Error).IsNotNull();
            await Assert.That(missing.IsFlat).IsFalse();
            await Assert.That(missing.ToText()).IsEqualTo("event.Weapon == ");
            await Assert.That(unclosed.Error).IsNotNull();
            await Assert.That(stray.Error).IsNotNull();
            await Assert.That(unterminated.Error).IsNotNull();
        }

        await Assert.That(() => missing.WithRowAdded("x", null, null)).Throws<InvalidOperationException>();
    }

    // ── vocabulary ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Vocabulary_ForKill_IsTheDeathEventFacetsContextsAndSiblings()
    {
        List<string> paths = [.. ConditionVocabulary.PathsFor(EmbeddedCatalog.Root, "kill", ["round_kills", "round_deaths"])];
        Console.WriteLine("[vocabulary] kill: " + string.Join(", ", paths));
        using (Assert.Multiple())
        {
            await Assert.That(paths).Contains("event.Weapon");
            await Assert.That(paths).Contains("event.Attacker");
            await Assert.That(paths).Contains("event.AssistedFlash");
            await Assert.That(paths).Contains("enrich.kill.was_enemy_kill");
            await Assert.That(paths).Contains("enrich.kill.ticks_since_spot");
            await Assert.That(paths).Contains("round.number");
            await Assert.That(paths).Contains("match.live");
            await Assert.That(paths).Contains("player.alive");
            await Assert.That(paths).Contains("round_kills");
            await Assert.That(paths).DoesNotContain("enrich.hurt.was_enemy_damage");
            await Assert.That(paths.Count).IsEqualTo(paths.Distinct(StringComparer.Ordinal).Count());
            await Assert.That(paths.IndexOf("event.Weapon")).IsLessThan(paths.IndexOf("enrich.kill.was_enemy_kill"));
            await Assert.That(paths.IndexOf("enrich.kill.was_enemy_kill")).IsLessThan(paths.IndexOf("round.number"));
            await Assert.That(paths.IndexOf("round.number")).IsLessThan(paths.IndexOf("round_kills"));
        }
    }

    [Test]
    public async Task Vocabulary_ForDamageDealt_IsTheHurtEventAndItsFacets()
    {
        IReadOnlyList<string> paths = ConditionVocabulary.PathsFor(EmbeddedCatalog.Root, "damage_dealt");
        IReadOnlyList<string> none = ConditionVocabulary.PathsFor(EmbeddedCatalog.Root, null, ["flick_shots"]);
        Console.WriteLine("[vocabulary] damage_dealt: " + string.Join(", ", paths));
        using (Assert.Multiple())
        {
            await Assert.That(paths).Contains("event.DmgHealth");
            await Assert.That(paths).Contains("event.HitGroup");
            await Assert.That(paths).Contains("enrich.hurt.was_enemy_damage");
            await Assert.That(paths).Contains("enrich.hurt.capped_damage");
            await Assert.That(paths).DoesNotContain("enrich.kill.was_enemy_kill");
            await Assert.That(paths).DoesNotContain("event.AssistedFlash");
            await Assert.That(none.Any(p => p.StartsWith("event.", StringComparison.Ordinal))).IsFalse();
            await Assert.That(none.Any(p => p.StartsWith("enrich.", StringComparison.Ordinal))).IsFalse();
            await Assert.That(none).Contains("round.active");
            await Assert.That(none).Contains("flick_shots");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string Shape(ConditionExpression expr)
    {
        if (expr.Error is not null)
        {
            return "error";
        }

        if (expr.IsFlat)
        {
            if (expr.Rows.Count == 1)
            {
                ConditionRow row = expr.Rows[0];
                if (row.Comparator is not null)
                {
                    return "one comparison " + row.Comparator;
                }

                return row.Path.Any(c => c is '+' or '-' or '*' or '/' or '%') ? "arithmetic formula" : "bare path";
            }

            return $"flat {expr.ConnectiveSpelling} x{expr.Rows.Count}";
        }

        return expr.Root switch
        {
            ConditionJunction j => $"nested: {Describe(j)}",
            ConditionNegation => "nested: negation",
            ConditionGroup => "nested: group",
            _ => "nested"
        };
    }

    private static string Describe(ConditionJunction junction) =>
        junction.Separators[0].Trim() + " over [" + string.Join(", ", junction.Operands.Select(o => o switch
        {
            ConditionLeaf => "leaf",
            ConditionGroup { Inner: ConditionJunction inner } => "(" + Describe(inner) + ")",
            ConditionGroup => "(leaf)",
            ConditionJunction inner => Describe(inner),
            ConditionNegation => "not",
            _ => "?"
        })) + "]";

    private static bool LooksLikeAPath(string? text) =>
        text is not null && text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_')
        && text is not ("true" or "false")
        && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

    private sealed record Site(string Ruleset, string Id, string Key, string Text, string? View, IReadOnlyList<string> Siblings)
    {
        public override string ToString() => $"{Ruleset}/{Id} {Key}: '{Text}'";
    }

    /// <summary>Every expression site in rules/, with the view the engine resolved for its node.</summary>
    private sealed record Corpus(IReadOnlyList<Site> Sites)
    {
        internal static Corpus Load()
        {
            string root = DemoTestHelper.FindRepoRoot()
                          ?? throw new SkipTestException("repo root not found from the test bin");
            RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(Path.Combine(root, "rules"));
            if (!loaded.Success)
            {
                throw new InvalidOperationException("rules/ did not load: "
                                                    + string.Join("; ", loaded.Errors.Select(e => e.ToString())));
            }

            RulesetComposition.Result composed = RulesetComposition.Compose(
                loaded.Rulesets,
                CatalogScopeAdapter.From(CatalogResource.Load()),
                64.0,
                DemoSourceProfileRegistry.DefaultFallback.GetType().Name);
            Dictionary<RulesetDocumentNodeKey, string?> views = RulesetDocumentGraph
                .Build(composed.Rulesets, openRulesetId: null).Nodes
                .ToDictionary(n => n.Key, n => string.IsNullOrEmpty(n.View) ? null : n.View);

            List<Site> sites = [];
            foreach (RulesetDoc doc in loaded.Rulesets)
            {
                string[] siblings = [.. doc.Stats.Select(s => s.Id)];
                foreach (StatDef stat in doc.Stats)
                {
                    string? view = views.GetValueOrDefault(new RulesetDocumentNodeKey(doc.Id, stat.Id));
                    void Add(string key, string? text)
                    {
                        if (!string.IsNullOrEmpty(text))
                        {
                            sites.Add(new Site(doc.Id, stat.Id, key, text, view, siblings));
                        }
                    }

                    string kind = stat.Kind.ToString()?.ToLowerInvariant() ?? "";
                    Add(kind is "compute" ? "compute" : kind is "flag" ? "when" : "kind-arg", kind is "compute" or "flag" ? stat.KindArg : null);
                    Add("where", stat.Trigger?.Where);
                    Add("while", stat.Trigger?.While);
                    Add("off.where", stat.OffTrigger?.Where);
                    Add("off.while", stat.OffTrigger?.While);
                }

                foreach (HighlightDef highlight in doc.Highlights)
                {
                    if (!string.IsNullOrEmpty(highlight.When))
                    {
                        sites.Add(new Site(doc.Id, highlight.Id, "when", highlight.When, null, siblings));
                    }
                }
            }

            return new Corpus(sites);
        }
    }
}
