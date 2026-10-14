namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// Every workflow definition the application ships, every version of each (FLOW-001, 13A.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Definitions only.</strong> Goals, steps, prerequisites and outcomes, each step pointing at a
/// capability in <see cref="WorkflowCapabilities"/>. Nothing here draws a screen, stores a run or performs a
/// domain operation.
/// </para>
/// <para>
/// <strong>A shipped version is frozen and is never removed.</strong> A run pins the version it started on
/// (13A.6), so a change — copy included — is a new version added beside the old one. The reviewed snapshot
/// <c>Tests/Workflows/Snapshots/workflow-definitions.json</c> refuses an edit in place.
/// </para>
/// <para>
/// One partial file per area keeps each journey next to its neighbours; this file holds the keys and the list.
/// </para>
/// </remarks>
public static partial class WorkflowCatalogue
{
    public const string RecipeCreateKey = "recipe.create";

    public const string RecipeImproveKey = "recipe.improve";

    public const string RecipeTestAndApproveKey = "recipe.test-and-approve";

    public const string BrandVoiceKey = "brand.voice";

    public const string BlogPostKey = "content.blog-and-seo";

    public const string ImagesKey = "images.create";

    public const string SocialPostsKey = "social.create";

    public const string OrganizeAssetsKey = "assets.organize";

    public const string PlanAndPublishKey = "publishing.plan-and-publish";

    public const string PlanDayAndWeekKey = "planning.day-and-week";

    /// <summary>Every definition, every version, in the order a creator meets them.</summary>
    /// <remarks>
    /// Lazy because the definitions live in other parts of this partial class, and C# does not order static
    /// initializers across partial declarations: an eager list could be built before the entries it names.
    /// </remarks>
    public static IReadOnlyList<WorkflowDefinition> All => Everything.Value;

    private static readonly Lazy<IReadOnlyList<WorkflowDefinition>> Everything = new(() =>
    [
        RecipeCreateV1,
        RecipeImproveV1,
        RecipeTestAndApproveV1,
        BrandVoiceV1,
        BlogPostV1,
        ImagesV1,
        SocialPostsV1,
        OrganizeAssetsV1,
        PlanAndPublishV1,
        PlanDayAndWeekV1,
    ]);

    /// <summary>The definition a run pinned, or null when no such key and version was ever shipped.</summary>
    public static WorkflowDefinition? Find(WorkflowKey key, int version) =>
        All.FirstOrDefault(definition => definition.Key == key && definition.Version == version);

    /// <summary>The newest version of each workflow: what a new run starts on.</summary>
    public static IReadOnlyList<WorkflowDefinition> Latest() =>
        [.. All.GroupBy(definition => definition.Key).Select(family => family.MaxBy(definition => definition.Version)!)];
}
