using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation.Results;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// IMG-002's request contract: what a client may ask for, and what it cannot reach.
/// </summary>
public sealed class ImagePromptEndpointTests
{
    [Fact]
    public void The_request_carries_the_concept_the_shot_and_what_refines_them()
    {
        var fields = typeof(RequestImagePromptViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "BriefDocumentId", "ChannelKey", "ConceptId", "ConceptRequestId", "DishName",
                "RecipeId", "RecipeVersionId", "SceneOverrides", "ShotKind", "StyleOverrides",
            ],
            fields);
    }

    /// <summary>
    /// The contract cannot carry a rendering decision, which is IMG-002's RESTRICTION on the way in.
    /// </summary>
    [Theory]
    [InlineData("seed")]
    [InlineData("steps")]
    [InlineData("sampler")]
    [InlineData("guidance")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("workspace")]
    [InlineData("schema")]
    public void The_request_cannot_name_a_rendering_setting_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestImagePromptViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A `prompt` field would make the request the answer, which is the one thing it must not be.
    /// </summary>
    [Fact]
    public void The_request_cannot_carry_a_prompt_of_its_own()
    {
        Assert.DoesNotContain(
            typeof(RequestImagePromptViewModel).GetProperties(),
            property => property.Name.Equals("Prompt", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("Avoid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_concept_and_a_shot_are_enough()
    {
        var result = Validate(Minimal());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Fact]
    public void A_request_with_no_concept_is_refused()
    {
        var model = Minimal();
        model.ConceptRequestId = Guid.Empty;

        var result = Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "ConceptRequestId");
    }

    [Fact]
    public void A_request_with_no_concept_id_is_refused()
    {
        var model = Minimal();
        model.ConceptId = Guid.Empty;

        var result = Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "ConceptId");
    }

    /// <summary>
    /// A concept may plan three shots, so which one to compose for is not something the server may guess.
    /// </summary>
    [Fact]
    public void A_request_with_no_shot_is_refused()
    {
        var model = Minimal();
        model.ShotKind = null;

        var result = Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "ShotKind");
    }

    [Fact]
    public void A_channel_the_product_does_not_know_is_refused()
    {
        var model = Minimal();
        model.ChannelKey = "not-a-channel";

        var result = Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "ChannelKey");
    }

    [Fact]
    public void A_version_without_its_recipe_is_refused()
    {
        var model = Minimal();
        model.RecipeVersionId = Guid.NewGuid();

        var result = Validate(model);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "RecipeVersionId");
    }

    [Fact]
    public void The_task_requires_its_own_request_contract()
    {
        Assert.True(AiTaskCatalog.RequiresTaskInputs(AiTaskType.ImagePrompt));
        Assert.Equal(AiTaskType.ImagePrompt, AiTaskCatalog.Resolve(AiTaskCatalog.ImagePrompt));
    }

    private static RequestImagePromptViewModel Minimal() => new()
    {
        ConceptRequestId = Guid.NewGuid(),
        ConceptId = Guid.NewGuid(),
        ShotKind = AiPhotographyShotKind.Hero,
    };

    private static ValidationResult Validate(RequestImagePromptViewModel model) =>
        new RequestImagePromptViewModelValidator(new ContentChannelCatalog()).Validate(model);
}
