using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The promises IMG-002's document makes about its own shape, and the one rule it deliberately does not carry.
/// </summary>
public sealed class AiImagePromptOutputTests
{
    /// <summary>
    /// No field for a rendering setting, which is "no rendering" made structural rather than checked.
    /// </summary>
    [Fact]
    public void No_type_in_the_document_has_a_field_for_a_rendering_setting()
    {
        Type[] types = [typeof(AiImagePromptOutputDocument), typeof(AiImagePromptOutputWarning)];

        var offenders = types
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .Where(name => new[]
                {
                    "Seed", "Steps", "Sampler", "Guidance", "Denoise", "Model", "Provider",
                    "Width", "Height", "Dimension", "Resolution", "AspectRatio", "Render",
                }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_type_in_the_document_has_a_field_for_a_url_or_an_owner()
    {
        var offenders = typeof(AiImagePromptOutputDocument).GetProperties()
            .Select(property => property.Name)
            .Where(name => new[] { "Url", "Uri", "Path", "Workspace", "Membership", "Asset", "Image" }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A composed prompt can never become a recipe change, even when composed against a pinned version.
    /// </summary>
    [Fact]
    public void A_composed_prompt_can_never_become_a_recipe_change()
    {
        Assert.DoesNotContain(AiChangeTargetKind.ImagePrompt, AiChangeApplicability.Targets);
        Assert.Empty(AiChangeApplicability.For(AiChangeTargetKind.ImagePrompt));
        Assert.Null(AiChangeTargetPolicy.For(AiChangeTargetKind.ImagePrompt));

        foreach (var kind in Enum.GetValues<AiChangeKind>())
        {
            Assert.False(AiChangeApplicability.IsApplicable(kind, AiChangeTargetKind.ImagePrompt));
        }
    }

    /// <summary>
    /// A prompt may carry numbers where a photography concept may not, and that asymmetry is deliberate.
    /// </summary>
    /// <remarks>
    /// A concept is a shoot plan, so any figure in it would be invented; a prompt is the text an image model
    /// reads, where "4:5" and "three loaves" are ordinary. Asserted rather than left implicit because the two
    /// validators sit side by side and the obvious tidy-up is to make them agree.
    /// </remarks>
    [Fact]
    public void A_prompt_may_carry_a_numeral_where_a_concept_may_not()
    {
        const string schema = "image.prompt.v1";
        var prompt = "Overhead photograph of three soda bread loaves on pale oak, framed to a 4:5 crop, soft "
            + "window light from the left, one torn edge showing the crumb.";

        var accepted = AiImagePromptOutputValidator.Validate(
            $"{{\"schemaVersion\":\"{schema}\",\"prompt\":\"{prompt}\",\"avoid\":[]}}", schema);

        Assert.True(accepted.Succeeded, accepted.Failure?.ReasonCode);

        // The same words as a photography concept's styling note would be refused outright.
        var concept = AiPhotographyConceptOutputValidator.Validate(
            "{\"schemaVersion\":\"image.photography-concept.v1\",\"concepts\":[{\"label\":\"L\",\"mood\":\"M\","
            + "\"palette\":\"P\",\"rationale\":\"R\",\"shots\":[{\"kind\":\"Hero\",\"framing\":\"F\","
            + "\"lighting\":\"G\",\"surface\":\"S\",\"styling\":\"Three loaves, 4 minutes apart\","
            + "\"props\":[]}]}]}",
            "image.photography-concept.v1");

        Assert.False(concept.Succeeded);
        Assert.Equal(AiOutputReason.PhotographyNumeralNotPermitted, concept.Failure!.ReasonCode);
    }

    [Fact]
    public void The_exported_schema_describes_the_prompt_and_its_avoid_list()
    {
        var schema = AiImagePromptOutputSchema.Json;

        foreach (var expected in new[] { "prompt", "avoid", "warnings" })
        {
            Assert.Contains(expected, schema, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("seed", schema, StringComparison.OrdinalIgnoreCase);
    }
}
