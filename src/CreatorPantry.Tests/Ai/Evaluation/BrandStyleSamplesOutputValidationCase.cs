using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture straight through <see cref="AiBrandStyleSamplesOutputValidator.Validate"/> — no envelope, no
/// gateway, no database. Covers SchemaValidity and RefusalSafety for 11A.24's own document, which is not shaped
/// like a recipe diff and so cannot run through <see cref="OutputValidationCase"/>.
/// </summary>
/// <remarks>
/// One payload per fixture, because the validator sees one half of the comparison at a time and both halves are
/// held to exactly the same rules. A fixture therefore says something about both columns at once, which is the
/// property that makes the comparison honest.
/// </remarks>
internal sealed class BrandStyleSamplesOutputValidationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AiEvaluationKind Kind => AiEvaluationKind.BrandStyleSamplesOutputValidation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var result = AiBrandStyleSamplesOutputValidator.Validate(input.Payload, input.ExpectedSchemaVersion);

        return Task.FromResult(expect.Outcome switch
        {
            "Accepted" => Accepted(result, expect),

            "Rejected" => Rejected(fixture.Identity, result, expect),

            _ => throw new AiEvaluationException(
                fixture.Identity, $"'expect.outcome' is not recognised: '{expect.Outcome}'."),
        });
    }

    /// <summary>
    /// A validated answer, and then the server's own safety net over its samples.
    /// </summary>
    /// <remarks>
    /// The net runs after validation and its findings are warnings, so a fixture about it expects
    /// <c>Accepted</c> and names the codes. Exact, not "at least": a net that fired on something nobody
    /// reviewed is as much a defect as one that missed a claim, because noise is what makes a real finding
    /// easy to ignore.
    /// </remarks>
    private static AiEvaluationVerdict Accepted(
        AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument> result, Expect expect)
    {
        if (!result.Succeeded)
        {
            return AiEvaluationVerdict.Fail(
                $"expected the answer to validate, but it was rejected: {result.Failure!.ReasonCode} "
                + $"({result.Failure.Message})");
        }

        if (expect.Findings is null)
        {
            return AiEvaluationVerdict.Pass();
        }

        // The handler's own net, not a copy of it: a fixture that re-implemented the scan would keep passing
        // after the real rule changed.
        var reported = AiBrandStyleSampleCatalog.All
            .SelectMany(sample => AiEditorialClaimScanner.ScanTextForSafetyClaims(
                AiBrandStyleSampleCatalog.TextOf(result.Document!.Samples, sample)))
            .Select(finding => finding.Code)
            .ToHashSet(StringComparer.Ordinal);

        var missing = expect.Findings.Where(code => !reported.Contains(code)).ToList();

        if (missing.Count > 0)
        {
            return AiEvaluationVerdict.Fail(
                $"expected finding(s) [{string.Join(", ", missing)}], got [{string.Join(", ", reported)}].");
        }

        var unexpected = reported.Where(code => !expect.Findings.Contains(code)).ToList();

        return unexpected.Count > 0
            ? AiEvaluationVerdict.Fail($"unexpected finding(s) [{string.Join(", ", unexpected)}].")
            : AiEvaluationVerdict.Pass();
    }

    private static AiEvaluationVerdict Rejected(
        string fixtureIdentity,
        AiOutputValidationOutcome<AiBrandStyleSamplesOutputDocument> result,
        Expect expect)
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

        return expect.Category is { } category && result.Failure.Category != category
            ? AiEvaluationVerdict.Fail($"expected category '{category}', got '{result.Failure.Category}'.")
            : AiEvaluationVerdict.Pass();
    }

    private sealed record Input(string? Payload, string ExpectedSchemaVersion);

    /// <param name="Findings">
    /// The exact safety-net codes this answer must produce. Omitted means the fixture is not about the net.
    /// </param>
    private sealed record Expect(
        string Outcome, string? ReasonCode, AiFailureCategory? Category, List<string>? Findings);
}
