using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="AiEditorialPackageOutputValidator.Validate"/> and, when the answer is
/// accepted and the fixture declares the recipe's facts, through <see cref="AiEditorialClaimScanner"/> — no
/// envelope, no gateway, no database. Covers SchemaValidity, RefusalSafety and PromptInjection for RCPUB-001's
/// own document, which is prose rather than a recipe diff and so cannot run through
/// <see cref="OutputValidationCase"/>.
/// </summary>
internal sealed class EditorialPackageOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.EditorialPackageOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var result = AiEditorialPackageOutputValidator.Validate(input.Payload, input.ExpectedSchemaVersion);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => result.Succeeded
                ? Findings(fixture.Identity, result.Document!, input, expect)
                : AiEvaluationVerdict.Fail(
                    $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                    + $"({result.Failure.Message})"),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    private static AiEvaluationVerdict Findings(
        string fixtureIdentity, AiEditorialPackageOutputDocument document, Input input, Expect expect)
    {
        if (expect.Warnings is null)
        {
            return AiEvaluationVerdict.Pass();
        }

        if (input.Source is null)
        {
            throw new AiEvaluationException(
                fixtureIdentity, "'input.source' is required when 'expect.warnings' is declared.");
        }

        var facts = AiEditorialSourceFacts.Of(input.Source.Numbers ?? [], input.Source.FreeText, input.Source.StorageNotes);
        var found = AiEditorialClaimScanner.Scan(document.Sections, facts)
            .Select(finding => finding.Code)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        var wanted = expect.Warnings.Distinct().Order(StringComparer.Ordinal).ToArray();

        return found.SequenceEqual(wanted)
            ? AiEvaluationVerdict.Pass()
            : AiEvaluationVerdict.Fail(
                $"expected the scanner to report [{string.Join(", ", wanted)}], got [{string.Join(", ", found)}].");
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity, AiOutputValidationOutcome<AiEditorialPackageOutputDocument> result, Expect expect)
    {
        if (expect.ReasonCode is null)
        {
            throw new AiEvaluationException(
                fixtureIdentity, "'expect.reasonCode' is required when 'expect.outcome' is 'Rejected'.");
        }

        if (result.Succeeded)
        {
            return AiEvaluationVerdict.Fail("expected the answer to be rejected, but it validated.");
        }

        if (result.Failure!.ReasonCode != expect.ReasonCode)
        {
            return AiEvaluationVerdict.Fail(
                $"expected reason '{expect.ReasonCode}', got '{result.Failure.ReasonCode}' ({result.Failure.Message}).");
        }

        if (expect.Category is { } category && result.Failure.Category != category)
        {
            return AiEvaluationVerdict.Fail($"expected category '{category}', got '{result.Failure.Category}'.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private sealed record Input(string? Payload, string ExpectedSchemaVersion, SourceFacts? Source);

    private sealed record SourceFacts(string[]? Numbers, string? FreeText, string? StorageNotes);

    private sealed record Expect(string Outcome, string? ReasonCode, AiFailureCategory? Category, string[]? Warnings);
}
