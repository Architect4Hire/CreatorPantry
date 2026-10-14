using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

public static partial class WorkflowCatalogue
{
    public static WorkflowDefinition OrganizeAssetsV1 { get; } = new()
    {
        Key = OrganizeAssetsKey,
        Version = 1,
        StartsFrom = [WorkflowResourceKind.MediaAsset],
        Goal = "Organize your pictures",
        Description = "Add a picture to your library, or pick one already there, and describe it so you can find it again.",
        ExpectedOutcome = "A picture in your library, described and tagged so you can find it again.",
        Steps =
        [
            new()
            {
                Key = "add",
                Title = "Upload a picture or choose one",
                Help = "Your original is kept exactly as you uploaded it.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.AssetsLibrary),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.MediaAsset),
            },
            new()
            {
                Key = "describe",
                Title = "Describe it",
                Help = "Say what is in the picture, for people who cannot see it and for search.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.AssetsDetail),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "tag",
                Title = "Tag it",
                Help = "Pick from your tags so it turns up when you look for it later.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.AssetsDetail),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "note-use",
                Title = "Note where you used it",
                Help = "Keep track of where it has already appeared.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.AssetsDetail),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(SocialPostsKey, "Write posts that use it."),
            new WorkflowSuggestion.Workflow(ImagesKey, "Make more pictures in the same style."),
        ],
    };

    /// <summary>
    /// Publishing as the publishing module records it: a booking is done when the record says so, and an
    /// answer the provider has not given is never read as one (13A.22, publishing.md).
    /// </summary>
    public static WorkflowDefinition PlanAndPublishV1 { get; } = new()
    {
        Key = PlanAndPublishKey,
        Version = 1,
        StartsFrom = [WorkflowResourceKind.ContentCard],
        Goal = "Plan and publish content",
        Description = "Choose something from your board, pick when it goes out in your own time zone, and confirm it.",
        ExpectedOutcome = "A post booked for the time you chose in your own time zone, and a clear answer on whether it went through.",
        Steps =
        [
            new()
            {
                Key = "choose",
                Title = "Choose what to publish",
                Help = "Pick a card from your board.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.BoardLibrary),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.ContentCard),
            },
            new()
            {
                Key = "check",
                Title = "Check it's ready",
                Help = "Read the words and look at the picture one last time.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.BoardCard),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "schedule",
                Title = "Pick a time and schedule it",
                Help = "Times are in your workspace's time zone. You confirm before anything is sent, and an Editor makes the booking.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.BoardSchedule),

                // Scheduling refuses to guess a zone, so the step waits for one and says where to set it.
                Prerequisites = [new WorkflowPrerequisite.WorkspaceFact(WorkflowWorkspaceFact.SchedulingZoneSet)],
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.PublishingRecord),
            },
            new()
            {
                Key = "confirm",
                Title = "Check it went through",
                Help = "See whether the booking was taken. If we couldn't tell, we say so and tell you what to do next.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.BoardCard),
                CompletionRule = new WorkflowCompletionRule.PublishingRecordStatusIn(
                    [PublishingRecordStatus.Scheduled, PublishingRecordStatus.Published]),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(SocialPostsKey, "Write the next batch of posts."),
        ],
    };

    /// <summary>
    /// Registered so the hub can say it is coming rather than leave a gap (13A.13); it cannot be started until
    /// work tasks exist (13A.23 onward). Its steps already name the capabilities that will carry them.
    /// </summary>
    public static WorkflowDefinition PlanDayAndWeekV1 { get; } = new()
    {
        Key = PlanDayAndWeekKey,
        Version = 1,
        Goal = "Plan my day and week",
        Description = "Jot down what needs doing, pick what to do today, and lay out the rest of the week.",
        ExpectedOutcome = "A short list for today and a plan for the week, kept apart from your content board.",
        UnavailableReason = "Your day and week planner isn't built yet.",
        Steps =
        [
            new()
            {
                Key = "capture",
                Title = "Jot down what needs doing",
                Help = "One line each. Details can wait.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.PlanningDay),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.WorkTask),
            },
            new()
            {
                Key = "today",
                Title = "Pick what to do today",
                Help = "Choose a few things for today. Anything else stays on your list.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.PlanningDay),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.WorkTask),
            },
            new()
            {
                Key = "week",
                Title = "Lay out your week",
                Help = "Spread the rest across the days ahead.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.PlanningWeek),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
        ],
    };
}
