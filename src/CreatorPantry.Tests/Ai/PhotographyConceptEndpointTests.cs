using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation.Results;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// IMG-001's request contract: what a client may ask for, and what it cannot reach.
/// </summary>
/// <remarks>
/// Contract and validator work, so it needs no database — the boundary through the real route is
/// <c>PhotographyConceptIsolationEndpointTests</c>, which pays for a gateway fixture because it has to.
/// </remarks>
public sealed class PhotographyConceptEndpointTests
{
    /// <summary>
    /// Six fields, and the contract's real content is what is absent: no task, no scope, no prompt, no model,
    /// no provider parameter, no concept count and no workspace, because the type has nowhere to put one.
    /// </summary>
    [Fact]
    public void The_request_carries_only_a_channel_a_recipe_pin_and_what_the_creator_described()
    {
        var fields = typeof(RequestPhotographyConceptViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "ChannelKey", "CreatorConcept", "DishName", "RecipeId", "RecipeVersionId", "SceneOverrides",
                "StyleOverrides",
            ],
            fields);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("workspace")]
    [InlineData("system")]
    [InlineData("count")]
    [InlineData("schema")]
    public void The_request_cannot_name_a_task_a_prompt_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestPhotographyConceptViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An empty request is complete: a creator may plan a shoot with nothing pinned and nothing described.
    /// </summary>
    [Fact]
    public void A_request_that_names_nothing_at_all_is_valid()
    {
        Assert.True(Validate(new RequestPhotographyConceptViewModel()).IsValid);
    }

    [Fact]
    public void A_known_channel_a_recipe_and_a_concept_are_valid_together()
    {
        var result = Validate(new RequestPhotographyConceptViewModel
        {
            ChannelKey = "instagram",
            RecipeId = Guid.NewGuid(),
            CreatorConcept = "Overhead, bright, a bit messy.",
            SceneOverrides = ["Linen cloth", "Late morning"],
            StyleOverrides = ["Warm neutrals"],
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Theory]
    [InlineData("not-a-channel")]
    [InlineData("INSTAGRAM-PLUS")]
    public void A_channel_the_product_does_not_know_is_refused(string channelKey)
    {
        var result = Validate(new RequestPhotographyConceptViewModel { ChannelKey = channelKey });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(
            RequestPhotographyConceptViewModel.ChannelKey));
    }

    /// <summary>
    /// A version without its recipe names nothing resolvable, the same rule a prompt record's pins obey.
    /// </summary>
    [Fact]
    public void A_version_without_its_recipe_is_refused()
    {
        var result = Validate(new RequestPhotographyConceptViewModel { RecipeVersionId = Guid.NewGuid() });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(
            RequestPhotographyConceptViewModel.RecipeVersionId));
    }

    [Fact]
    public void A_concept_past_the_limit_is_refused()
    {
        var result = Validate(new RequestPhotographyConceptViewModel
        {
            CreatorConcept = new string('a', AiPolicy.PhotographyCreatorConceptMaxLength + 1),
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Too_many_overrides_are_refused()
    {
        var result = Validate(new RequestPhotographyConceptViewModel
        {
            SceneOverrides = [.. Enumerable.Repeat("Linen", AiPolicy.MaxPhotographyOverrideCount + 1)],
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void An_override_past_the_limit_is_refused()
    {
        var result = Validate(new RequestPhotographyConceptViewModel
        {
            StyleOverrides = [new string('a', AiPolicy.PhotographyOverrideMaxLength + 1)],
        });

        Assert.False(result.IsValid);
    }

    /// <summary>
    /// The task needs its own route, and the catalogue says so — the generic proposal route has nowhere to put
    /// a channel, a creator concept or an override list, and the scope here is the server's to choose.
    /// </summary>
    [Fact]
    public void The_task_requires_its_own_request_contract()
    {
        Assert.True(AiTaskCatalog.RequiresTaskInputs(AiTaskType.PhotographyConcept));
        Assert.Equal(
            AiTaskType.PhotographyConcept,
            AiTaskCatalog.Resolve(AiTaskCatalog.PhotographyConcept));
    }

    private static ValidationResult Validate(RequestPhotographyConceptViewModel model) =>
        new RequestPhotographyConceptViewModelValidator(new ContentChannelCatalog()).Validate(model);
}
