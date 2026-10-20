using System.Text.RegularExpressions;

namespace CreatorPantry.Tests.Architecture;

/// <summary>
/// The three deterministic safety analyzers are pure, enforced rather than promised (14.7, M-3 of the audit).
/// </summary>
/// <remarks>
/// <para>
/// <c>DietaryAllergenAnalyzer</c>, <c>NutritionEstimateCalculator</c> and <c>FoodSafetyWarningDetector</c> are
/// each documented as pure — the same version, selection and records give the same answer, with no clock, no
/// model, no I/O. Three baselines rest on it: B-35 records the method version on every estimate so one
/// computed under different rules can be told apart, B-36 records the date the records were read *as an input*
/// rather than reading a clock, and B-37's warnings are reproducible from a version's own text.
/// </para>
/// <para>
/// Until this test, that was convention: the code was pure, and nothing stopped the next edit injecting a
/// <c>TimeProvider</c> "just for a default" or an <c>IIngredientRepository</c> "just to look one thing up". A
/// clock inside one of these would make an analysis unreproducible; a repository would make it unfilterable by
/// workspace.
/// </para>
/// <para>
/// A text scan rather than reflection: what matters is that these files do not *name* the forbidden things,
/// and a scan says which line to look at when it fails. The clock belongs in Business, where
/// <c>IDietaryAllergenAnalysisBusiness</c> injects it and passes the date in.
/// </para>
/// </remarks>
public sealed class SafetyAnalyzerPurityTests
{
    private static readonly string[] PureFiles =
    [
        "Modules/Nutrition/Managers/DietaryAllergenAnalyzer.cs",
        "Modules/Nutrition/Managers/NutritionEstimateCalculator.cs",
        "Modules/Nutrition/Managers/FoodSafetyWarningDetector.cs",
    ];

    /// <summary>
    /// What a pure analyzer may not name, and what each would cost.
    /// </summary>
    /// <remarks>
    /// <c>DateTime</c>/<c>DateTimeOffset</c>/<c>TimeProvider</c>: an answer that changes with the day.
    /// <c>Random</c>/<c>Guid.NewGuid</c>: an answer that changes between two identical calls.
    /// <c>Http</c>/<c>DbContext</c>/repositories/gateways/data layers: an answer that depends on what some
    /// store happened to hold, outside any workspace filter. <c>IChatClient</c>: a model deciding a finding.
    /// <c>async</c>/<c>Task</c>: not impure in itself, but nothing here has anything to await, and the day one
    /// does is the day one of the above arrived.
    /// </remarks>
    private static readonly (string Pattern, string Why)[] Forbidden =
    [
        (@"\bDateTime\b|\bDateTimeOffset\b|\bTimeProvider\b|\bIClock\b|DateOnly\.FromDateTime",
            "a clock would make the answer depend on the day it was asked"),
        (@"\bRandom\b|Guid\.NewGuid", "an answer must not change between two identical calls"),
        (@"\bHttpClient\b|\bDbContext\b|I\w+Repository\b|I\w+Gateway\b|I\w+DataLayer\b|I\w+Facade\b",
            "a read of its own would escape the workspace filter and the facade that resolves it"),
        (@"\bIChatClient\b|\bIEmbeddingGenerator\b|\bSemanticKernel\b",
            "no model may decide a deterministic finding"),
        (@"\basync\b|\bTask<|\bawait\b", "nothing here has anything to await"),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_safety_analyzer_names_nothing_impure(int index)
    {
        var relative = PureFiles[index];
        var text = File.ReadAllText(Path.Combine(DomainRoot(), relative));
        var code = WithoutCommentsOrStrings(text);

        foreach (var (pattern, why) in Forbidden)
        {
            var match = Regex.Match(code, pattern);

            Assert.False(
                match.Success,
                $"{relative} names '{match.Value}', and {why}. If this is genuinely needed, it belongs in the "
                    + "module's Business layer, which already injects the clock and the facades.");
        }
    }

    /// <summary>The scan is looking at the files it thinks it is.</summary>
    /// <remarks>
    /// Without this, a renamed or moved analyzer would make the theory above pass by reading nothing — the
    /// same failure mode <c>AiEvaluationHarnessTests</c> guards its own fixture scan against.
    /// </remarks>
    [Fact]
    public void The_scan_found_all_three_analyzers()
    {
        foreach (var relative in PureFiles)
        {
            var path = Path.Combine(DomainRoot(), relative);

            Assert.True(File.Exists(path), $"{relative} was not found; this test is checking nothing.");
            Assert.Contains("static", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Comments and string literals are stripped before the scan.
    /// </summary>
    /// <remarks>
    /// These files explain themselves at length, and the explanations say the words — "no clock", "never a
    /// model", a `DateOnly` parameter described in prose. A scan over raw text would fail on the remarks that
    /// exist to state the rule.
    /// </remarks>
    private static string WithoutCommentsOrStrings(string text)
    {
        var withoutBlockComments = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//[^\n]*", " ");
        var withoutVerbatim = Regex.Replace(withoutLineComments, @"@""(?:[^""]|"""")*""", " \"\" ");

        return Regex.Replace(withoutVerbatim, @"""(?:\\.|[^""\\])*""", " \"\" ");
    }

    private static string DomainRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CreatorPantry.Domain")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "src", "CreatorPantry.Domain");
    }
}
