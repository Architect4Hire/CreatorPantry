using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

public static partial class WorkflowCatalogue
{
    /// <summary>
    /// The long-form pipeline (LF.6.2) as a journey: its steps are the pipeline's, and each is done when the
    /// draft says so — a step is never ticked off here ahead of the piece (LF.6.3, 13A.18).
    /// </summary>
    public static WorkflowDefinition BlogPostV1 { get; } = new()
    {
        Key = BlogPostKey,
        Version = 1,
        Goal = "Write a blog post or newsletter",
        Description = "Plan a full article or newsletter issue, write it section by section, and accept it when it reads right.",
        ExpectedOutcome = "A finished article or newsletter you have accepted, with search suggestions and a copy to download.",
        Steps =
        [
            new()
            {
                Key = "about",
                Title = "Choose what it's about",
                Help = "Start from one of your recipes or from an idea of your own.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.LongFormStart),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(
                    [WorkflowResourceKind.CreativeContext, WorkflowResourceKind.LongFormDraft]),
            },
            new()
            {
                Key = "outline",
                Title = "Agree the plan",
                Help = "Read the outline and change anything before any of it is written.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.LongFormOutline),
                CompletionRule = new WorkflowCompletionRule.LongFormDraftStatusIn(
                [
                    LongFormDraftStatus.Outlined,
                    LongFormDraftStatus.Writing,
                    LongFormDraftStatus.Assembled,
                    LongFormDraftStatus.Accepted,
                    LongFormDraftStatus.NeedsReview,
                ]),
            },
            new()
            {
                Key = "write",
                Title = "Write it",
                Help = "Write each section yourself or ask for a first go, then edit it however you like.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.LongFormWrite),
                CompletionRule = new WorkflowCompletionRule.LongFormDraftStatusIn(
                    [LongFormDraftStatus.Assembled, LongFormDraftStatus.Accepted, LongFormDraftStatus.NeedsReview]),
            },
            new()
            {
                Key = "accept",
                Title = "Read it and accept it",
                Help = "Read the whole piece. Accepting keeps this exact text as the finished one.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.LongFormReview),
                CompletionRule = new WorkflowCompletionRule.LongFormDraftStatusIn([LongFormDraftStatus.Accepted]),
            },
            new()
            {
                Key = "search",
                Title = "Get search suggestions",
                Help = "Suggested titles and descriptions to help people find it. They are suggestions only, never promises about ranking.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.LongFormSearchSuggestions),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "download",
                Title = "Download it",
                Help = "Take a copy to paste into your blog or newsletter tool.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.LongFormDownload),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(ImagesKey, "Make pictures to go with it."),
            new WorkflowSuggestion.Workflow(SocialPostsKey, "Tell your followers it is out."),
        ],
    };

    public static WorkflowDefinition ImagesV1 { get; } = new()
    {
        Key = ImagesKey,
        Version = 1,
        StartsFrom = [WorkflowResourceKind.Recipe],
        Goal = "Create food images",
        Description = "Plan a picture of your food, make a few, and keep the ones you like in your library.",
        ExpectedOutcome = "Pictures you chose, kept in your library, and the best one on your recipe if you like.",
        Steps =
        [
            new()
            {
                Key = "about",
                Title = "Choose what the picture is for",
                Help = "A recipe or an idea. Your brand's look is used unless you change it.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.ImagesStudio),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.CreativeContext),
            },
            new()
            {
                Key = "plan",
                Title = "Plan the picture",
                Help = "Describe the scene, or start from a suggestion. You can change every word before anything is made.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.ImagesStudioContext),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "make-and-keep",
                Title = "Make pictures and keep the best",
                Help = "You see what it will use up before anything is made. Keep the ones you like; the rest are not saved.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.ImagesStudioContext),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.MediaAsset),
            },
            new()
            {
                Key = "lead-picture",
                Title = "Use it as your recipe's lead picture",
                Help = "Put your favourite at the top of the recipe.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.RecipesLeadPicture),

                // Only offered for a picture of a recipe: the step reads the recipe the run is about.
                Prerequisites = [new WorkflowPrerequisite.ResourceLinked(WorkflowResourceKind.Recipe)],
                CompletionRule = new WorkflowCompletionRule.ReadinessRulesMet([RecipeReadinessCatalogue.MediaHeroMissing]),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(SocialPostsKey, "Write posts to go with your pictures."),
            new WorkflowSuggestion.Workflow(OrganizeAssetsKey, "Describe and tag them so you can find them again."),
        ],
    };

    public static WorkflowDefinition SocialPostsV1 { get; } = new()
    {
        Key = SocialPostsKey,
        Version = 1,
        Goal = "Create social posts",
        Description = "Write a post for each of your channels about one recipe or idea, and choose the ones you like.",
        ExpectedOutcome = "Posts you approved for each channel, with a picture, ready for your board.",
        Steps =
        [
            new()
            {
                Key = "about",
                Title = "Choose what it's about",
                Help = "A recipe or an idea. Your brand's voice and channels are used unless you change them.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.SocialStudio),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.CreativeContext),
            },
            new()
            {
                Key = "write",
                Title = "Write and choose your posts",
                Help = "Each channel gets its own post, kept within its length. Edit any of them, then keep the ones you like.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.SocialStudioContext),
                CompletionRule = new WorkflowCompletionRule.SocialPostAccepted(),
            },
            new()
            {
                Key = "picture",
                Title = "Pick a picture to go with them",
                Help = "Choose one from your library.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.SocialStudioContext),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "board",
                Title = "Add it to your content board",
                Help = "Put it on your board so you can plan when it goes out. Nothing is posted yet.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.BoardLibrary),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.ContentCard),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(PlanAndPublishKey, "Pick a time and schedule your posts."),
        ],
    };
}
