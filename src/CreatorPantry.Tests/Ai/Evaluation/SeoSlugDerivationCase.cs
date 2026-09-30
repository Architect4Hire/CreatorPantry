using System.Text.Json;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture through <see cref="SeoSlug.FromTitle"/>. The slug is an identifier, so it is worked out by rule
/// and never asked of a model; a fixture here is the rule stated as an example.
/// </summary>
internal sealed class SeoSlugDerivationCase : IAiEvaluationCase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AiEvaluationKind Kind => AiEvaluationKind.SeoSlugDerivation;

    public Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(Json)
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        var slug = SeoSlug.FromTitle(input.Title, input.MaxLength ?? new SeoRules().SlugMaxLength);

        return Task.FromResult(slug == expect.Slug
            ? AiEvaluationVerdict.Pass()
            : AiEvaluationVerdict.Fail($"expected slug '{expect.Slug}', got '{slug}'."));
    }

    private sealed record Input(string? Title, int? MaxLength);

    private sealed record Expect(string Slug);
}
