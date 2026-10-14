using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

public static partial class WorkflowCatalogue
{
    /// <summary>The example approved with the contract (13A.1), unchanged.</summary>
    public static WorkflowDefinition RecipeCreateV1 { get; } = new()
    {
        Key = RecipeCreateKey,
        Version = 1,
        Goal = "Create a recipe",
        Description = "Write a recipe yourself or start from a first draft, then fill in what a recipe needs before you test it.",
        ExpectedOutcome = "A recipe in your library with its ingredients, method, yield and time filled in, ready to test.",
        Steps =
        [
            new()
            {
                Key = "start",
                Title = "Start your recipe",
                Help = "Write it yourself, or get a first draft to change however you like. Nothing becomes your recipe until you keep it.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesNew, [WorkflowCapabilities.RecipesDraftWithHelp]),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.Recipe),
            },
            new()
            {
                Key = "ingredients",
                Title = "Check your ingredients",
                Help = "List everything that goes in, in your own words. We keep exactly what you write.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesEdit),
                Prerequisites = [new WorkflowPrerequisite.StepCompleted("start")],
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet([RecipeReadinessCatalogue.IngredientsPresent]),
            },
            new()
            {
                Key = "method",
                Title = "Check your method",
                Help = "Write the steps in the order you cook them, so someone else could follow along.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesEdit),
                Prerequisites = [new WorkflowPrerequisite.StepCompleted("start")],
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet([RecipeReadinessCatalogue.InstructionsPresent]),
            },
            new()
            {
                Key = "yield-and-time",
                Title = "What it makes and how long it takes",
                Help = "Say how much it makes or how many it serves, and how long it takes, so readers can plan.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesEdit),
                Prerequisites = [new WorkflowPrerequisite.StepCompleted("start")],
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet(
                    [RecipeReadinessCatalogue.YieldStated, RecipeReadinessCatalogue.TimeStated]),
            },
            new()
            {
                Key = "try-sizes",
                Title = "Try it at another size",
                Help = "See the amounts for a bigger or smaller batch. Nothing changes in your recipe unless you choose to keep it.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.RecipesScalePreview),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(RecipeTestAndApproveKey, "Cook it, note what happened, and approve it when it is right."),
            new WorkflowSuggestion.Workflow(BlogPostKey, "Turn it into a blog post people can find."),
            new WorkflowSuggestion.Workflow(ImagesKey, "Make pictures of the finished dish."),
        ],
    };

    public static WorkflowDefinition RecipeImproveV1 { get; } = new()
    {
        Key = RecipeImproveKey,
        Version = 1,
        StartsFrom = [WorkflowResourceKind.Recipe],
        Goal = "Improve a recipe",
        Description = "Change a recipe you already have, yourself or with suggestions you approve one by one.",
        ExpectedOutcome = "Your recipe with the changes you chose, saved as a new version, and the earlier one kept.",
        Steps =
        [
            ChooseRecipe(),
            new()
            {
                Key = "change",
                Title = "Make your changes",
                Help = "Ask for suggestions or change it yourself. Nothing changes until you say yes, and your own words stay as you wrote them.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesSuggestChanges, [WorkflowCapabilities.RecipesEdit]),
                CompletionRule = new WorkflowCompletionRule.RecipeSavedDuringRun(),
            },
            new()
            {
                Key = "decide",
                Title = "Decide on every suggestion",
                Help = "Say yes or no to anything still waiting, and read any safety note before you go on.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesEdit),
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet(
                    [RecipeReadinessCatalogue.AiChangesPending, RecipeReadinessCatalogue.AiSafetyCautionOutstanding]),
            },
            new()
            {
                Key = "compare",
                Title = "Compare with before",
                Help = "See exactly what changed. Every earlier version is kept, so you can go back.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.RecipesHistory),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "try-sizes",
                Title = "Try it at another size",
                Help = "See the amounts for a bigger or smaller batch. Nothing changes in your recipe unless you choose to keep it.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.RecipesScalePreview),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(RecipeTestAndApproveKey, "Cook the new version and approve it when it is right."),
            new WorkflowSuggestion.Workflow(BlogPostKey, "Tell your readers what changed."),
        ],
    };

    public static WorkflowDefinition RecipeTestAndApproveV1 { get; } = new()
    {
        Key = RecipeTestAndApproveKey,
        Version = 1,
        StartsFrom = [WorkflowResourceKind.Recipe],
        Goal = "Test and approve a recipe",
        Description = "Cook it, write down what happened, fix what went wrong, and give it the final yes.",
        ExpectedOutcome = "A recipe you have cooked, fixed and approved, with your test notes kept.",
        Steps =
        [
            ChooseRecipe(),
            new()
            {
                Key = "basics",
                Title = "Fill in the basics",
                Help = "A recipe needs its ingredients, method, yield and time before it can be tested.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesEdit),
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet(
                [
                    RecipeReadinessCatalogue.IngredientsPresent,
                    RecipeReadinessCatalogue.InstructionsPresent,
                    RecipeReadinessCatalogue.YieldStated,
                    RecipeReadinessCatalogue.TimeStated,
                ]),
            },
            new()
            {
                Key = "cook",
                Title = "Cook it and note what happened",
                Help = "Make it from the recipe as written, then say how it turned out and anything that went wrong.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesTestKitchen),
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet([RecipeReadinessCatalogue.TestingCurrentVersionUntested]),
            },
            new()
            {
                Key = "fix",
                Title = "Sort out what went wrong",
                Help = "Fix the recipe or close each problem you noted. If a test failed, cook it again once it is fixed.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesTestKitchen, [WorkflowCapabilities.RecipesEdit]),
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet(
                    [RecipeReadinessCatalogue.TestingLastOutcomeFailed, RecipeReadinessCatalogue.TestingBlockingIssueOutstanding]),
            },
            new()
            {
                Key = "send-for-review",
                Title = "Send it for review",
                Help = "Say it is ready, and see anything still in the way first.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesReadiness),
                CompletionRule = new WorkflowCompletionRule.RecipeStatusIn([RecipeStatus.ReadyForReview, RecipeStatus.Approved]),
            },
            new()
            {
                Key = "approve",
                Title = "Approve it",
                Help = "Someone with Editor access gives it the final yes. Approving keeps this exact version.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.RecipesApprove),
                CompletionRule = new WorkflowCompletionRule.RecipeStatusIn([RecipeStatus.Approved]),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(BlogPostKey, "Share the finished recipe in a blog post."),
            new WorkflowSuggestion.Workflow(ImagesKey, "Make pictures of the finished dish."),
            new WorkflowSuggestion.Workflow(SocialPostsKey, "Tell your followers it is ready."),
        ],
    };

    /// <summary>The first step of every journey that works on a recipe the creator already has.</summary>
    /// <remarks>Satisfied at once by a run started from a recipe (13A.5), which links it at the start.</remarks>
    private static WorkflowStepDefinition ChooseRecipe() => new()
    {
        Key = "choose",
        Title = "Choose the recipe",
        Help = "Pick the recipe from your library.",
        Requirement = WorkflowStepRequirement.Required,
        Capability = new(WorkflowCapabilities.RecipesLibrary),
        CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.Recipe),
    };
}
