using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>The single DbContext for the modular monolith.</summary>
/// <param name="workspaceContext">
/// Optional: unavailable outside a request/operation scope (migrations, unrelated hosts). Every
/// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/> entity is filtered by it via <see cref="WorkspaceOwnershipConvention"/>.
/// </param>
public class CreatorPantryDbContext(DbContextOptions<CreatorPantryDbContext> options, IWorkspaceContext? workspaceContext = null)
    : IdentityDbContext<ApplicationUser, IdentityRole, string>(options), IWorkspaceIdSource
{
    /// <summary>The Aspire connection name; matches the database resource in the AppHost.</summary>
    public const string ConnectionName = "creatorpantrydb";

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <remarks>
    /// Platform-scoped machine credentials (baseline B-14). Not workspace-owned: an ops client never acts as
    /// a creator and never holds workspace access, so there is no workspace to own it.
    /// </remarks>
    public DbSet<OpsApiClient> OpsApiClients => Set<OpsApiClient>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    /// <remarks>
    /// Unfiltered before a workspace is resolved (see <see cref="WorkspaceMembership"/>'s remarks) — a
    /// query here before resolution must supply its own explicit <c>WorkspaceId</c>/<c>UserId</c>
    /// predicate, the way <see cref="CreatorPantry.Domain.Modules.Tenancy.Data.WorkspaceRepository"/> does, or it reads every workspace.
    /// </remarks>
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <remarks>
    /// Platform-scoped and deliberately unfiltered: an action taken outside any workspace has no
    /// <c>WorkspaceId</c> to filter by. Safe to read that way because the row carries actor identifiers, an
    /// action code, a reason and compact state pointers, and never creator content — the same argument
    /// <see cref="CreatorPantry.Domain.Modules.AiUsage.Data.Entities.AccountAiUsageEntry"/> makes.
    /// </remarks>
    public DbSet<PlatformAuditLog> PlatformAuditLogs => Set<PlatformAuditLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <remarks>
    /// Platform reference data (tenancy.md): shared across every workspace, so it is deliberately not
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/> and carries no query filter. Unlike a workspace-owned set, this
    /// one is readable before a workspace is resolved.
    /// </remarks>
    public DbSet<MeasurementUnit> MeasurementUnits => Set<MeasurementUnit>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<UnitAlias> UnitAliases => Set<UnitAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<FoodCategory> FoodCategories => Set<FoodCategory>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<IngredientAlias> IngredientAliases => Set<IngredientAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Cuisine> Cuisines => Set<Cuisine>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<CuisineAlias> CuisineAliases => Set<CuisineAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Course> Courses => Set<Course>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<CourseAlias> CourseAliases => Set<CourseAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    /// <remarks>
    /// <see cref="CookingTechnique.RequiresSafetyCaution"/> reads in one direction only: true requires a
    /// caution, false means none is attached — never that the technique is safe (recipes.md).
    /// </remarks>
    public DbSet<CookingTechnique> CookingTechniques => Set<CookingTechnique>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<CookingTechniqueAlias> CookingTechniqueAliases => Set<CookingTechniqueAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<EquipmentTypeAlias> EquipmentTypeAliases => Set<EquipmentTypeAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<ReferenceSource> ReferenceSources => Set<ReferenceSource>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<IngredientDensityReference> IngredientDensityReferences => Set<IngredientDensityReference>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<DietaryProfile> DietaryProfiles => Set<DietaryProfile>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Allergen> Allergens => Set<Allergen>();

    /// <inheritdoc cref="MeasurementUnits"/>
    /// <remarks>
    /// A trait that is not here means unknown. Querying this set and finding nothing never means the
    /// ingredient is compatible with a profile (recipes.md).
    /// </remarks>
    public DbSet<IngredientDietaryTrait> IngredientDietaryTraits => Set<IngredientDietaryTrait>();

    /// <inheritdoc cref="MeasurementUnits"/>
    /// <remarks>
    /// A trait that is not here means unknown. Querying this set and finding nothing never means the
    /// ingredient is free of an allergen, and no caller may treat it that way (recipes.md, ai.md).
    /// </remarks>
    public DbSet<IngredientAllergenTrait> IngredientAllergenTraits => Set<IngredientAllergenTrait>();

    /// <remarks>
    /// Workspace-owned creator intellectual property (tenancy.md), and the root of the recipe aggregate.
    /// Filtered by <see cref="WorkspaceOwnershipConvention"/> like every other
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/> entity, which means querying it
    /// before a workspace is resolved throws rather than quietly returning nothing.
    /// </remarks>
    public DbSet<Recipe> Recipes => Set<Recipe>();

    /// <remarks>
    /// Interior to the <see cref="Recipe"/> aggregate. Exposed as a set because EF needs the entity type in
    /// the model, not because anything may write one on its own: the aggregate is loaded and saved through
    /// its root, and these rows reach the database only by way of a recipe.
    /// </remarks>
    public DbSet<RecipeIngredientGroup> RecipeIngredientGroups => Set<RecipeIngredientGroup>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeInstructionGroup> RecipeInstructionGroups => Set<RecipeInstructionGroup>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeInstructionStep> RecipeInstructionSteps => Set<RecipeInstructionStep>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeEquipment> RecipeEquipment => Set<RecipeEquipment>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeAssetLink> RecipeAssetLinks => Set<RecipeAssetLink>();

    /// <remarks>
    /// A second aggregate root, not part of <see cref="Recipe"/>: versions outlive the edits that supersede
    /// them, so this set is queried in its own right. Write-once — <see cref="ImmutableRecordInterceptor"/>
    /// refuses every update and delete of a row here.
    /// </remarks>
    public DbSet<RecipeVersion> RecipeVersions => Set<RecipeVersion>();

    /// <inheritdoc cref="RecipeVersions"/>
    /// <remarks>
    /// Separated from <see cref="RecipeVersions"/> so that listing a recipe's history does not read its
    /// archive. Query this only when a snapshot is actually needed.
    /// </remarks>
    public DbSet<RecipeVersionSnapshot> RecipeVersionSnapshots => Set<RecipeVersionSnapshot>();

    /// <remarks>
    /// How a recipe reached the editorial state it is in (TESTRUN-005). Write-once —
    /// <see cref="ImmutableRecordInterceptor"/> refuses every update and delete of a row here, because a
    /// transition is a thing that happened at a moment. A root of its own, by the same argument
    /// <see cref="RecipeVersions"/> is one: it outlives every edit made after it and is read as a history
    /// rather than loaded with the recipe, so it restricts rather than cascades from
    /// <see cref="Recipes"/>.
    /// </remarks>
    public DbSet<RecipeStatusTransition> RecipeStatusTransitions => Set<RecipeStatusTransition>();

    /// <remarks>
    /// The creator's own tag vocabulary — workspace-owned and defined entirely by them, unlike the shared
    /// <see cref="Cuisines"/> and <see cref="Courses"/> catalogues. A third aggregate root in the recipes
    /// module: tags are listed, renamed and retired on their own.
    /// </remarks>
    public DbSet<WorkspaceTag> WorkspaceTags => Set<WorkspaceTag>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeTag> RecipeTags => Set<RecipeTag>();

    /// <remarks>
    /// One cook of one exact recipe version, and the root of the test-run aggregate. A fourth root in the
    /// recipes module: tests are listed, filtered and read on their own, and they outlive nothing — they are
    /// pinned to an immutable version by a <c>Restrict</c> foreign key, so a version with test history cannot
    /// be removed out from under them.
    /// </remarks>
    public DbSet<RecipeTestRun> RecipeTestRuns => Set<RecipeTestRun>();

    /// <remarks>
    /// Interior to the <see cref="RecipeTestRun"/> aggregate. Exposed as a set because EF needs the entity
    /// type in the model, not because anything may write one on its own. Mutable, unlike the AI module's
    /// warnings: these are the tester's own words about their own cook, and correcting them is the creator
    /// editing their own content rather than the system rewriting it.
    /// </remarks>
    public DbSet<TestObservation> TestObservations => Set<TestObservation>();

    /// <inheritdoc cref="TestObservations"/>
    /// <remarks>
    /// Carries no resolved flag. Whether an issue has been dealt with is answered by whether a
    /// <see cref="TestIssueResolution"/> exists for it, so there is no second copy of that fact to drift.
    /// </remarks>
    public DbSet<TestIssue> TestIssues => Set<TestIssue>();

    /// <inheritdoc cref="TestObservations"/>
    /// <remarks>
    /// The one write-once entity in this aggregate — <see cref="ImmutableRecordInterceptor"/> refuses every
    /// update and delete of a row here, because a resolution records a decision somebody took at a moment
    /// rather than an observation somebody is still making. At most one per issue, by unique index.
    /// </remarks>
    public DbSet<TestIssueResolution> TestIssueResolutions => Set<TestIssueResolution>();

    /// <inheritdoc cref="TestObservations"/>
    /// <remarks>
    /// Records a test's use of a media asset and never the asset itself: no bytes, no copied alt text. Like
    /// <see cref="RecipeAssetLinks"/>, its <c>MediaAssetId</c> has no foreign key until the media aggregate
    /// lands, so the write seam is what keeps an attachment inside the workspace until then.
    /// </remarks>
    public DbSet<TestAttachmentLink> TestAttachmentLinks => Set<TestAttachmentLink>();

    /// <remarks>
    /// The AI module's aggregate root: one request for assistance and what became of it. Deliberately holds
    /// no prompt body, model response, or generated text — ai.md forbids storing those by default, and a
    /// table with no column for them cannot accumulate them by accident.
    /// </remarks>
    public DbSet<AiOperation> AiOperations => Set<AiOperation>();

    /// <remarks>
    /// What one operation proposed, and the root of the proposal aggregate. Write-once —
    /// <see cref="ImmutableRecordInterceptor"/> refuses every update and delete of a row here, because a
    /// proposal records what a model said at a moment and a different answer is a different operation.
    /// </remarks>
    public DbSet<AiProposal> AiProposals => Set<AiProposal>();

    /// <remarks>
    /// Interior to the <see cref="AiProposal"/> aggregate, and the one entity in this module that is not
    /// write-once: the creator's per-change decision is written after insert. Everything describing the change
    /// is written once by the seam that creates the proposal.
    /// </remarks>
    public DbSet<AiStructuredChange> AiStructuredChanges => Set<AiStructuredChange>();

    /// <inheritdoc cref="AiStructuredChanges"/>
    /// <remarks>
    /// Write-once. A warning records what a creator was shown at review time; absence of one is never a
    /// finding of safety.
    /// </remarks>
    public DbSet<AiWarning> AiWarnings => Set<AiWarning>();

    /// <inheritdoc cref="AiStructuredChanges"/>
    /// <remarks>Write-once and append-only: a creator who changes their mind leaves another row.</remarks>
    public DbSet<AiProposalFeedback> AiProposalFeedback => Set<AiProposalFeedback>();

    /// <inheritdoc cref="AiStructuredChanges"/>
    /// <remarks>
    /// Write-once. Which brand guide version, profile revision and cited passages one proposal was grounded on.
    /// At most one row per proposal, and none at all when the generation asked for no brand context.
    /// </remarks>
    public DbSet<AiProposalBrandContext> AiProposalBrandContexts => Set<AiProposalBrandContext>();

    /// <inheritdoc cref="AiProposalBrandContexts"/>
    /// <remarks>
    /// Write-once. One row per cited passage, holding identifiers and no text: the parent's checksum is what
    /// pins the words.
    /// </remarks>
    public DbSet<AiProposalBrandSource> AiProposalBrandSources => Set<AiProposalBrandSource>();

    /// <remarks>
    /// One write-once row per provider attempt, hanging off the operation rather than the proposal because a
    /// failed attempt produces no proposal. Holds no prompt body, response, or provider payload.
    /// </remarks>
    public DbSet<AiExecutionMetadata> AiExecutionMetadata => Set<AiExecutionMetadata>();

    /// <remarks>
    /// <para>
    /// The per-account AI usage ledger (USAGE-001/002). Deliberately <em>not</em>
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/> and carrying no query filter,
    /// because usage is measured per account and one person's spend has to be summable across every workspace
    /// they belong to — which a filtered entity could only answer through the <c>IgnoreQueryFilters()</c>
    /// tenancy.md prohibits. Like the platform reference sets above, it is readable before a workspace is
    /// resolved; unlike them, it is per-account data rather than shared vocabulary.
    /// </para>
    /// <para>
    /// That is safe for exactly one reason, and <see cref="AccountAiUsageEntry"/>'s remarks state it: the row
    /// holds counts and never content, and has no free-text column at all. A content-bearing column added here
    /// would make the missing filter indefensible.
    /// </para>
    /// </remarks>
    public DbSet<AccountAiUsageEntry> AccountAiUsageEntries => Set<AccountAiUsageEntry>();

    /// <remarks>
    /// The AI allowance terms in force for one account (USAGE-003), effective-dated and platform-scoped for
    /// the same reason the ledger is: a quota belongs to an account, never to a workspace and never to a
    /// membership, so a creator does not earn a fresh allowance by joining another workspace. Holds terms, not
    /// balances.
    /// </remarks>
    public DbSet<AccountAiQuota> AccountAiQuotas => Set<AccountAiQuota>();

    /// <remarks>
    /// One account's allowance window and what it has spent inside it. Freezes the terms it opened under, so
    /// changing a quota takes effect at the next roll rather than re-pricing a period the creator has already
    /// been spending against, and stores its boundaries rather than recomputing them from "now".
    /// </remarks>
    public DbSet<AccountAiQuotaPeriod> AccountAiQuotaPeriods => Set<AccountAiQuotaPeriod>();

    /// <remarks>
    /// One admitted run's hold on an account's allowance (USAGE-004), from admission through to the charge
    /// landing on its period. The row <see cref="AccountAiQuotaPeriod.Reserved"/> needs to be correct: a
    /// balance cannot be released once, settled once, or reconciled without something carrying its own
    /// identity. Counts only and platform-scoped, like the two above it.
    /// </remarks>
    public DbSet<AccountAiQuotaReservation> AccountAiQuotaReservations => Set<AccountAiQuotaReservation>();

    Guid? IWorkspaceIdSource.CurrentWorkspaceIdOrNull => workspaceContext is { IsResolved: true } context ? context.WorkspaceId : null;

    /// <remarks>
    /// Added here rather than at each <c>AddDbContext</c> call site (API host, migration service, tests) so
    /// every instance enforces <see cref="WorkspaceOwnershipInterceptor"/> and
    /// <see cref="AuditLogImmutabilityInterceptor"/> on save with nothing to forget. Additive over whatever
    /// provider/options DI already configured.
    /// </remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(
            new WorkspaceOwnershipInterceptor(),
            new AuditLogImmutabilityInterceptor(),
            new ImmutableRecordInterceptor());
    }

    /// <remarks>
    /// The workspace's brand identity facts, one per workspace: a root of the brand module. Holds no voice,
    /// tone or visual direction — those belong to the style guide (DEC-010).
    /// </remarks>
    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();

    /// <inheritdoc cref="BrandProfiles"/>
    /// <remarks>Interior to the <see cref="BrandProfile"/> aggregate, as are the two sets below.</remarks>
    public DbSet<BrandChannelDefault> BrandChannelDefaults => Set<BrandChannelDefault>();

    /// <inheritdoc cref="BrandChannelDefaults"/>
    public DbSet<BrandLink> BrandLinks => Set<BrandLink>();

    /// <inheritdoc cref="BrandChannelDefaults"/>
    /// <remarks>
    /// Write-once — <see cref="ImmutableRecordInterceptor"/> refuses every update and delete. One row per
    /// profile revision, holding the facts as they stood then.
    /// </remarks>
    public DbSet<BrandProfileRevision> BrandProfileRevisions => Set<BrandProfileRevision>();

    /// <inheritdoc cref="BrandChannelDefaults"/>
    /// <remarks>Its <c>MediaAssetId</c> has no foreign key until the media aggregate lands.</remarks>
    public DbSet<BrandAssetLink> BrandAssetLinks => Set<BrandAssetLink>();

    /// <remarks>
    /// A creator's private brand source material: a root of the brand module. Metadata and private blob
    /// pointers only — no document body, extracted text or URL is stored in SQL.
    /// </remarks>
    public DbSet<BrandSourceDocument> BrandSourceDocuments => Set<BrandSourceDocument>();

    /// <inheritdoc cref="BrandSourceDocuments"/>
    /// <remarks>
    /// Write-once — <see cref="ImmutableRecordInterceptor"/> refuses every update and delete. One row per
    /// uploaded file, exactly as it arrived.
    /// </remarks>
    public DbSet<BrandSourceDocumentVersion> BrandSourceDocumentVersions => Set<BrandSourceDocumentVersion>();

    /// <inheritdoc cref="BrandSourceDocuments"/>
    /// <remarks>Write-once record of each extraction attempt or creator correction of a version's text.</remarks>
    public DbSet<BrandSourceExtraction> BrandSourceExtractions => Set<BrandSourceExtraction>();

    /// <inheritdoc cref="BrandSourceDocuments"/>
    /// <remarks>
    /// The extraction queue itself, not a record of one: a row is written in the same save as the version it
    /// names, and the Worker claims it from here. Mutable, unlike the rest of this aggregate — a queue whose
    /// rows could not change would not be one.
    /// </remarks>
    public DbSet<BrandSourceExtractionOperation> BrandSourceExtractionOperations =>
        Set<BrandSourceExtractionOperation>();

    /// <inheritdoc cref="BrandSourceDocuments"/>
    /// <remarks>
    /// The embedding queue: one row per request to chunk and embed an extracted artifact, staged in the same
    /// save as the extraction it names and claimed by the Worker. Mutable, as a queue must be.
    /// </remarks>
    public DbSet<BrandSourceEmbeddingOperation> BrandSourceEmbeddingOperations =>
        Set<BrandSourceEmbeddingOperation>();

    /// <inheritdoc cref="BrandSourceDocuments"/>
    /// <remarks>
    /// One embedding run over one extracted artifact, and the unit of replacement for what retrieval reads.
    /// Mutable: its status is what moves a freshly built set into use and the one it replaces out.
    /// </remarks>
    public DbSet<BrandSourceChunkSet> BrandSourceChunkSets => Set<BrandSourceChunkSet>();

    /// <inheritdoc cref="BrandSourceChunkSets"/>
    /// <remarks>
    /// Interior to a set: one passage of brand source material, its offsets in the artifact it was cut from,
    /// and its vector. Derived data — rebuildable from the artifact, so replaced rather than versioned. The
    /// text is creator content and untrusted prompt content wherever it is later used (ai.md).
    /// </remarks>
    public DbSet<BrandSourceChunk> BrandSourceChunks => Set<BrandSourceChunk>();

    /// <inheritdoc cref="BrandSourceDocuments"/>
    /// <remarks>The creator's tag vocabulary for source material; separate from the recipe tags.</remarks>
    public DbSet<BrandSourceTag> BrandSourceTags => Set<BrandSourceTag>();

    /// <inheritdoc cref="BrandSourceDocuments"/>
    /// <remarks>Interior to the <see cref="BrandSourceDocument"/> aggregate.</remarks>
    public DbSet<BrandSourceDocumentTag> BrandSourceDocumentTags => Set<BrandSourceDocumentTag>();

    /// <remarks>
    /// A named guide to how the brand writes and looks: a root of the brand module, and the sole source of
    /// voice, tone and visual direction (DEC-010). What it says lives on its versions.
    /// </remarks>
    public DbSet<BrandStyleGuide> BrandStyleGuides => Set<BrandStyleGuide>();

    /// <inheritdoc cref="BrandStyleGuides"/>
    /// <remarks>
    /// Write-once — <see cref="ImmutableRecordInterceptor"/> refuses every update and delete, as it does for
    /// the four sets below. One row per edit of a guide.
    /// </remarks>
    public DbSet<BrandStyleGuideVersion> BrandStyleGuideVersions => Set<BrandStyleGuideVersion>();

    /// <inheritdoc cref="BrandStyleGuideVersions"/>
    /// <remarks>Interior to a guide version: one keyed prose section in the creator's words.</remarks>
    public DbSet<BrandStyleGuideSection> BrandStyleGuideSections => Set<BrandStyleGuideSection>();

    /// <inheritdoc cref="BrandStyleGuideVersions"/>
    /// <remarks>Interior to a guide version: one ordered do or don't.</remarks>
    public DbSet<BrandStyleGuideRule> BrandStyleGuideRules => Set<BrandStyleGuideRule>();

    /// <inheritdoc cref="BrandStyleGuideVersions"/>
    /// <remarks>Interior to a guide version: one exact source-document version it was written from.</remarks>
    public DbSet<BrandStyleGuideSourceLink> BrandStyleGuideSourceLinks => Set<BrandStyleGuideSourceLink>();

    /// <inheritdoc cref="BrandStyleGuideVersions"/>
    /// <remarks>The record that a guide version was approved; its existence is what "approved" means.</remarks>
    public DbSet<BrandStyleGuideApproval> BrandStyleGuideApprovals => Set<BrandStyleGuideApproval>();

    /// <inheritdoc cref="BrandStyleGuides"/>
    /// <remarks>The approved guide version a workspace writes with by default; at most one row per workspace.</remarks>
    public DbSet<BrandStyleGuideDefault> BrandStyleGuideDefaults => Set<BrandStyleGuideDefault>();

    /// <remarks>
    /// One derivative package per recipe and kind, and where it stands in review. The mutable root of the
    /// content module; its content lives on the immutable revisions below.
    /// </remarks>
    public DbSet<ContentProposal> ContentProposals => Set<ContentProposal>();

    /// <inheritdoc cref="ContentProposals"/>
    /// <remarks>
    /// Write-once — <see cref="ImmutableRecordInterceptor"/> refuses every update and delete. Each holds its
    /// words and the exact sources it was written against; accepted ones are retained.
    /// </remarks>
    public DbSet<ContentRevision> ContentRevisions => Set<ContentRevision>();

    /// <inheritdoc cref="ContentRevisions"/>
    /// <remarks>Write-once record of every move a proposal made.</remarks>
    public DbSet<ContentProposalTransition> ContentProposalTransitions => Set<ContentProposalTransition>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(CreatorPantryDbContext).Assembly);

        WorkspaceOwnershipConvention.Apply(builder, this);
    }
}
