using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.24's document, schema and catalogue — the parts the evaluation fixtures cannot reach, because a fixture
/// asserts what one payload becomes rather than what the contract offers.
/// </summary>
public sealed class AiBrandStyleSamplesOutputTests
{
    private const string SchemaVersion = "brand.style-test-drive.v1";

    /// <summary>
    /// The schema the model is shown has the three samples and nowhere to claim it followed a rule. Which
    /// guidance applied is the server's answer, from the package it assembled; a field here would invite an
    /// unfalsifiable claim about the model's own writing.
    /// </summary>
    [Fact]
    public void The_schema_offers_three_samples_and_no_place_to_claim_a_rule()
    {
        var schema = AiBrandStyleSamplesOutputSchema.Json;

        Assert.Contains("blogIntro", schema, StringComparison.Ordinal);
        Assert.Contains("socialCaption", schema, StringComparison.Ordinal);
        Assert.Contains("imagePrompt", schema, StringComparison.Ordinal);

        Assert.DoesNotContain("appliedRule", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("citation", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guide", schema, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Each sample's name is what reaches <c>AiStructuredChange.FieldName</c> and the API, so it has to survive
    /// the round trip — and each has a ceiling above the shared floor, or a valid answer would be impossible.
    /// </summary>
    [Fact]
    public void Every_sample_has_a_name_that_round_trips_and_a_limit_above_the_floor()
    {
        Assert.Equal(3, AiBrandStyleSampleCatalog.All.Count);

        foreach (var sample in AiBrandStyleSampleCatalog.All)
        {
            var wire = AiBrandStyleSampleCatalog.ToWire(sample);

            Assert.Equal(sample, AiBrandStyleSampleCatalog.Parse(wire));
            Assert.Equal(char.ToLowerInvariant(wire[0]), wire[0]);
            Assert.True(
                AiBrandStyleSampleCatalog.MaxLengthOf(sample) > AiPolicy.StyleSampleMinLength,
                $"{wire} has no room between the floor and its ceiling.");
        }
    }

    /// <summary>"Not declared" is not a fourth sample, and nothing may address a row to it.</summary>
    [Fact]
    public void The_undeclared_member_is_not_one_of_the_samples()
    {
        Assert.DoesNotContain(AiBrandStyleSample.Unspecified, AiBrandStyleSampleCatalog.All);
        Assert.Null(AiBrandStyleSampleCatalog.Parse("unspecified"));
        Assert.Null(AiBrandStyleSampleCatalog.Parse(null));
        Assert.Null(AiBrandStyleSampleCatalog.Parse(" "));
    }

    /// <summary>
    /// Both halves of the comparison go through the gateway as a delegate, so the delegate and the method have
    /// to agree — a looser delegate would be a second, unreviewed set of rules on the path that is actually used.
    /// </summary>
    [Fact]
    public void The_delegate_validates_exactly_as_the_method_does()
    {
        var payload = """
            {"schemaVersion":"brand.style-test-drive.v1","samples":{
            "blogIntro":{"text":"There is a kind of weeknight when the oven is too much and the pan has to do all of it."},
            "socialCaption":{"text":"One pan, one lemon, and a dinner that asks nothing else of you."},
            "imagePrompt":{"text":"Overhead shot of a cast-iron pan of lemon chicken on weathered oak, soft light."}}}
            """.ReplaceLineEndings(string.Empty);

        var direct = AiBrandStyleSamplesOutputValidator.Validate(payload, SchemaVersion);
        var viaDelegate = AiBrandStyleSamplesOutputValidator.AsDelegate()(
            payload, SchemaVersion, AiOperationScope.NotApplicable);

        Assert.True(direct.Succeeded, direct.Failure?.Message);
        Assert.True(viaDelegate.Succeeded, viaDelegate.Failure?.Message);

        var rejectedDirectly = AiBrandStyleSamplesOutputValidator.Validate("{}", SchemaVersion);
        var rejectedViaDelegate = AiBrandStyleSamplesOutputValidator.AsDelegate()(
            "{}", SchemaVersion, AiOperationScope.NotApplicable);

        Assert.Equal(rejectedDirectly.Failure!.ReasonCode, rejectedViaDelegate.Failure!.ReasonCode);
    }

    /// <summary>
    /// The scope the delegate is handed is deliberately unused: a test drive's scope is
    /// <see cref="AiOperationScope.NotApplicable"/>, and an answer whose verdict moved with the scope would be
    /// a sample judged by rules about changing a recipe.
    /// </summary>
    [Fact]
    public void The_verdict_does_not_depend_on_the_scope()
    {
        var verdicts = Enum.GetValues<AiOperationScope>()
            .Select(scope => AiBrandStyleSamplesOutputValidator.AsDelegate()("{}", SchemaVersion, scope))
            .Select(outcome => outcome.Failure!.ReasonCode)
            .Distinct()
            .ToList();

        Assert.Single(verdicts);
    }
}
