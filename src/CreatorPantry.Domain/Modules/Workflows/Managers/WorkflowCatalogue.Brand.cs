namespace CreatorPantry.Domain.Modules.Workflows.Managers;

public static partial class WorkflowCatalogue
{
    /// <summary>
    /// The Phase 11A setup wizard, carried by the workflow rather than forked: its first step is the wizard
    /// itself, and nothing here is a second way to build a guide (13A.17).
    /// </summary>
    public static WorkflowDefinition BrandVoiceV1 { get; } = new()
    {
        Key = BrandVoiceKey,
        Version = 1,
        Goal = "Create my brand voice",
        Description = "Answer a few questions and share some of your writing, and get a guide to how you sound.",
        ExpectedOutcome = "A brand guide that sounds like you, switched on so new writing uses it.",
        Steps =
        [
            new()
            {
                Key = "set-up",
                Title = "Tell us how you sound",
                Help = "A few questions about what you make and who it is for. You can stop and come back at any point.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.BrandSetup),
                CompletionRule = new WorkflowCompletionRule.ResourceLinked(WorkflowResourceKind.BrandStyleGuide),
            },
            new()
            {
                Key = "examples",
                Title = "Add writing you're proud of",
                Help = "A few pieces you wrote help the guide sound like you. Your originals are never changed.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.BrandExamples),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "review",
                Title = "Read your guide and change anything",
                Help = "It is your guide. Change any word that does not sound like you.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.BrandGuideEdit),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "try",
                Title = "Try it on a real piece",
                Help = "See a short piece written with your guide before you decide.",
                Requirement = WorkflowStepRequirement.Optional,
                Capability = new(WorkflowCapabilities.BrandTestDrive),
                CompletionRule = new WorkflowCompletionRule.Acknowledged(),
            },
            new()
            {
                Key = "switch-on",
                Title = "Switch it on",
                Help = "From now on, new writing follows this guide. The workspace Owner makes this choice, and can switch back.",
                Requirement = WorkflowStepRequirement.Required,
                Capability = new(WorkflowCapabilities.BrandSwitchOn),
                CompletionRule = new WorkflowCompletionRule.BrandGuideIsActive(),
            },
        ],
        NextSuggestions =
        [
            new WorkflowSuggestion.Workflow(BlogPostKey, "Write your first piece in your own voice."),
            new WorkflowSuggestion.Workflow(SocialPostsKey, "Write posts that sound like you."),
        ],
    };
}
