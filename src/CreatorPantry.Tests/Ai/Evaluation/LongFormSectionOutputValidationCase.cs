using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs LF.3.1's validator over one answer and then, for a body that validates, the same measuring and claim
/// scanning the handler runs before it stores anything.
/// </summary>
/// <remarks>
/// <para>
/// Two stages in one kind, for the reason <see cref="AiEvaluationKind.LongFormOutlineOutputValidation"/> gives:
/// a second section, a heading line or a link is <em>refused</em>, while a recomputed figure, a safety claim or
/// a body over the ceiling is <em>flagged</em> beside the section and never refused.
/// </para>
/// <para>
/// The ceiling is the content module's real one, reached through its real facade.
/// </para>
/// <para>
/// <strong>What it cannot show</strong> is whether a body is any good: on the subject, in the creator's voice,
/// faithful to accepted copy it was told to work from. Every fixture here is an answer written by hand.
/// </para>
/// </remarks>
internal sealed class LongFormSectionOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // The content module's own registration of its shape rules: stateless singletons, so resolved once.
    private static readonly ILongFormPlanFacade Plans = new ServiceCollection()
        .AddContentModule()
        .BuildServiceProvider()
        .GetRequiredService<ILongFormPlanFacade>();

    public AiEvaluationKind Kind => AiEvaluationKind.LongFormSectionOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        if (input.Role is not { } role)
        {
            throw new AiEvaluationException(fixture.Identity, "'input.role' is required.");
        }

        // A fixture may ask for a body of a given length rather than carry thousands of characters of filler.
        var payload = input.RepeatBody is { } repeat
            ? JsonSerializer.Serialize(new
            {
                schemaVersion = input.ExpectedSchemaVersion,
                body = string.Concat(Enumerable.Repeat(repeat.Text, repeat.Times)),
            })
            : input.Payload;

        var result = AiLongFormSectionOutputValidator.Validate(payload, input.ExpectedSchemaVersion);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? Accepted(result.Document!, input, role, expect)
                : AiEvaluationVerdict.Fail(
                    $"expected the body to validate, but it was rejected: {result.Failure!.ReasonCode} "
                    + $"({result.Failure.Message})"),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Accepted(
        AiLongFormSectionOutputDocument document, Input input, LongFormSectionRole role, Expect expect)
    {
        var package = Package(input);
        var targetId = Guid.NewGuid();

        // The section under test last, after whatever the fixture says is already written.
        var earlier = (input.EarlierOpenings ?? [])
            .Select(opening => new LongFormWritingSectionServiceModel(
                Guid.NewGuid(), LongFormSectionRole.Body, "An earlier section", "What it covers.", null, opening))
            .ToList();

        var writing = new LongFormWritingContextServiceModel(
            package.WorkspaceId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            input.Kind ?? LongFormKind.BlogArticle,
            RevisionNumber: 3,
            TargetIndex: earlier.Count,
            CurrentBody: null,
            [.. earlier, new LongFormWritingSectionServiceModel(targetId, role, "The section", "What it is for.", null, null)],
            Pins: [],
            new LongFormWordRange(900, 1600),
            Plans.Measure(string.Empty).MaxCharacters);

        var (changes, warnings) = LongFormSectionAiTaskHandler.Translate(
            document, writing, package, Plans.Measure(document.Body), input.Carried ?? []);

        // What the content module's scanner finds when this body is written into the draft (LF.3.2), against
        // the fixture's recipes as the pinned sources. Placed where the server's findings go: ahead of the
        // model's own warnings.
        var found = LongFormClaimScanner.Scan(
            new AiClaimScanFacade(),
            role,

            // An item is held to the one recipe it is bound to, as it is when its body lands on the draft.
            sourceReferenceId: input.ItemRecipe is { } position ? package.Recipes[position - 1].ReferenceId : null,
            document.Body,
            [.. package.Recipes.Select(recipe => new LongFormClaimRecipe(recipe.ReferenceId, recipe.Title, FactLines(recipe)))]);

        var firstOfTheModels = warnings.FindIndex(warning => warning.Message.StartsWith(AiPolicy.ModelWarningLabel, StringComparison.Ordinal));

        warnings.InsertRange(
            firstOfTheModels < 0 ? warnings.Count : firstOfTheModels,
            found.Select(finding => new AiOutputWarning
            {
                Kind = AiWarningKind.UnverifiedClaim,
                Message = $"[{finding.Code}] {finding.Message}",
                ChangeIndex = 0,
            }));

        // One target, and it is the section asked for: a body has nowhere else to go.
        if (changes.Any(change =>
                change.TargetId != targetId || change.TargetKind is not AiChangeTargetKind.LongFormSectionBody))
        {
            return AiEvaluationVerdict.Fail("a row was stored against something other than the section asked for.");
        }

        var fields = changes.ToDictionary(change => change.FieldName!, change => change.AfterValue!);

        // Never trimmed, shortened or re-worded, whatever the fixture is about — and never over a row's length.
        if (!string.Equals(LongFormSectionFields.Join(fields), document.Body, StringComparison.Ordinal))
        {
            return AiEvaluationVerdict.Fail("the body was not stored as written.");
        }

        if (fields.Values.Any(value => value.Length > AiPolicy.ChangeValueMaxLength))
        {
            return AiEvaluationVerdict.Fail("a part of the body is longer than one row holds.");
        }

        if (fields.GetValueOrDefault(LongFormSectionFields.BasedOnRevision) != "3")
        {
            return AiEvaluationVerdict.Fail("the revision the body was written against was not stored.");
        }

        foreach (var phrase in expect.Flagged ?? [])
        {
            if (!warnings.Any(warning => warning.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            {
                return AiEvaluationVerdict.Fail(
                    $"expected a finding containing \"{phrase}\", but found: "
                    + (warnings.Count == 0 ? "none" : string.Join(" | ", warnings.Select(warning => warning.Message))));
            }
        }

        foreach (var phrase in expect.NotFlagged ?? [])
        {
            if (warnings.FirstOrDefault(warning =>
                    warning.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) is { } unexpected)
            {
                return AiEvaluationVerdict.Fail(
                    $"expected nothing to be flagged about \"{phrase}\", but found: {unexpected.Message}");
            }
        }

        if (expect.NoFindings is true && warnings.Count > 0)
        {
            return AiEvaluationVerdict.Fail(
                "expected no findings, but found: " + string.Join(" | ", warnings.Select(warning => warning.Message)));
        }

        // A server finding is never behind a model's warning, and a model's warning is always labelled as one.
        var firstModel = warnings.FindIndex(warning => warning.Message.StartsWith(AiPolicy.ModelWarningLabel, StringComparison.Ordinal));

        if (firstModel >= 0 && warnings.Skip(firstModel).Any(warning =>
                !warning.Message.StartsWith(AiPolicy.ModelWarningLabel, StringComparison.Ordinal)))
        {
            return AiEvaluationVerdict.Fail("a server finding was stored behind one of the model's own warnings.");
        }

        return AiEvaluationVerdict.Pass();
    }

    /// <summary>A recipe's lines as the content module reads a pinned version's: every line that states a figure.</summary>
    private static List<string> FactLines(CreativeContextRecipeEntry recipe)
    {
        var lines = new List<string>();
        lines.AddRange(recipe.Ingredients);
        lines.AddRange(recipe.Steps);

        if (recipe.YieldText is { } yield)
        {
            lines.Add(yield);
        }

        foreach (var minutes in new[] { recipe.PrepTimeMinutes, recipe.CookTimeMinutes, recipe.RestTimeMinutes, recipe.TotalTimeMinutes })
        {
            if (minutes is { } value)
            {
                lines.Add($"{value} minutes");
            }
        }

        return lines;
    }

    private static CreativeContextPackage Package(Input input) => new(
        Guid.NewGuid(),
        AiTaskType.LongFormSection,
        Guid.NewGuid(),
        ContextVersion: null,
        new CreativeContextWords(input.WorkingTitle, PictureBrief: null),
        Channels: [],
        Day: null,
        [
            .. (input.Recipes ?? []).Select(recipe => new CreativeContextRecipeEntry(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                recipe.Title,
                recipe.YieldText,
                recipe.PrepTimeMinutes,
                recipe.CookTimeMinutes,
                RestTimeMinutes: null,
                TotalTimeMinutes: null,
                recipe.Ingredients ?? [],
                recipe.Steps ?? [],
                0,
                0)),
        ],
        Concepts: [],
        Pictures: [],
        Prompts: [],
        Dropped: [],
        Omissions: [],
        EstimatedTokens: 0,
        Checksum: "sha256:fixture",
        AssembledAt: DateTimeOffset.UnixEpoch);

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiLongFormSectionOutputDocument> result, Expect expect)
    {
        if (expect.ReasonCode is null)
        {
            throw new AiEvaluationException(
                fixtureIdentity, "'expect.reasonCode' is required when 'expect.outcome' is 'Rejected'.");
        }

        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the body to be rejected, but it validated.");
        }

        if (result.Failure!.ReasonCode != expect.ReasonCode)
        {
            return AiEvaluationVerdict.Fail(
                $"expected reason '{expect.ReasonCode}', got '{result.Failure.ReasonCode}' "
                + $"({result.Failure.Message}).");
        }

        if (expect.Category is { } category && result.Failure.Category != category)
        {
            return AiEvaluationVerdict.Fail(
                $"expected category '{category}', got '{result.Failure.Category}'.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private sealed record Input(
        string? Payload,
        string ExpectedSchemaVersion,
        LongFormSectionRole? Role,
        LongFormKind? Kind = null,
        string? WorkingTitle = null,
        IReadOnlyList<RecipeInput>? Recipes = null,
        int? ItemRecipe = null,
        IReadOnlyList<string>? EarlierOpenings = null,
        IReadOnlyList<string>? Carried = null,
        RepeatInput? RepeatBody = null);

    private sealed record RepeatInput(string Text, int Times);

    private sealed record RecipeInput(
        string Title,
        string? YieldText = null,
        int? PrepTimeMinutes = null,
        int? CookTimeMinutes = null,
        IReadOnlyList<string>? Ingredients = null,
        IReadOnlyList<string>? Steps = null);

    private sealed record Expect(
        string Outcome,
        string? ReasonCode = null,
        AiFailureCategory? Category = null,
        IReadOnlyList<string>? Flagged = null,
        IReadOnlyList<string>? NotFlagged = null,
        bool? NoFindings = null);
}
