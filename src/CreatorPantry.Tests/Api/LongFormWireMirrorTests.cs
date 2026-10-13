using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Api;

/// <summary>
/// The browser's long-form models name the same enum members, the same role table and the same two limits as
/// the server (LF.2.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The enums are the defect class <see cref="WireEnumMirrorTests"/> describes.</strong> A member the
/// server sends and the union lacks does not degrade one field: the draft fails to decode, and the plan
/// screen shows its error state with nothing saying which member was the problem.
/// </para>
/// <para>
/// <strong>The role table is a different risk, and a quieter one.</strong> The screen uses it only to offer
/// the roles a plan still has room for — the server remains the judge of every plan sent — so drift would
/// never produce a wrong write. It would produce a screen that offers a section the server then refuses, or
/// withholds one it would have accepted, and neither looks like a bug to the person it happens to.
/// </para>
/// <para>
/// The file is found by <see cref="CallerFilePathAttribute"/> and parsed, not executed, for the reasons
/// <see cref="WireEnumMirrorTests"/> gives.
/// </para>
/// </remarks>
public sealed class LongFormWireMirrorTests
{
    private const string ModelsPath = "src/web/src/app/models/long-form-draft.models.ts";

    private static readonly Dictionary<string, string[]> MirroredEnums = new(StringComparer.Ordinal)
    {
        ["LongFormKind"] = Members<LongFormKind>(),
        ["LongFormDraftStatus"] = Members<LongFormDraftStatus>(),
        ["LongFormSectionRole"] = Members<LongFormSectionRole>(),
        // The wire name differs from the server's on purpose: the browser only ever meets this enum on a
        // long-form revision.
        ["LongFormRevisionSource"] = Members<ContentRevisionSource>(),
        ["LongFormWordTargetStatus"] = Members<LongFormWordTargetStatus>(),
        ["LongFormAcceptanceIssue"] = Members<LongFormAcceptanceIssue>(),
        ["LongFormSectionRequestOutcome"] = Members<LongFormSectionRequestOutcome>(),
        ["LongFormFindingKind"] = Members<LongFormFindingKind>(),
        ["LongFormItemSourceState"] = Members<LongFormItemSourceState>(),
    };

    public static TheoryData<string> Mirrored => [.. MirroredEnums.Keys];

    public static TheoryData<LongFormKind> Kinds => [.. Enum.GetValues<LongFormKind>()];

    [Theory]
    [MemberData(nameof(Mirrored))]
    public void The_TypeScript_union_names_every_member(string typeName)
    {
        var union = Between(ReadModels(), $"export type {typeName} =", ";");

        Assert.Equal(MirroredEnums[typeName], QuotedNames(union).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>The runtime set is what <c>decodeEnum</c> consults; a member only in the union fails at runtime.</summary>
    [Theory]
    [MemberData(nameof(Mirrored))]
    public void The_runtime_value_set_names_every_member(string typeName)
    {
        var values = Between(
            ReadModels(),
            $"const {ScreamingSnake(typeName)}_VALUES: ReadonlySet<string> = new Set<{typeName}>([",
            "]);");

        Assert.Equal(MirroredEnums[typeName], QuotedNames(values).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>A whole new value set added to the file and not registered above would go unchecked.</summary>
    [Fact]
    public void Every_value_set_in_the_models_file_is_covered_by_a_case()
    {
        var declared = Regex
            .Matches(ReadModels(), @"^(?:export )?const ([A-Z_]+)_VALUES\b", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(MirroredEnums.Keys.Select(ScreamingSnake).Order(StringComparer.Ordinal).ToArray(), declared);
    }

    /// <summary>Role by role, limit by limit, and in the policy's own order — the order the screen offers them in.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_role_table_matches_the_policy(LongFormKind kind)
    {
        var table = Between(ReadModels(), "export const LONG_FORM_ROLE_RULES", "};");
        var rules = Between(table, $"{kind}: [", "]");

        var mirrored = Regex
            .Matches(rules, @"\{\s*role:\s*'([A-Za-z]+)',\s*max:\s*(null|\d+)\s*\}")
            .Select(match => (Role: match.Groups[1].Value, Max: match.Groups[2].Value))
            .ToArray();

        var expected = LongFormPolicy.RolesFor(kind)
            .Select(rule => (Role: rule.Role.ToString(), Max: rule.MaxOccurrences?.ToString() ?? "null"))
            .ToArray();

        Assert.Equal(expected, mirrored);
    }

    /// <summary>
    /// Every entry in the table was read by the test above.
    /// </summary>
    /// <remarks>
    /// The entries are matched by pattern, so one written in a shape the pattern does not see would simply be
    /// absent from the comparison — and a table missing an entry on both sides compares equal. Counting the
    /// <c>role:</c> keys independently is what makes a reformatted entry a failure rather than a blind spot.
    /// </remarks>
    [Fact]
    public void Every_entry_in_the_role_table_is_in_the_shape_the_comparison_reads()
    {
        var table = Between(ReadModels(), "export const LONG_FORM_ROLE_RULES", "};");

        var keys = Regex.Matches(table, @"\brole:").Count;
        var read = Regex.Matches(table, @"\{\s*role:\s*'([A-Za-z]+)',\s*max:\s*(null|\d+)\s*\}").Count;
        var expected = Enum.GetValues<LongFormKind>().Sum(kind => LongFormPolicy.RolesFor(kind).Count);

        Assert.Equal(keys, read);
        Assert.Equal(expected, read);
    }

    [Theory]
    [InlineData("LONG_FORM_HEADING_MAX_CHARACTERS", LongFormPolicy.HeadingMaxCharacters)]
    [InlineData("LONG_FORM_INTENT_MAX_CHARACTERS", LongFormPolicy.IntentMaxCharacters)]
    public void The_limits_the_screen_states_are_the_policys(string constant, int expected)
    {
        var match = Regex.Match(ReadModels(), $@"export const {constant} = (\d+);");

        Assert.True(match.Success, $"no 'export const {constant}' in {ModelsPath}");
        Assert.Equal(expected, int.Parse(match.Groups[1].Value));
    }

    /// <summary>Nothing in the file relies on <c>//</c> surviving, which is what makes stripping comments safe.</summary>
    [Fact]
    public void The_models_file_has_no_string_a_comment_strip_could_eat()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(CallerPath()), ModelsPath));
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotContain("://", withoutBlockComments, StringComparison.Ordinal);
    }

    private static string[] Members<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Order(StringComparer.Ordinal).ToArray();

    private static string Between(string source, string opening, string closing)
    {
        var start = source.IndexOf(opening, StringComparison.Ordinal);

        Assert.True(start >= 0, $"no '{opening}' in {ModelsPath}");

        var from = start + opening.Length;
        var end = source.IndexOf(closing, from, StringComparison.Ordinal);

        Assert.True(end > from, $"'{opening}' is never closed by '{closing}' in {ModelsPath}");

        return source[from..end];
    }

    private static IEnumerable<string> QuotedNames(string body) =>
        Regex.Matches(body, @"'([A-Za-z]+)'").Select(match => match.Groups[1].Value);

    private static string ScreamingSnake(string typeName) =>
        Regex.Replace(typeName, "(?<!^)([A-Z])", "_$1").ToUpperInvariant();

    /// <summary>The models file with its comments removed, so prose naming a member is never read as one.</summary>
    private static string ReadModels()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(CallerPath()), ModelsPath));

        return Regex.Replace(source, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);
    }

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Api/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
