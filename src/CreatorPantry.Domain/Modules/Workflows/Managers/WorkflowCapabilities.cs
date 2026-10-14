using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// The capabilities a workflow step may name: implemented product surfaces, each with the route that opens it.
/// </summary>
/// <remarks>
/// <para>
/// A registry rather than a route string on every step, so a step names a capability by key and a moved route
/// is one edit here. A step that names a key not listed fails validation, and every route here is checked
/// against the web app's own route sources by the tests.
/// </para>
/// <para>
/// Several keys open the same address. A page holds more than one thing a creator does — the recipe page holds
/// its tools drawer, its tests and its readiness — and each is a different action with its own name and its own
/// least role. A tool in a drawer has no address of its own, so its key opens the page the drawer is on.
/// </para>
/// <para>
/// Anything not built carries an <see cref="WorkflowCapability.UnavailableReason"/> rather than being left out,
/// so a journey that needs it says so instead of quietly missing a step (13A.4).
/// </para>
/// </remarks>
public static class WorkflowCapabilities
{
    // ---- Recipes ----

    public const string RecipesNew = "recipes.new";

    public const string RecipesDraftWithHelp = "recipes.draft-with-help";

    public const string RecipesLibrary = "recipes.library";

    public const string RecipesEdit = "recipes.edit";

    public const string RecipesScalePreview = "recipes.scale-preview";

    public const string RecipesSuggestChanges = "recipes.suggest-changes";

    public const string RecipesHistory = "recipes.history";

    public const string RecipesTestKitchen = "recipes.test-kitchen";

    public const string RecipesReadiness = "recipes.readiness";

    public const string RecipesApprove = "recipes.approve";

    public const string RecipesLeadPicture = "recipes.lead-picture";

    // ---- Brand ----

    public const string BrandSetup = "brand.setup";

    public const string BrandExamples = "brand.examples";

    public const string BrandGuideEdit = "brand.guide-edit";

    public const string BrandTestDrive = "brand.test-drive";

    public const string BrandSwitchOn = "brand.switch-on";

    // ---- Long-form ----

    public const string LongFormStart = "long-form.start";

    public const string LongFormOutline = "long-form.outline";

    public const string LongFormWrite = "long-form.write";

    public const string LongFormReview = "long-form.review";

    public const string LongFormSearchSuggestions = "long-form.search-suggestions";

    public const string LongFormDownload = "long-form.download";

    // ---- Pictures and posts ----

    public const string ImagesStudio = "images.studio";

    public const string ImagesStudioContext = "images.studio-context";

    public const string SocialStudio = "social.studio";

    public const string SocialStudioContext = "social.studio-context";

    // ---- Library, board and publishing ----

    public const string AssetsLibrary = "assets.library";

    public const string AssetsDetail = "assets.detail";

    public const string BoardLibrary = "board.library";

    public const string BoardCard = "board.card";

    public const string BoardSchedule = "board.schedule";

    // ---- Planning (13A.23 onward) ----

    public const string PlanningDay = "planning.day";

    public const string PlanningWeek = "planning.week";

    private const string LongFormDraft = "workflows/long-form/context/{contextId}/draft/{draftId}";

    public static readonly IReadOnlyList<WorkflowCapability> All =
    [
        // A recipe saved before the creator moves on is the start step's draft.
        new(RecipesNew, "Write it yourself", "recipes/new", WorkspaceRole.Contributor, DraftKind: WorkflowResourceKind.Recipe),

        // The concept studio, then its first-draft review: a draft is a proposal until the creator keeps it.
        new(RecipesDraftWithHelp, "Help me draft it", "ai-recipe-studio", WorkspaceRole.Contributor,
            DraftKind: WorkflowResourceKind.CreativeContext),

        new(RecipesLibrary, "Choose from your recipes", "recipes", WorkspaceRole.Viewer),
        new(RecipesEdit, "Open your recipe", "recipes/{recipeId}", WorkspaceRole.Contributor),

        // Tools in the recipe page's drawer, which has no address of its own: these open the page it is on.
        new(RecipesScalePreview, "Try another size", "recipes/{recipeId}", WorkspaceRole.Viewer),
        new(RecipesSuggestChanges, "Get suggested changes", "recipes/{recipeId}", WorkspaceRole.Contributor),

        new(RecipesHistory, "See earlier versions", "recipes/{recipeId}?area=history", WorkspaceRole.Viewer),
        new(RecipesTestKitchen, "Record a test", "recipes/{recipeId}?area=tests", WorkspaceRole.Contributor),
        new(RecipesReadiness, "Check what's left", "recipes/{recipeId}?area=readiness", WorkspaceRole.Contributor),

        // The same area as readiness, and a different act: only an Editor may approve (TESTRUN-005).
        new(RecipesApprove, "Approve it", "recipes/{recipeId}?area=readiness", WorkspaceRole.Editor),
        new(RecipesLeadPicture, "Choose the lead picture", "recipes/{recipeId}?area=media", WorkspaceRole.Contributor),

        new(BrandSetup, "Set up your voice", "brand/setup", WorkspaceRole.Editor),
        new(BrandExamples, "Add your examples", "brand/library", WorkspaceRole.Editor),
        new(BrandGuideEdit, "Edit your guide", "brand/style-guide/{guideId}", WorkspaceRole.Editor),
        new(BrandTestDrive, "Try it out", "brand/style-guide/{guideId}/test-drive", WorkspaceRole.Contributor),

        // Activation is the Owner's alone.
        new(BrandSwitchOn, "Switch it on", "brand/style-guide/{guideId}/history", WorkspaceRole.Owner),

        new(LongFormStart, "Start a piece", "workflows/long-form", WorkspaceRole.Contributor,
            DraftKind: WorkflowResourceKind.LongFormDraft),
        new(LongFormOutline, "Plan it", $"{LongFormDraft}/outline", WorkspaceRole.Contributor),
        new(LongFormWrite, "Write it", $"{LongFormDraft}/write", WorkspaceRole.Contributor),
        new(LongFormReview, "Read and accept it", $"{LongFormDraft}/review", WorkspaceRole.Contributor),
        new(LongFormSearchSuggestions, "Get search suggestions", $"{LongFormDraft}/review", WorkspaceRole.Contributor),
        new(LongFormDownload, "Download it", $"{LongFormDraft}/review", WorkspaceRole.Viewer),

        new(ImagesStudio, "Make pictures", "image-studio", WorkspaceRole.Contributor,
            DraftKind: WorkflowResourceKind.CreativeContext),
        new(ImagesStudioContext, "Carry on with your pictures", "image-studio/context/{contextId}", WorkspaceRole.Contributor),
        new(SocialStudio, "Write posts", "social-studio", WorkspaceRole.Contributor,
            DraftKind: WorkflowResourceKind.CreativeContext),
        new(SocialStudioContext, "Carry on with your posts", "social-studio/context/{contextId}", WorkspaceRole.Contributor),

        new(AssetsLibrary, "Open your library", "dam", WorkspaceRole.Contributor),
        new(AssetsDetail, "Open the picture", "dam/{assetId}", WorkspaceRole.Contributor),

        new(BoardLibrary, "Open your board", "content-board", WorkspaceRole.Contributor),
        new(BoardCard, "Open the card", "content-board/{cardId}", WorkspaceRole.Viewer),

        // The schedule drawer on the card's page; scheduling is an Editor's (PUB-002).
        new(BoardSchedule, "Schedule it", "content-board/{cardId}", WorkspaceRole.Editor),

        new(PlanningDay, "Plan your day", "my-day", WorkspaceRole.Contributor,
            UnavailableReason: "Your day planner isn't built yet."),
        new(PlanningWeek, "Plan your week", "my-week", WorkspaceRole.Contributor,
            UnavailableReason: "Your week planner isn't built yet."),
    ];

    public static IReadOnlyDictionary<WorkflowCapabilityKey, WorkflowCapability> ByKey { get; } =
        All.ToDictionary(capability => capability.Key);
}
