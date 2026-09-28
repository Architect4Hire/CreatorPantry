using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Api;

/// <summary>
/// Every C# enum the browser decodes has a TypeScript union mirroring it, and the two have to name exactly the
/// same members.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a real defect class, not a tidiness rule.</strong> The browser decodes an unknown enum
/// member as <c>null</c>, and <c>decodeArray</c> fails the entire payload when any element fails to decode —
/// so a single member added on the server and forgotten in the union does not degrade one field. It makes the
/// whole feature undecodable: a status that never arrives, a proposal panel that renders its error state, and
/// nothing anywhere saying which member was the problem. It has happened five times.
/// </para>
/// <para>
/// <strong>The file is found by <see cref="CallerFilePathAttribute"/>, deliberately.</strong>
/// <c>BulkOperationBoundaryTests</c> discovers the repository root by walking up from the process's output
/// directory, which is why those three fail whenever a build redirects its output — and they fail as
/// assertions, which reads exactly like a real regression. A compile-time path does not move when the build
/// output does.
/// </para>
/// <para>
/// The union is parsed rather than executed. Running the TypeScript would mean a Node toolchain inside the
/// backend suite for a question that is answered perfectly well by reading the member names, and a test that
/// needs a second runtime installed is a test that gets skipped.
/// </para>
/// </remarks>
public sealed class WireEnumMirrorTests
{
    private const string ModelsPath = "src/web/src/app/models/ai-proposal.models.ts";

    /// <summary>Every enum the browser decodes, and the members the server says it has.</summary>
    private static readonly Dictionary<string, string[]> MirroredEnums = new(StringComparer.Ordinal)
    {
        ["AiOperationStatus"] = Members<AiOperationStatus>(),
        ["AiTaskType"] = Members<AiTaskType>(),
        ["AiOperationScope"] = Members<AiOperationScope>(),
        ["AiFailureCategory"] = Members<AiFailureCategory>(),
        ["AiChangeKind"] = Members<AiChangeKind>(),
        ["AiChangeTargetKind"] = Members<AiChangeTargetKind>(),
        ["AiChangeDisposition"] = Members<AiChangeDisposition>(),
        ["AiWarningKind"] = Members<AiWarningKind>(),
    };

    public static TheoryData<string> Mirrored => [.. MirroredEnums.Keys];

    /// <summary>
    /// The exported type is the contract a component writes against, so a member missing here is a member a
    /// feature cannot mention.
    /// </summary>
    [Theory]
    [MemberData(nameof(Mirrored))]
    public void The_TypeScript_union_names_every_member(string typeName)
    {
        var source = ReadModels();
        var union = Union(source, typeName);

        Assert.Equal(MirroredEnums[typeName], QuotedNames(union).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The runtime set is what <c>decodeEnum</c> actually consults, so a member present in the union and
    /// missing from the set type-checks and then fails at runtime — the worse of the two failures, because
    /// nothing catches it until a payload carrying that member arrives.
    /// </summary>
    [Theory]
    [MemberData(nameof(Mirrored))]
    public void The_runtime_value_set_names_every_member(string typeName)
    {
        var source = ReadModels();
        var values = ValueSet(source, typeName);

        Assert.Equal(MirroredEnums[typeName], QuotedNames(values).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Every enum the browser decodes is listed above.
    /// </summary>
    /// <remarks>
    /// Without this, adding a whole new mirrored enum and forgetting to register it here would leave the new
    /// union unchecked — the same drift one level up, and invisible for the same reason.
    /// </remarks>
    [Fact]
    public void Every_union_in_the_models_file_is_covered_by_a_case()
    {
        var declared = Regex
            .Matches(ReadModels(), @"^const (AI_[A-Z_]+)_VALUES\b", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var covered = MirroredEnums.Keys
            .Select(ScreamingSnake)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, covered);
    }

    private static string[] Members<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Order(StringComparer.Ordinal).ToArray();

    /// <summary>The union's body: everything between <c>export type Name =</c> and the statement's semicolon.</summary>
    private static string Union(string source, string typeName) =>
        Between(source, $"export type {typeName} =", ";", $"no 'export type {typeName}' in {ModelsPath}");

    /// <summary>The runtime set's body, which is what <c>decodeEnum</c> consults.</summary>
    private static string ValueSet(string source, string typeName) =>
        Between(
            source,
            $"const {ScreamingSnake(typeName)}_VALUES: ReadonlySet<string> = new Set<{typeName}>([",
            "]);",
            $"no value set for {typeName} in {ModelsPath}");

    private static string Between(string source, string opening, string closing, string missing)
    {
        var start = source.IndexOf(opening, StringComparison.Ordinal);

        Assert.True(start >= 0, missing);

        var from = start + opening.Length;
        var end = source.IndexOf(closing, from, StringComparison.Ordinal);

        Assert.True(end > from, $"'{opening}' is never closed by '{closing}' in {ModelsPath}");

        return source[from..end];
    }

    private static IEnumerable<string> QuotedNames(string body) =>
        Regex.Matches(body, @"'([A-Za-z]+)'").Select(match => match.Groups[1].Value);

    private static string ScreamingSnake(string typeName) =>
        Regex.Replace(typeName, "(?<!^)([A-Z])", "_$1").ToUpperInvariant();

    /// <summary>
    /// The models file with its comments removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stripped before anything is located in it, not afterwards, and both reasons are ones this test got
    /// wrong first. A doc comment above a union member carries member names in prose — "distinct from
    /// <c>NotApplicable</c>" — which would be read as phantom members. And a doc comment containing a
    /// semicolon ends the union early, so every member below it silently vanishes from the comparison: the
    /// guard would then pass while the union was missing exactly what it was written to catch.
    /// </para>
    /// <para>
    /// Safe to strip globally here because this file holds enum unions and decoders, with no string literal
    /// containing <c>//</c>; a test asserts that stays true.
    /// </para>
    /// </remarks>
    private static string ReadModels([CallerFilePath] string callerPath = "")
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(callerPath), ModelsPath));

        return Regex.Replace(source, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);
    }

    /// <summary>
    /// Nothing in the models file relies on <c>//</c> surviving, which is what makes stripping comments safe.
    /// </summary>
    /// <remarks>
    /// A URL in a string literal is the case that would break it: the strip would eat the rest of that line,
    /// and a union member below it on the same line would disappear. Checked rather than assumed, because the
    /// failure it would cause is a test that quietly passes.
    /// </remarks>
    [Fact]
    public void The_models_file_has_no_string_literal_that_stripping_comments_would_damage()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(CallerPath()), ModelsPath));
        var withoutComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotContain("://", withoutComments, StringComparison.Ordinal);
    }

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>
    /// The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Api/</c>.
    /// </summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
