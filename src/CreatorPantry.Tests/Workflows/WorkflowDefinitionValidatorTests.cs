using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Codes = CreatorPantry.Domain.Modules.Workflows.Managers.WorkflowDefinitionErrorCodes;

namespace CreatorPantry.Tests.Workflows;

public sealed class WorkflowDefinitionValidatorTests
{
    private static readonly WorkflowDefinition Example = WorkflowDefinitionFixtures.CreateRecipe;

    [Fact]
    public void The_approved_example_is_valid()
    {
        Assert.Empty(WorkflowDefinitionValidator.Validate(Example));
    }

    [Fact]
    public void The_capability_registry_is_valid()
    {
        Assert.Empty(WorkflowDefinitionValidator.ValidateCapabilities(WorkflowCapabilities.All));
    }

    [Fact]
    public void Every_defect_is_reported_not_only_the_first()
    {
        var broken = Example with { Version = 0, Goal = " ", Steps = [] };

        Assert.Equal(
            [Codes.VersionInvalid, Codes.CopyMissing, Codes.StepsEmpty],
            WorkflowDefinitionValidator.Validate(broken).Select(error => error.Code));
    }

    // ---- Identity ----

    [Theory]
    [InlineData("")]
    [InlineData("recipe")]
    [InlineData("Recipe.create")]
    [InlineData("recipe..create")]
    [InlineData("recipe.create-")]
    [InlineData("recipe.-create")]
    [InlineData("recipe_create.x")]
    [InlineData("1recipe.create")]
    [InlineData("recipe.create.")]
    public void A_key_is_lowercase_dotted_words(string key)
    {
        AssertOnly(Example with { Key = key }, Codes.KeyInvalid);
    }

    [Fact]
    public void A_key_has_a_length_limit()
    {
        AssertOnly(Example with { Key = "recipe." + new string('a', 58) }, Codes.KeyInvalid);
        Assert.Empty(WorkflowDefinitionValidator.Validate(Example with { Key = "recipe." + new string('a', 57) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_version_starts_at_one(int version)
    {
        AssertOnly(Example with { Version = version }, Codes.VersionInvalid);
    }

    // ---- Copy ----

    [Fact]
    public void Copy_may_not_be_blank()
    {
        AssertOnly(Example with { Description = "" }, Codes.CopyMissing);
        AssertOnly(Example.WithStep("method", step => step with { Help = "   " }), Codes.CopyMissing);
    }

    [Fact]
    public void Copy_has_a_length_limit()
    {
        AssertOnly(Example with { Goal = new string('a', WorkflowDefinitionValidator.MaxGoalLength + 1) }, Codes.CopyTooLong);
        Assert.Empty(WorkflowDefinitionValidator.Validate(
            Example with { Goal = new string('a', WorkflowDefinitionValidator.MaxGoalLength) }));
    }

    [Theory]
    [InlineData("Pick a prompt for your recipe")]
    [InlineData("We send it to the AI model")]
    [InlineData("Paste your JSON here")]
    [InlineData("Your recipe moves through our STATE MACHINE")]
    [InlineData("Uses about 400 tokens")]
    public void Copy_never_needs_the_creator_to_know_how_the_product_works_inside(string help)
    {
        AssertOnly(Example.WithStep("method", step => step with { Help = help }), Codes.CopyJargon);
    }

    [Theory]
    [InlineData("Remodel the menu for spring")]
    [InlineData("Apiary honey cake")]
    [InlineData("A tokenless guide")]
    public void A_word_that_merely_contains_jargon_is_not_jargon(string help)
    {
        Assert.Empty(WorkflowDefinitionValidator.Validate(Example.WithStep("method", step => step with { Help = help })));
    }

    // ---- Steps ----

    [Fact]
    public void A_workflow_has_steps()
    {
        AssertOnly(Example with { Steps = [] }, Codes.StepsEmpty);
    }

    [Fact]
    public void A_workflow_has_at_least_one_required_step()
    {
        var allOptional = Example with
        {
            Steps = [Example.Steps[0] with { Requirement = WorkflowStepRequirement.Optional }],
        };

        AssertOnly(allOptional, Codes.StepsNoneRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ingredients")]
    [InlineData("ingredients.list")]
    [InlineData("yield_and_time")]
    public void A_step_key_is_one_lowercase_segment(string key)
    {
        AssertOnly(Example.WithStep("try-sizes", step => step with { Key = key }), Codes.StepKeyInvalid);
    }

    [Fact]
    public void Step_keys_are_unique()
    {
        AssertOnly(Example.WithStep("try-sizes", step => step with { Key = "method" }), Codes.StepKeyDuplicate);
    }

    [Fact]
    public void A_step_is_required_or_optional()
    {
        AssertOnly(
            Example.WithStep("try-sizes", step => step with { Requirement = (WorkflowStepRequirement)0 }),
            Codes.StepRequirementInvalid);
    }

    // ---- Prerequisites ----

    [Fact]
    public void A_workflow_cannot_wait_for_its_own_step_before_it_starts()
    {
        AssertOnly(
            Example with { Prerequisites = [new WorkflowPrerequisite.StepCompleted("start")] },
            Codes.PrerequisiteNotAllowed);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("try-sizes")]
    [InlineData("nowhere")]
    public void A_step_waits_only_for_an_earlier_step(string waitsFor)
    {
        AssertOnly(
            Example.WithStep("method", step => step with { Prerequisites = [new WorkflowPrerequisite.StepCompleted(waitsFor)] }),
            Codes.PrerequisiteStepNotEarlier);
    }

    [Fact]
    public void A_resource_kind_is_one_the_contract_knows()
    {
        AssertOnly(
            Example.WithStep("start", step => step with
            {
                CompletionRule = new WorkflowCompletionRule.ResourceLinked((WorkflowResourceKind)99),
            }) with
            {
                // Without the recipe the start step linked, every later step would also be reported.
                Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.Recipe)],
            },
            Codes.ResourceKindUnknown);
    }

    // ---- Resource order ----

    [Fact]
    public void A_step_cannot_read_a_recipe_the_run_does_not_hold_yet()
    {
        var withoutStart = Example with { Steps = [.. Example.Steps.Skip(1).Select(step => step with { Prerequisites = [] })] };

        var errors = WorkflowDefinitionValidator.Validate(withoutStart);

        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.Equal(Codes.ResourceNotYetLinked, error.Code));
        // Each of the three edit steps opens the recipe and reads its readiness; the scaling step opens it.
        Assert.Equal(7, errors.Count);
    }

    [Fact]
    public void An_optional_step_guarantees_no_resource_because_it_may_be_skipped()
    {
        var optionalStart = Example.WithStep("start", step => step with { Requirement = WorkflowStepRequirement.Optional });

        var errors = WorkflowDefinitionValidator.Validate(optionalStart);

        Assert.Contains(errors, error => error.Code == Codes.ResourceNotYetLinked && error.Path.Contains("steps[ingredients]"));
    }

    [Fact]
    public void A_workflow_that_starts_from_a_recipe_may_read_it_from_its_first_step()
    {
        var fromRecipe = Example with
        {
            Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.Recipe)],
            Steps = [.. Example.Steps.Skip(1).Select(step => step with { Prerequisites = [] })],
        };

        Assert.Empty(WorkflowDefinitionValidator.Validate(fromRecipe));
    }

    [Fact]
    public void A_step_that_requires_the_recipe_itself_may_read_it()
    {
        var guarded = Example
            .WithStep("start", step => step with { Requirement = WorkflowStepRequirement.Optional })
            .WithStep("ingredients", step => step with
            {
                Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.Recipe)],
            });

        Assert.DoesNotContain(
            WorkflowDefinitionValidator.Validate(guarded),
            error => error.Path.Contains("steps[ingredients]"));
    }

    // ---- Capabilities ----

    [Fact]
    public void A_step_names_a_registered_capability()
    {
        AssertOnly(Example.WithStep("method", step => step with { Capability = new("recipes.nowhere") }), Codes.CapabilityUnknown);
        AssertOnly(
            Example.WithStep("start", step => step with { Capability = new(WorkflowCapabilities.RecipesNew, ["recipes.nowhere"]) }),
            Codes.CapabilityUnknown);
    }

    [Fact]
    public void A_step_offers_one_primary_action_and_at_most_two_alternatives()
    {
        AssertOnly(
            Example.WithStep("try-sizes", step => step with
            {
                Capability = new(
                    WorkflowCapabilities.RecipesScalePreview,
                    [WorkflowCapabilities.RecipesEdit, WorkflowCapabilities.RecipesNew, WorkflowCapabilities.RecipesDraftWithHelp]),
            }),
            Codes.CapabilityTooManyAlternatives);
    }

    [Fact]
    public void A_step_does_not_offer_the_same_capability_twice()
    {
        AssertOnly(
            Example.WithStep("start", step => step with { Capability = new(WorkflowCapabilities.RecipesNew, [WorkflowCapabilities.RecipesNew]) }),
            Codes.CapabilityDuplicate);
    }

    [Fact]
    public void A_required_step_cannot_rest_on_a_capability_that_does_not_exist_yet()
    {
        var registry = RegistryWithUnavailable(WorkflowCapabilities.RecipesEdit);

        var errors = WorkflowDefinitionValidator.Validate(Example, registry);

        Assert.Equal(3, errors.Count);
        Assert.All(errors, error => Assert.Equal(Codes.CapabilityUnavailableForRequiredStep, error.Code));
    }

    [Fact]
    public void An_optional_step_or_an_alternative_may_name_one_that_does_not_exist_yet()
    {
        Assert.Empty(WorkflowDefinitionValidator.Validate(Example, RegistryWithUnavailable(WorkflowCapabilities.RecipesScalePreview)));
        Assert.Empty(WorkflowDefinitionValidator.Validate(Example, RegistryWithUnavailable(WorkflowCapabilities.RecipesDraftWithHelp)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/recipes")]
    [InlineData("recipes/")]
    [InlineData("Recipes")]
    [InlineData("recipes/{orderId}")]
    [InlineData("recipes/{}")]
    [InlineData("recipes/{recipeId")]
    [InlineData("https://example.com/recipes")]
    public void A_route_is_relative_lowercase_segments_and_known_placeholders(string route)
    {
        var errors = WorkflowDefinitionValidator.ValidateCapabilities(
            [new WorkflowCapability("recipes.somewhere", "Go there", route, WorkspaceRole.Viewer)]);

        Assert.Equal([Codes.CapabilityRouteInvalid], errors.Select(error => error.Code));
    }

    [Fact]
    public void A_registered_capability_has_one_key_a_plain_name_and_says_why_it_is_unavailable()
    {
        var errors = WorkflowDefinitionValidator.ValidateCapabilities(
        [
            new("recipes.edit", "Open your recipe", "recipes/{recipeId}", WorkspaceRole.Viewer),
            new("recipes.edit", "Open the recipe JSON", "recipes/{recipeId}", WorkspaceRole.Viewer),
            new("recipes.later", "Later", "recipes", WorkspaceRole.Viewer, UnavailableReason: " "),
        ]);

        Assert.Equal(
            [Codes.CapabilityDuplicate, Codes.CopyJargon, Codes.CopyMissing],
            errors.Select(error => error.Code));
    }

    // ---- Completion rules ----

    [Fact]
    public void A_required_step_is_never_proved_by_the_creators_word_alone()
    {
        AssertOnly(
            Example.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.Acknowledged() }),
            Codes.CompletionAcknowledgedOnRequiredStep);
    }

    [Fact]
    public void A_readiness_rule_completion_names_rules_the_recipe_module_has()
    {
        AssertOnly(
            Example.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet([]) }),
            Codes.CompletionReadinessRulesEmpty);
        AssertOnly(
            Example.WithStep("method", step => step with
            {
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet(
                    [RecipeReadinessCatalogue.InstructionsPresent, "recipe.instructions.count"]),
            }),
            Codes.CompletionReadinessRuleUnknown);
    }

    [Fact]
    public void Every_readiness_rule_in_the_catalogue_is_accepted()
    {
        var everyRule = Example.WithStep("method", step => step with
        {
            CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet([.. RecipeReadinessCatalogue.Rules.Select(rule => rule.Id)]),
        });

        Assert.Empty(WorkflowDefinitionValidator.Validate(everyRule));
    }

    [Fact]
    public void A_status_completion_names_recipe_statuses_that_exist()
    {
        Assert.Empty(WorkflowDefinitionValidator.Validate(Example.WithStep("method", step => step with
        {
            CompletionRule = new WorkflowCompletionRule.RecipeStatusIn([RecipeStatus.InDevelopment, RecipeStatus.Testing]),
        })));
        AssertOnly(
            Example.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.RecipeStatusIn([]) }),
            Codes.CompletionStatusesEmpty);
        AssertOnly(
            Example.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.RecipeStatusIn([(RecipeStatus)42]) }),
            Codes.CompletionStatusUnknown);
    }

    // ---- Suggestions ----

    [Fact]
    public void At_most_three_suggestions()
    {
        AssertOnly(
            Example with
            {
                NextSuggestions =
                [
                    .. Example.NextSuggestions,
                    new WorkflowSuggestion.Capability(WorkflowCapabilities.RecipesEdit, "Keep working on it."),
                ],
            },
            Codes.SuggestionsTooMany);
    }

    [Fact]
    public void A_suggestion_opens_something_not_already_suggested()
    {
        AssertOnly(
            Example with
            {
                NextSuggestions =
                [
                    new WorkflowSuggestion.Workflow("images.create", "Make pictures of it."),
                    new WorkflowSuggestion.Workflow("images.create", "A different reason for the same thing."),
                ],
            },
            Codes.SuggestionDuplicate);
    }

    [Fact]
    public void A_workflow_does_not_suggest_itself()
    {
        AssertOnly(
            Example with { NextSuggestions = [new WorkflowSuggestion.Workflow(Example.Key, "Make another.")] },
            Codes.SuggestionSelf);
    }

    [Fact]
    public void A_suggestion_names_a_well_formed_workflow_or_a_registered_capability_and_says_why_in_plain_words()
    {
        AssertOnly(Example with { NextSuggestions = [new WorkflowSuggestion.Workflow("Images", "Make pictures.")] }, Codes.KeyInvalid);
        AssertOnly(
            Example with { NextSuggestions = [new WorkflowSuggestion.Capability("images.nowhere", "Make pictures.")] },
            Codes.CapabilityUnknown);
        AssertOnly(Example with { NextSuggestions = [new WorkflowSuggestion.Workflow("images.create", "")] }, Codes.CopyMissing);
    }

    // ---- Area routes (13A.4) ----

    [Theory]
    [InlineData("recipes/{recipeId}?area=tests")]
    [InlineData("content-board?view=week")]
    public void A_route_may_open_one_of_its_pages_areas(string route)
    {
        Assert.Empty(WorkflowDefinitionValidator.ValidateCapabilities(
            [new WorkflowCapability("recipes.somewhere", "Go there", route, WorkspaceRole.Viewer)]));
    }

    [Theory]
    [InlineData("recipes?area")]
    [InlineData("recipes?area=")]
    [InlineData("recipes?Area=tests")]
    [InlineData("recipes?area=tests?view=week")]
    [InlineData("recipes?area=tests&view=week")]
    [InlineData("recipes?area={recipeId}")]
    [InlineData("?area=tests")]
    public void An_area_is_one_fixed_lowercase_name_and_value_and_never_filled_from_a_run(string route)
    {
        Assert.Equal(
            [Codes.CapabilityRouteInvalid],
            WorkflowDefinitionValidator.ValidateCapabilities(
                [new WorkflowCapability("recipes.somewhere", "Go there", route, WorkspaceRole.Viewer)]).Select(error => error.Code));
    }

    // ---- Linking several records at once (13A.4) ----

    [Fact]
    public void A_step_that_links_records_names_at_least_one_kind_and_each_once()
    {
        AssertOnly(
            Example.WithStep("start", step => step with { CompletionRule = new WorkflowCompletionRule.ResourceLinked([]) }) with
            {
                Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.Recipe)],
            },
            Codes.CompletionKindsEmpty);
        AssertOnly(
            Example.WithStep("start", step => step with
            {
                CompletionRule = new WorkflowCompletionRule.ResourceLinked([WorkflowResourceKind.Recipe, WorkflowResourceKind.Recipe]),
            }),
            Codes.CompletionKindDuplicate);
    }

    [Fact]
    public void Every_kind_a_step_links_is_held_by_the_steps_after_it()
    {
        // The long-form shape: one step files the context and the draft together, and a later one opens both.
        var definition = Example with
        {
            Steps =
            [
                Example.Steps[0] with
                {
                    Capability = new(WorkflowCapabilities.LongFormStart),
                    CompletionRule = new WorkflowCompletionRule.ResourceLinked(
                        [WorkflowResourceKind.CreativeContext, WorkflowResourceKind.LongFormDraft]),
                },
                Example.Steps[1] with
                {
                    Capability = new(WorkflowCapabilities.LongFormOutline),
                    CompletionRule = new WorkflowCompletionRule.LongFormDraftStatusIn([LongFormDraftStatus.Outlined]),
                },
            ],
        };

        Assert.Empty(WorkflowDefinitionValidator.Validate(definition));
    }

    // ---- Facts other modules decide (13A.4) ----

    public static TheoryData<string, WorkflowCompletionRule> RulesReadingAnUnlinkedRecord() => new()
    {
        { "draft state", new WorkflowCompletionRule.LongFormDraftStatusIn([LongFormDraftStatus.Accepted]) },
        { "guide switched on", new WorkflowCompletionRule.BrandGuideIsActive() },
        { "post accepted", new WorkflowCompletionRule.SocialPostAccepted() },
        { "booking state", new WorkflowCompletionRule.PublishingRecordStatusIn([PublishingRecordStatus.Scheduled]) },
    };

    /// <summary>
    /// The example's run holds a recipe and nothing else, so each of these reads a record it never linked — and
    /// a fact about a record the run never held would be satisfied by anybody's.
    /// </summary>
    [Theory]
    [MemberData(nameof(RulesReadingAnUnlinkedRecord))]
    public void A_fact_about_a_record_needs_the_run_to_hold_that_record(string what, WorkflowCompletionRule rule)
    {
        Assert.False(string.IsNullOrEmpty(what));
        AssertOnly(Example.WithStep("method", step => step with { CompletionRule = rule }), Codes.ResourceNotYetLinked);
    }

    [Fact]
    public void A_recipe_saved_during_the_run_needs_the_runs_recipe()
    {
        Assert.Empty(WorkflowDefinitionValidator.Validate(
            Example.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.RecipeSavedDuringRun() })));
    }

    [Fact]
    public void A_state_fact_names_states_that_exist()
    {
        var withDraft = Example with { Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.LongFormDraft)] };
        var withBooking = Example with { Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.PublishingRecord)] };

        AssertOnly(
            withDraft.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.LongFormDraftStatusIn([]) }),
            Codes.CompletionStatusesEmpty);
        AssertOnly(
            withDraft.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.LongFormDraftStatusIn([(LongFormDraftStatus)40]) }),
            Codes.CompletionStatusUnknown);
        AssertOnly(
            withBooking.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.PublishingRecordStatusIn([]) }),
            Codes.CompletionStatusesEmpty);
        AssertOnly(
            withBooking.WithStep("method", step => step with { CompletionRule = new WorkflowCompletionRule.PublishingRecordStatusIn([(PublishingRecordStatus)40]) }),
            Codes.CompletionStatusUnknown);
    }

    [Fact]
    public void A_step_may_wait_for_a_workspace_fact_the_contract_knows()
    {
        Assert.Empty(WorkflowDefinitionValidator.Validate(Example.WithStep("method", step => step with
        {
            Prerequisites = [new WorkflowPrerequisite.WorkspaceFact(WorkflowWorkspaceFact.SchedulingZoneSet)],
        })));
        AssertOnly(
            Example.WithStep("method", step => step with { Prerequisites = [new WorkflowPrerequisite.WorkspaceFact((WorkflowWorkspaceFact)9)] }),
            Codes.PrerequisiteFactUnknown);
    }

    // ---- Unavailable workflows (13A.4) ----

    [Fact]
    public void A_workflow_that_says_why_may_rest_on_something_not_built_yet()
    {
        var registry = RegistryWithUnavailable(WorkflowCapabilities.RecipesEdit);

        Assert.Empty(WorkflowDefinitionValidator.Validate(Example with { UnavailableReason = "This is coming soon." }, registry));
    }

    [Fact]
    public void A_workflow_cannot_claim_to_be_unavailable_when_everything_it_needs_is_built()
    {
        AssertOnly(Example with { UnavailableReason = "This is coming soon." }, Codes.UnavailableNotBlocked);
    }

    [Fact]
    public void An_unavailable_workflow_says_why_in_plain_words()
    {
        var registry = RegistryWithUnavailable(WorkflowCapabilities.RecipesEdit);

        Assert.Equal(
            [Codes.CopyMissing],
            WorkflowDefinitionValidator.Validate(Example with { UnavailableReason = " " }, registry).Select(error => error.Code));
        Assert.Equal(
            [Codes.CopyJargon],
            WorkflowDefinitionValidator.Validate(Example with { UnavailableReason = "The API is not ready." }, registry).Select(error => error.Code));
    }

    private static void AssertOnly(WorkflowDefinition definition, string code)
    {
        var errors = WorkflowDefinitionValidator.Validate(definition);

        Assert.True(
            errors.Count > 0 && errors.All(error => error.Code == code),
            $"expected only {code}, got: {(errors.Count == 0 ? "no errors" : string.Join("; ", errors))}");
    }

    private static Dictionary<WorkflowCapabilityKey, WorkflowCapability> RegistryWithUnavailable(string key) =>
        WorkflowCapabilities.All.ToDictionary(
            capability => capability.Key,
            capability => capability.Key.Value == key
                ? capability with { UnavailableReason = "This is coming soon." }
                : capability);
}
