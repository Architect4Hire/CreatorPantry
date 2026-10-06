using System.Collections.ObjectModel;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

/// <summary>
/// Domain rules for recipes: the invariants, the mapping from creator input to the aggregate, and the
/// decisions about what a version records. Calls the DataLayer and nothing else.
/// </summary>
public interface IRecipeBusiness
{
    /// <summary>
    /// Applies creation invariants, maps the creator's input onto a new aggregate, and writes it with its
    /// first version.
    /// </summary>
    /// <param name="input">
    /// A validated request reduced to its meaning by <see cref="CanonicalCreateRecipe.From"/>. Taking the
    /// canonical form rather than the view model keeps HTTP shapes out of Business, and means the value
    /// hashed as the idempotency fingerprint is the very value mapped here — so "same fingerprint" and "same
    /// recipe" cannot drift apart. Format rules are not re-checked; what is checked is what a validator
    /// structurally could not know.
    /// </param>
    /// <param name="yieldUnitDimension">
    /// The dimension of <c>input.YieldUnitId</c>, or <c>null</c> when no unit was named.
    /// </param>
    /// <param name="ingredientUnitDimensions">
    /// The resolved dimension of every distinct <c>MeasurementUnitId</c> named by <c>input.IngredientGroups</c>,
    /// keyed by that id. An ingredient's unit is not restricted to one dimension the way a yield unit's
    /// possibilities are enumerable or a step's temperature unit is fixed, so — unlike
    /// <see cref="RecipeInstructionStep.TemperatureUnitDimension"/>, which this layer hardcodes — Business
    /// cannot derive this fact itself and must be handed it.
    /// </param>
    /// <remarks>
    /// The dimension is a parameter rather than something looked up here, and that is a boundary decision
    /// rather than a convenience. Measurement units belong to another module, whose only permitted entry
    /// point is its facade — and Business may call nothing but its own DataLayer. So the Facade resolves it
    /// and passes the fact down. The same applies to whether the cuisine, course and technique ids exist and
    /// are active, and whether an ingredient id is one the catalogue still offers: <strong>the Facade must
    /// verify those before calling this method</strong>, because nothing below will, and an unverified id
    /// becomes a foreign-key violation at save time — a 500 where the honest answer is a 400 naming the field.
    /// </remarks>
    /// <param name="origin">
    /// Where version 1 came from. Required rather than optional: a recipe can be created two ways and
    /// <see cref="RecipeVersionSource.AiProposalAccepted"/> and the proposal's id, so the version can say
    /// can say afterwards what produced it.
    /// </param>
    Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CanonicalCreateRecipe input,
        MeasurementDimension? yieldUnitDimension,
        IReadOnlyDictionary<Guid, MeasurementDimension> ingredientUnitDimensions,
        RecipeVersionOrigin origin,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one recipe as a client sees it.
    /// </summary>
    /// <returns>
    /// The recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>.
    /// </returns>
    /// <remarks>
    /// The not-found decision is made here rather than above, because whether a resource exists for this
    /// caller is a resource-level authorization question and those stay in Business. It answers the same way
    /// for a recipe that was never created and for one belonging to another workspace — not as a convenience,
    /// but because the layer beneath cannot tell them apart and this layer must not be able to either
    /// (tenancy.md).
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the archived content of one exact, explicitly named version — never "current" — for a caller with
    /// no use for the recipe as it stands today.
    /// </summary>
    /// <returns>
    /// The snapshot, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> (the recipe is not
    /// visible here, indistinguishable from one that was never created) or
    /// <see cref="RecipeErrorCodes.VersionNotFound"/> (the recipe is visible but names no such version).
    /// </returns>
    /// <remarks>
    /// Named by version id rather than by number, unlike every calculation preview in this module: its one
    /// caller is the AI worker, which pins a version by <c>AiOperation.RecipeVersionId</c> — a permanent id, not
    /// a number a creator typed — so a proposal computed against it can be told apart from a recipe that has
    /// since moved on.
    /// </remarks>
    Task<OperationResult<RecipeSnapshotServiceModel>> GetSnapshotAsync(
        Guid recipeId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a creator's partial edit and captures the version that records it.
    /// </summary>
    /// <param name="recipeId">The recipe to edit, from the route.</param>
    /// <param name="patch">
    /// A validated request reduced to its meaning by <see cref="CanonicalRecipePatch.From"/>. Each field
    /// still carries whether it was submitted, because that — not its value — is what decides whether it is
    /// being changed, cleared, or left alone.
    /// </param>
    /// <param name="submittedYieldUnitDimension">
    /// The dimension of the yield unit <em>if the request submitted one</em>, and <c>null</c> otherwise —
    /// including when the request submitted <c>null</c> to clear the unit. When the unit was not submitted,
    /// the recipe's stored dimension stands, because the unit is not changing.
    /// </param>
    /// <param name="ingredientUnitDimensions">
    /// The resolved dimension of every distinct <c>MeasurementUnitId</c> the patch's ingredient list names,
    /// when it submitted one at all — see <see cref="CreateAsync"/> for why Business cannot derive this
    /// itself. Empty, and unused, when <c>patch.IngredientGroups</c> was not submitted.
    /// </param>
    /// <returns>
    /// The edited recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.RecipeInvalidRequest"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Invariants are checked against the merged recipe, not against the request.</strong> A patch
    /// names only part of a recipe, so "does the total time still make sense" and "does this yield unit
    /// still have a quantity beside it" cannot be answered from the body — a request that clears the yield
    /// quantity is only wrong because of a unit it never mentioned. This is the one real difference from
    /// <see cref="CreateAsync"/>, where a complete request let the edge answer some of it.
    /// </para>
    /// <para>
    /// <strong>An edit that changes nothing writes nothing.</strong> Not an optimisation: a version that
    /// records no change is noise in a history a creator reads, and stamping <c>UpdatedAt</c> for it would
    /// invalidate every concurrency token their collaborators hold for no reason.
    /// </para>
    /// <para>
    /// As on <see cref="CreateAsync"/>, <strong>the caller must have verified the submitted reference ids
    /// first</strong>: nothing below here will, and an unverified id becomes a foreign-key violation at save
    /// time — a 500 where the honest answer is a 400 naming the field.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
        Guid recipeId,
        CanonicalRecipePatch patch,
        MeasurementDimension? submittedYieldUnitDimension,
        IReadOnlyDictionary<Guid, MeasurementDimension> ingredientUnitDimensions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies the changes a creator accepted from an AI proposal, capturing one version that records where
    /// they came from.
    /// </summary>
    /// <param name="expectedVersionId">
    /// The version the proposal was computed against, which must still be the recipe's current version.
    /// </param>
    /// <param name="aiProposalId">
    /// The proposal the creator accepted. Recorded on the version beside
    /// <see cref="RecipeVersionSource.AiProposalAccepted"/>, so a version a model helped write stays
    /// identifiable afterwards.
    /// </param>
    /// <param name="changes">
    /// The accepted changes and only those. Whoever calls this has already decided which ones the creator took;
    /// nothing here re-reads a proposal or a disposition, because this module does not own either.
    /// </param>
    /// <returns>
    /// The edited recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.RecipeInvalidRequest"/>, <see cref="RecipeErrorCodes.RecipeConflict"/> —
    /// which is also how a stale source is reported — or
    /// <see cref="RecipeErrorCodes.RecipeArchivedConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Every rule an ordinary edit obeys applies here unchanged.</strong> The accepted changes become a
    /// <see cref="CanonicalRecipePatch"/> and travel the same merge path a creator's own edit travels — the same
    /// invariant checks, the same archived-recipe refusal, the same instruction reconciliation, the same single
    /// version. A model cannot reach a recipe by a route that skips any of it.
    /// </para>
    /// <para>
    /// <strong>Accepting changes that amount to nothing writes nothing</strong>, for the reason
    /// <see cref="UpdateAsync"/> gives. A proposal whose accepted changes all match the recipe's current values
    /// is a decision worth recording on the proposal, but not a version worth putting in a history a creator
    /// reads.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> ApplyProposedChangesAsync(
        Guid recipeId,
        Guid expectedVersionId,
        Guid aiProposalId,
        IReadOnlyList<ProposedRecipeChange> changes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the workspace's recipes, as the client reads it: summaries, the cursor that resumes
    /// them, and the total when it was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No failure case, and so no <c>OperationResult</c>. Every way this request can be refused — an unparseable
    /// filter, a cursor from another scope — has already been settled above, by the validator and the query
    /// factory, before a criteria existed to hand down here. A workspace with no recipes and filters that match
    /// nothing are both an empty page, not an error, and a search must not be able to say "not found" about a
    /// workspace the caller is demonstrably a member of.
    /// </para>
    /// <para>
    /// No role gate, for the same reason <see cref="GetDetailAsync"/> gives: <c>Viewer</c> is the lowest role, so
    /// a resolved workspace context already is the authorization.
    /// </para>
    /// </remarks>
    Task<RecipeSearchPageServiceModel> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a recipe's history, newest first, as the client reads it: version metadata and the
    /// cursor that resumes it.
    /// </summary>
    /// <returns>
    /// The page, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> — the single answer for a
    /// recipe that does not exist and one that belongs to another workspace.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A failure case, unlike <see cref="SearchAsync"/>, and the difference is which question is being asked.
    /// A search asks about a workspace the caller demonstrably belongs to, so an empty answer is an answer.
    /// This asks about one named recipe, and whether that recipe exists is precisely what must not be
    /// disclosed.
    /// </para>
    /// <para>
    /// No role gate, for the reason <see cref="GetDetailAsync"/> gives: <c>Viewer</c> is the lowest role, so a
    /// resolved workspace context already is the authorization. Someone who may read a recipe may read how it
    /// came to say what it says.
    /// </para>
    /// <para>
    /// <strong>Read-only, necessarily.</strong> These rows are immutable and this seam has no write path — a
    /// correction to the history is a new version, written by an edit or a restore, never a change here.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeVersionHistoryPageResult>> GetVersionHistoryAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compares two of one recipe's versions and returns what differs between them.
    /// </summary>
    /// <param name="recipeId">The recipe whose versions to compare, resolved from the route.</param>
    /// <param name="fromVersionNumber">The version reported as the <c>from</c> side. Need not be the lower number.</param>
    /// <param name="toVersionNumber">The version reported as the <c>to</c> side.</param>
    /// <returns>
    /// The comparison, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> when the recipe is
    /// not visible here, or <see cref="RecipeErrorCodes.VersionNotFound"/> when one of the numbers names no
    /// version of it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Nothing is generated and nothing is written.</strong> The answer is
    /// <see cref="RecipeComparer"/>'s arithmetic over two documents this server read for itself, which is the
    /// same requirement REC-008 and AIREC-GR-002 state from either side. Comparing two versions leaves no
    /// record that it happened.
    /// </para>
    /// <para>
    /// <strong>The two numbers may be equal</strong>, and the honest answer is a comparison with nothing in
    /// it. The single row the query returns is read as both sides rather than reported as a missing version.
    /// </para>
    /// <para>
    /// <strong>An unreadable stored document raises rather than returns.</strong>
    /// <see cref="RecipeSnapshotDocument.MinimumReadableSchemaVersion"/> guarantees that documents written by
    /// older builds stay readable, so the only way here is a document written by a <em>newer</em> build than
    /// the one serving the request — a rolled-back deployment reading forward. That is a server fault and a
    /// 500 is the truthful status; giving it an error code would invite a client to handle a condition it
    /// cannot do anything about.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid recipeId,
        int fromVersionNumber,
        int toVersionNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic scaling preview for one explicit recipe version. Read-only: nothing here is
    /// persisted, and the recipe is unchanged by having been scaled (ING-003).
    /// </summary>
    /// <param name="recipeId">The recipe to read from, resolved from the route.</param>
    /// <param name="sourceVersionNumber">The version to scale — required, never "whatever is current".</param>
    /// <param name="request">A multiplier or a target yield, already translated from the submitted decimals.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.VersionNotFound"/>, or <see cref="RecipeErrorCodes.ScalingInvalidRequest"/>
    /// when <c>RecipeScalingCalculator</c> itself could not resolve a factor (CALC-005).
    /// </returns>
    Task<OperationResult<RecipeScalingResultServiceModel>> ScaleAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeScalingRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic unit-conversion preview in the context of one explicit recipe version.
    /// Read-only: nothing here is persisted, and the recipe is unchanged by having a quantity converted
    /// (ING-004).
    /// </summary>
    /// <param name="recipeId">The recipe to check visibility against, resolved from the route.</param>
    /// <param name="sourceVersionNumber">The version this preview is asked in the context of — required, never "whatever is current".</param>
    /// <param name="request">The quantity to convert, with both units already resolved by the Facade.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.VersionNotFound"/>, or <see cref="RecipeErrorCodes.UnitConversionInvalidRequest"/>
    /// when <c>UnitConversionCalculator</c> could not bridge the two units (ING-004).
    /// </returns>
    Task<OperationResult<RecipeUnitConversionResultServiceModel>> ConvertUnitsAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeUnitConversionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic temperature-conversion preview in the context of one explicit recipe version.
    /// Read-only: nothing here is persisted, and the recipe is unchanged by having a temperature converted
    /// (CALC-003).
    /// </summary>
    /// <param name="recipeId">The recipe to check visibility against, resolved from the route.</param>
    /// <param name="sourceVersionNumber">The version this preview is asked in the context of — required, never "whatever is current".</param>
    /// <param name="model">The value, scales, precision, and pass-through context, already shape-validated.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.VersionNotFound"/>. <c>TemperatureConversionCalculator</c> never fails once
    /// its inputs are well formed, so no calculation-level error code is ever returned here.
    /// </returns>
    Task<OperationResult<RecipeTemperatureConversionResultServiceModel>> ConvertTemperatureAsync(
        Guid recipeId,
        int sourceVersionNumber,
        ConvertTemperatureViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic yield-reconciliation preview in the context of one explicit recipe version.
    /// Read-only: nothing here is persisted, and the recipe is unchanged by having its yield recalculated
    /// (ING-005).
    /// </summary>
    /// <param name="recipeId">The recipe to check visibility against, resolved from the route.</param>
    /// <param name="sourceVersionNumber">The version this preview is asked in the context of — required, never "whatever is current".</param>
    /// <param name="request">The creator's explicit batch yield, serving count, serving size, and pan capacity.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.VersionNotFound"/>, or <see cref="RecipeErrorCodes.YieldRecalculationInvalidRequest"/>
    /// on the rare non-positive value that reaches <c>YieldReconciliationCalculator</c> itself.
    /// </returns>
    /// <remarks>
    /// The dimension the four values are read in is resolved here, from the source version's own stored
    /// yield unit — never invented, and never a value the request could name itself (ING-005's restriction
    /// against inventing a missing serving definition applies to the dimension exactly as it does to a pan
    /// geometry).
    /// </remarks>
    Task<OperationResult<RecipeYieldReconciliationResultServiceModel>> RecalculateYieldAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeYieldReconciliationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic display-normalization preview in the context of one explicit recipe version.
    /// Read-only and presentation-only: nothing here is persisted, and no canonical quantity or recipe version
    /// changes (ING-006).
    /// </summary>
    /// <param name="recipeId">The recipe to check visibility against, resolved from the route.</param>
    /// <param name="sourceVersionNumber">The version this preview is asked in the context of — required, never "whatever is current".</param>
    /// <param name="request">The value (and optional range upper bound) to render, with the unit already resolved by the Facade.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.VersionNotFound"/>. <c>QuantityDisplayCalculator</c> never fails once its
    /// inputs are well formed, so no calculation-level error code is ever returned here.
    /// </returns>
    Task<OperationResult<RecipeQuantityDisplayResultServiceModel>> NormalizeDisplayAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeQuantityDisplayRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Puts a recipe back to what one of its versions said, and captures the new version that records having
    /// done so.
    /// </summary>
    /// <param name="recipeId">The recipe to restore, from the route.</param>
    /// <param name="versionNumber">Which version's content to put back, from the route.</param>
    /// <param name="request">
    /// The state the restore was composed against, and why the creator did it. Carries no content: a restore
    /// takes all of it from the archive.
    /// </param>
    /// <returns>
    /// The restored recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.VersionNotFound"/> or <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>History is appended to, never rewritten.</strong> The version being restored is read and left
    /// exactly where it is — it does not become current, is not renumbered, and is not deleted. What the
    /// creator gets is a <em>new</em> current version whose content was copied from it, whose parent is the
    /// version it replaced, and whose <c>RestoredFromVersionId</c> names where the content came from. Those
    /// are two different ancestors and the requirement needs both.
    /// </para>
    /// <para>
    /// <strong>An unknown version number is answered before the concurrency token is checked.</strong> Both
    /// are refusals, but only one is worth re-reading for: version 99 will not appear on a second look, so
    /// telling that caller to reload would send them round a loop that cannot end. Nothing is disclosed by
    /// the ordering — see <see cref="RecipeErrorCodes.VersionNotFound"/>, which is returned only to a caller
    /// already shown that this recipe exists.
    /// </para>
    /// <para>
    /// <strong>A restore that would change nothing writes nothing</strong>, for the reason
    /// <see cref="UpdateAsync"/> gives, and with a second benefit here: it makes a creator who submits the
    /// same restore twice — or a client with no idempotency key that retries after re-reading — receive the
    /// recipe rather than a duplicate version.
    /// </para>
    /// <para>
    /// <strong>The archived vocabulary ids are not re-verified.</strong> A cuisine, course, technique or unit
    /// that has since been retired is still restored, exactly as <see cref="UpdateAsync"/> declines to
    /// re-check a reference an edit never mentioned: the creator did not name these ids, cannot do anything
    /// about a catalogue decision, and refusing their own history would be an answer with no remedy. Nothing
    /// deletes a reference row, so the foreign keys hold. Tags are the exception, because their vocabulary is
    /// the workspace's own — see <c>RecipeSnapshotReconciler</c>.
    /// </para>
    /// <para>
    /// <strong>An unreadable stored document raises rather than returns</strong>, for the reason
    /// <see cref="CompareVersionsAsync"/> gives: the only way there is a document written by a newer build
    /// than the one serving the request, which is a server fault and truthfully a 500.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> RestoreVersionAsync(
        Guid recipeId,
        int versionNumber,
        CanonicalRestoreRecipeVersion request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Copies one version of a recipe into a new, independent recipe with its own first version.
    /// </summary>
    /// <param name="recipeId">The recipe to copy from, resolved from the route.</param>
    /// <param name="request">The copy's title, and which version to copy.</param>
    /// <returns>
    /// The new recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> when the source is
    /// not visible here, or <see cref="RecipeErrorCodes.VersionNotFound"/> when the number names no version
    /// of it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The copy is canonical source material, not a derivative.</strong> Nothing links the two
    /// recipes' content: editing either does nothing to the other, and the copy has its own version history
    /// beginning at 1. <c>Recipe.DuplicatedFromVersionId</c> records where the content came from, which is
    /// provenance rather than dependence — content.md's derivative rules govern blog drafts and social copy
    /// generated <em>from</em> a recipe, and a recipe is not one of those.
    /// </para>
    /// <para>
    /// <strong>The copy is always a Draft</strong>, whatever the source said. A copy of a finished recipe is
    /// not itself finished — it exists to be taken somewhere else — and duplicating an archived recipe must
    /// not produce an archived copy that the creator then has to go and find.
    /// </para>
    /// <para>
    /// <strong>Every identity is minted fresh</strong>, down to each ingredient line and step. Sharing a
    /// child id between two recipes is not merely untidy: the ids are primary keys, and a diff matches
    /// content by them.
    /// </para>
    /// <para>
    /// <strong>The archived content is not re-validated</strong>, for the reason <see cref="UpdateAsync"/>
    /// declines to re-check a reference an edit never mentioned. A recipe written before a limit tightened,
    /// or naming a cuisine since retired, is still the creator's recipe; refusing to copy it would be an
    /// answer with no remedy. Nothing submitted this content, so there is nothing here a client could have
    /// got wrong.
    /// </para>
    /// <para>
    /// <strong>Tags are copied by resolving the archive's ids back to names</strong> and handing them to the
    /// create, which owns tag attachment for a new recipe. An id whose vocabulary row is gone resolves to
    /// nothing and is simply not applied.
    /// </para>
    /// </remarks>
    Task<OperationResult<CreatedRecipeServiceModel>> DuplicateAsync(
        Guid recipeId,
        CanonicalDuplicateRecipe request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shelves a recipe: moves it to <see cref="RecipeStatus.Archived"/>, records the move, and leaves
    /// everything it contains exactly where it is.
    /// </summary>
    /// <param name="recipeId">The recipe to archive, from the route.</param>
    /// <param name="actorUserId">The authenticated caller, for the audit entry. Never from a request field.</param>
    /// <param name="expectedConcurrencyToken">The state the command was composed against.</param>
    /// <returns>
    /// The recipe as it now stands, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Not a delete, and not a soft delete either.</strong> Every version, tag, media link,
    /// ingredient line and step survives untouched, and the recipe stays readable by id — what changes is
    /// that it drops out of the default library listing and stops accepting content changes. Reading it,
    /// listing its history, comparing its versions and copying it all keep working.
    /// </para>
    /// <para>
    /// <strong>Archiving an archived recipe is a success that does nothing.</strong> No audit entry, no
    /// stamped <c>UpdatedAt</c>, no invalidated token: the creator asked for a state the recipe is already
    /// in, and the honest answer is the recipe. That also makes the command safe to retry without an
    /// idempotency key, which is why it has none.
    /// </para>
    /// <para>
    /// <strong>No version is written.</strong> A version records what a recipe said, and this changes
    /// nothing it says — see <see cref="IRecipeDataLayer.TryTransitionAsync"/>.
    /// </para>
    /// <para>
    /// <strong>Since TESTRUN-005 this is one move of the editorial machine</strong>, not a command beside it:
    /// it runs through <see cref="TransitionAsync"/>, writes a <c>RecipeStatusTransition</c> like every other
    /// move, and is refused from a state <see cref="RecipeStatusTransitions"/> has no archive rule for. The
    /// route, the role bar and the answers a caller gets are unchanged.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Brings a recipe back from the archive, as a <see cref="RecipePolicy.UnarchivedStatus"/>.
    /// </summary>
    /// <inheritdoc cref="ArchiveAsync" path="/param"/>
    /// <returns>
    /// The recipe as it now stands, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="ArchiveAsync"/> in every respect, including that unarchiving a recipe which
    /// is not archived succeeds and does nothing.
    /// </para>
    /// <para>
    /// <strong>It returns to Draft, not to whatever it was before.</strong>
    /// <see cref="RecipePolicy.UnarchivedStatus"/> records why.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a recipe to another editorial state, if <see cref="RecipeStatusTransitions"/> has that move and
    /// the caller may make it (TESTRUN-005).
    /// </summary>
    /// <param name="target">The state to move to. From a route or a request body, never from the recipe.</param>
    /// <param name="reason">
    /// Why, in the caller's own words. Required for the moves
    /// <see cref="RecipeStatusTransitionRule.RequiresReason"/> names and optional for the rest.
    /// </param>
    /// <param name="readiness">
    /// A readiness evaluation of this recipe, for the move that needs one, and <c>null</c> otherwise. Gathered
    /// by the facade, because the evaluation crosses module boundaries that Business may not.
    /// </param>
    /// <param name="actorUserId">The authenticated caller, for the audit entry. Never from a request field.</param>
    /// <param name="expectedConcurrencyToken">The state the command was composed against.</param>
    /// <returns>
    /// The recipe as it now stands, or a failure carrying
    /// <see cref="RecipeErrorCodes.RecipeNotFound"/>, <see cref="RecipeErrorCodes.RecipeConflict"/>,
    /// <see cref="RecipeErrorCodes.TransitionInvalidRequest"/>,
    /// <see cref="RecipeErrorCodes.TransitionForbidden"/> or
    /// <see cref="RecipeErrorCodes.TransitionBlockedConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The only thing that moves <c>Recipe.Status</c>.</strong> The archive and restore commands run
    /// through it, an edit's reopen runs through it, and nothing else touches the column — the edit seam
    /// refuses a submitted status that differs from the recipe's own, and a version restore no longer puts one
    /// back. That is what makes the transition table a complete account of a recipe's editorial life rather
    /// than a partial one.
    /// </para>
    /// <para>
    /// <strong>Asking for the state the recipe is already in succeeds and does nothing.</strong> Not an
    /// invalid jump — a jump goes somewhere — but a repeat, which is the ordinary shape of a retried command
    /// and the behaviour REC-006 documented for archiving. It matters most for the approval: a replayed
    /// approval must not write a second version or a second transition, and answering "already there" is how
    /// that is guaranteed without an idempotency key.
    /// </para>
    /// <para>
    /// <strong>The readiness gate is checked against this recipe at this moment.</strong> A supplied
    /// evaluation whose concurrency token no longer matches the recipe is refused as a conflict rather than
    /// honoured: an evaluation of content that has since changed is not a fresh evaluation, whatever it says.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
        Guid recipeId,
        RecipeStatus target,
        string? reason,
        RecipeReadinessServiceModel? readiness,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Names <paramref name="recipeIds"/> for a caller in another module, as id and title only.
    /// </summary>
    /// <remarks>
    /// Maps straight to the published <see cref="RecipeLinkCandidateServiceModel"/> because there is nothing to
    /// decide: an id the resolved workspace cannot see is absent, which is the repository's doing and the only
    /// behaviour this needs. Order follows the ids given, so a caller that passed a sorted list gets one back.
    /// </remarks>
    Task<IReadOnlyList<RecipeLinkCandidateServiceModel>> ListTitlesAsync(
        IReadOnlyList<Guid> recipeIds, CancellationToken cancellationToken);
}

internal sealed class RecipeBusiness(
    IRecipeDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IRecipeBusiness
{
    public async Task<IReadOnlyList<RecipeLinkCandidateServiceModel>> ListTitlesAsync(
        IReadOnlyList<Guid> recipeIds, CancellationToken cancellationToken)
    {
        var found = await dataLayer.ListTitlesAsync(recipeIds, cancellationToken);
        var byId = found.ToDictionary(row => row.Id, row => row.Title);

        // Ordered by the ids the caller gave rather than by what the database returned, so a caller that sorted
        // its links keeps that order. Anything the workspace cannot see is skipped rather than named.
        return [.. recipeIds
            .Where(byId.ContainsKey)
            .Select(id => new RecipeLinkCandidateServiceModel(id, byId[id]))];
    }

    public async Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CanonicalCreateRecipe input,
        MeasurementDimension? yieldUnitDimension,
        IReadOnlyDictionary<Guid, MeasurementDimension> ingredientUnitDimensions,
        RecipeVersionOrigin origin,
        CancellationToken cancellationToken)
    {
        if (Validate(input, yieldUnitDimension) is { } error)
        {
            return OperationResult<CreatedRecipeServiceModel>.Failure(error);
        }

        // One read of the clock for the whole operation, so the recipe, its version and any tag born with it
        // all agree on when this happened.
        var now = clock.UtcNow;

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),

            // WorkspaceId is deliberately not set. The ownership interceptor stamps it from the resolved
            // context; assigning it here is the defect tenancy.md names.
            Title = input.Title,
            Description = input.Description,
            Headnote = input.Headnote,
            Notes = input.Notes,
            StorageNotes = input.StorageNotes,
            AttributionText = input.AttributionText,
            SourceUrl = input.SourceUrl,

            CuisineId = input.CuisineId,
            CourseId = input.CourseId,
            PrimaryTechniqueId = input.PrimaryTechniqueId,

            PrepTimeMinutes = input.PrepTimeMinutes,
            CookTimeMinutes = input.CookTimeMinutes,
            RestTimeMinutes = input.RestTimeMinutes,
            TotalTimeMinutes = input.TotalTimeMinutes,

            YieldText = input.YieldText,
            YieldQuantity = input.YieldQuantity,
            YieldUnitId = input.YieldUnitId,
            // Derived, never accepted from the request: a client able to send this could contradict the unit
            // it names, and the composite foreign key would then be pinning a lie.
            YieldUnitDimension = input.YieldUnitId is null ? null : yieldUnitDimension,

            ServingCount = input.ServingCount,
            ServingSize = input.ServingSize,

            Status = input.Status,

            // The authenticated creator, from the resolved membership. Never from the request body.
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var index = 0; index < input.Instructions.Count; index++)
        {
            recipe.InstructionGroups.Add(BuildInstructionGroup(recipe, input.Instructions[index], index));
        }

        for (var index = 0; index < input.IngredientGroups.Count; index++)
        {
            recipe.IngredientGroups.Add(BuildIngredientGroup(recipe, input.IngredientGroups[index], index, ingredientUnitDimensions));
        }

        var version = new RecipeVersionFacts(
            origin.Source,

            // Draft, always. This used to derive from the recipe's status, which was right while a create
            // could ask for Ready; since TESTRUN-005 only the approval transition mints a ready version, and
            // it is the one write that may — a create cannot reach Approved at all
            // (SettableRecipeStatusViewModel), so a conditional here would be a branch that can never take
            // its other path.
            RecipeVersionReadiness.Draft,

            // No reason. RecipeVersion.Reason is documented as optional — "a routine save has no reason" —
            // and "this is the first version" is already carried by the version number. Inventing a sentence
            // here would put user-visible English in the domain that nothing can localize.
            Reason: null,
            AiProposalId: origin.AiProposalId);

        var created = await dataLayer.CreateAsync(recipe, version, input.Tags, cancellationToken);

        // Mapping the persistence result to the application's own output shape is Business.s job (backend.md);
        // the layers above never see what the DataLayer returned.
        return OperationResult<CreatedRecipeServiceModel>.Success(new CreatedRecipeServiceModel(
            created.RecipeId,
            recipe.Title,
            recipe.Status,
            created.VersionId,
            created.VersionNumber,
            recipe.CreatedAt));
    }

    public async Task<RecipeSearchPageServiceModel> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var (rows, hasMore, total) = await dataLayer.SearchAsync(criteria, cancellationToken);

        // The cursor is minted here, not in the repository: it is bound to the resource and filters it was
        // issued for, and a repository is handed a predicate rather than a route. PageBuilder is the shared
        // helper so that no module can drift on how a page ends.
        var page = PageBuilder.Build(rows, hasMore, criteria.Scope, ToSummary);

        return new RecipeSearchPageServiceModel(page.Items, page.NextCursor, total);
    }

    /// <summary>
    /// Maps a repository row to the wire shape, dropping what must not leave the server.
    /// </summary>
    /// <remarks>
    /// The membership ids and the sort key the row carries are both absent from the result, for different
    /// reasons: the membership columns never leave the server (tenancy.md), and the sort key exists only so the
    /// row can say what it was ordered by, which is a fact about the query rather than about the recipe.
    /// </remarks>
    private static RecipeSummaryServiceModel ToSummary(RecipeSummaryRecord row) =>
        new(row.Id,
            row.Title,
            row.Description,
            row.Status,
            row.CuisineId,
            row.CourseId,
            row.CreatedAt,
            row.UpdatedAt,
            row.LatestVersionNumber,
            row.LatestVersionReadiness,
            row.HasUnmatchedIngredients);

    public async Task<OperationResult<RecipeVersionHistoryPageResult>> GetVersionHistoryAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken)
    {
        var page = await dataLayer.ListVersionsAsync(criteria, cancellationToken);

        if (page is not { } history)
        {
            // The recipe is not visible. Answered identically to one that was never created, because the layer
            // beneath cannot tell them apart and this one must not be able to either (tenancy.md).
            return NotFound<RecipeVersionHistoryPageResult>();
        }

        // The cursor is minted here rather than in the repository, for the reason PageBuilder exists: it is
        // bound to the recipe and workspace it was issued for, which a repository is never told.
        //
        // The authorship travels beside the page rather than inside it: naming a membership needs two other
        // modules, and this layer may call neither. See RecipeVersionHistoryPageResult.
        return OperationResult<RecipeVersionHistoryPageResult>.Success(new RecipeVersionHistoryPageResult(
            PageBuilder.Build(history.Rows, history.HasMore, criteria.Scope, ToHistoryEntry),
            history.Rows.ToDictionary(row => row.Id, row => row.CreatedByMembershipId)));
    }

    /// <summary>
    /// Maps a history row to the wire shape, dropping the membership id that must not leave the server.
    /// </summary>
    /// <remarks>
    /// The same rule the search mapping follows, with a more visible consequence: a history entry cannot name
    /// who wrote the version. See <see cref="RecipeVersionHistoryServiceModel"/> — the id is read for the
    /// future, not published today.
    /// </remarks>
    public async Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid recipeId,
        int fromVersionNumber,
        int toVersionNumber,
        CancellationToken cancellationToken)
    {
        var rows = await dataLayer.FindVersionSnapshotsAsync(
            recipeId, fromVersionNumber, toVersionNumber, cancellationToken);

        if (rows is null)
        {
            // The recipe is not visible. Answered identically to one that was never created (tenancy.md).
            return NotFound<RecipeVersionComparisonServiceModel>();
        }

        var from = rows.SingleOrDefault(row => row.VersionNumber == fromVersionNumber);
        var to = rows.SingleOrDefault(row => row.VersionNumber == toVersionNumber);

        // Both sides are named, so a creator who mistyped one number is told which one. Reporting only the
        // first would send them round the loop twice when they mistyped both.
        var missing = new List<(string Field, string Error)>();
        if (from is null)
        {
            missing.Add(("from", $"This recipe has no version {fromVersionNumber}."));
        }

        if (to is null && toVersionNumber != fromVersionNumber)
        {
            missing.Add(("to", $"This recipe has no version {toVersionNumber}."));
        }

        if (missing.Count > 0)
        {
            return OperationResult<RecipeVersionComparisonServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound, "Those versions could not be compared.", missing));
        }

        // Equal numbers read the one row as both sides rather than as a missing version: comparing a version
        // with itself is a legitimate question whose answer is "nothing changed".
        to ??= from;

        return OperationResult<RecipeVersionComparisonServiceModel>.Success(new RecipeVersionComparisonServiceModel
        {
            From = ToComparisonSide(from!),
            To = ToComparisonSide(to!),

            // Deserialized here rather than in the repository: reading stored text as a document is
            // translation, and deciding what an unreadable one means is a domain decision rather than a
            // persistence one.
            Comparison = RecipeComparer.Compare(
                RecipeSnapshotSerializer.Deserialize(from!.Document),
                RecipeSnapshotSerializer.Deserialize(to!.Document)),
        });
    }

    public async Task<OperationResult<RecipeScalingResultServiceModel>> ScaleAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeScalingRequest request,
        CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindCalculationSourceAsync(
            recipeId, sourceVersionNumber, cancellationToken);

        if (!visible)
        {
            // The recipe is not visible. Answered identically to one that was never created (tenancy.md).
            return NotFound<RecipeScalingResultServiceModel>();
        }

        if (source is null)
        {
            return OperationResult<RecipeScalingResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That recipe cannot be scaled as described.",
                [(SourceVersionNumberField, $"This recipe has no version {sourceVersionNumber}.")]));
        }

        // Deserialized here rather than in the repository, for the reason CompareVersionsAsync gives: reading
        // stored text as a document is translation, and deciding what an unreadable one means is a domain
        // decision.
        var document = RecipeSnapshotSerializer.Deserialize(source.Document);

        // DisplayPrecision is left null on every line rather than resolved from the Measurement module: this
        // read-only preview has nothing to gain from a second module's lookup that RecipeScalingCalculator
        // does not already handle by falling back to its own documented default (7.6), and Business may call
        // its own DataLayer only — resolving units belongs to the Facade, which does not yet have what it
        // would need to know which ones to ask for before this method runs.
        var lines = document.IngredientGroups
            .SelectMany(group => group.Ingredients)
            .Select(ingredient => new RecipeIngredientScalingInput
            {
                Id = ingredient.Id,
                DisplayText = ingredient.DisplayText,
                Quantity = ingredient.Quantity,
                QuantityUpper = ingredient.QuantityUpper,
                ScalingBehavior = ingredient.ScalingBehavior,
                MeasurementUnitDimension = ingredient.MeasurementUnitDimension,
                DisplayPrecision = null,
            })
            .ToList();

        var recipeYieldQuantity = document.Recipe.YieldQuantity is { } yield
            ? Quantity.FromDecimal(yield)
            : (Quantity?)null;

        var outcome = RecipeScalingCalculator.Scale(request, recipeYieldQuantity, lines);

        if (outcome.Error is { } scalingError)
        {
            return OperationResult<RecipeScalingResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.ScalingInvalidRequest,
                "That recipe could not be scaled as described.",
                [ScalingErrorField(scalingError)]));
        }

        return OperationResult<RecipeScalingResultServiceModel>.Success(
            new RecipeScalingResultServiceModel(sourceVersionNumber, outcome.Preview!));
    }

    public async Task<OperationResult<RecipeUnitConversionResultServiceModel>> ConvertUnitsAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeUnitConversionRequest request,
        CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindCalculationSourceAsync(
            recipeId, sourceVersionNumber, cancellationToken);

        if (!visible)
        {
            return NotFound<RecipeUnitConversionResultServiceModel>();
        }

        if (source is null)
        {
            return OperationResult<RecipeUnitConversionResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                CannotConvertUnits,
                [(SourceVersionNumberField, $"This recipe has no version {sourceVersionNumber}.")]));
        }

        // No density supplied: this route exposes same-dimension conversion only (ING-004's restriction — no
        // cross-dimension conversion without an approved density, and nothing in the Ingredients module yet
        // resolves one). A Mass/Volume request answers MissingDensity below rather than converting.
        var outcome = UnitConversionCalculator.Convert(request.Quantity, request.FromUnit, request.ToUnit);

        if (outcome.Error is { } conversionError)
        {
            return OperationResult<RecipeUnitConversionResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.UnitConversionInvalidRequest,
                CannotConvertUnits,
                [UnitConversionErrorField(conversionError)]));
        }

        return OperationResult<RecipeUnitConversionResultServiceModel>.Success(
            new RecipeUnitConversionResultServiceModel(sourceVersionNumber, outcome.Result!));
    }

    private const string CannotConvertUnits = "That quantity could not be converted as described.";

    private static (string Field, string Error) UnitConversionErrorField(UnitConversionError error) => error switch
    {
        UnitConversionError.IncompatibleUnits =>
            (ToUnitIdField, "These units cannot be converted between each other."),
        UnitConversionError.MissingDensity =>
            (ToUnitIdField, "This conversion needs an ingredient-specific density reference, which is not yet available."),
        UnitConversionError.TemperatureNotSupported =>
            (ToUnitIdField, "Temperature units use the temperature calculation instead."),
        _ => (ToUnitIdField, "That request could not be resolved."),
    };

    private const string ToUnitIdField = "toUnitId";

    public async Task<OperationResult<RecipeTemperatureConversionResultServiceModel>> ConvertTemperatureAsync(
        Guid recipeId,
        int sourceVersionNumber,
        ConvertTemperatureViewModel model,
        CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindCalculationSourceAsync(
            recipeId, sourceVersionNumber, cancellationToken);

        if (!visible)
        {
            return NotFound<RecipeTemperatureConversionResultServiceModel>();
        }

        if (source is null)
        {
            return OperationResult<RecipeTemperatureConversionResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That temperature could not be converted as described.",
                [(SourceVersionNumberField, $"This recipe has no version {sourceVersionNumber}.")]));
        }

        var result = TemperatureConversionCalculator.Convert(
            model.Value, model.FromScale, model.ToScale, model.Precision, model.OvenModeContext, model.SafetyNote);

        return OperationResult<RecipeTemperatureConversionResultServiceModel>.Success(
            new RecipeTemperatureConversionResultServiceModel(sourceVersionNumber, result));
    }

    public async Task<OperationResult<RecipeYieldReconciliationResultServiceModel>> RecalculateYieldAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeYieldReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindCalculationSourceAsync(
            recipeId, sourceVersionNumber, cancellationToken);

        if (!visible)
        {
            return NotFound<RecipeYieldReconciliationResultServiceModel>();
        }

        if (source is null)
        {
            return OperationResult<RecipeYieldReconciliationResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That yield could not be recalculated as described.",
                [(SourceVersionNumberField, $"This recipe has no version {sourceVersionNumber}.")]));
        }

        // Deserialized only for the recipe's own stored yield dimension — see the interface remarks on why
        // that is resolved here rather than accepted from the request. Falls back to Count, a neutral choice
        // that never invents a geometry: it only decides whether pan/fill-ratio comparison is even attempted,
        // which requires Volume specifically.
        var document = RecipeSnapshotSerializer.Deserialize(source.Document);
        var dimension = document.Recipe.YieldUnitDimension ?? MeasurementDimension.Count;

        var input = new YieldReconciliationInput(
            dimension,
            request.DisplayPrecision ?? RecipeScalingPolicy.DefaultDisplayPrecision,
            request.BatchYield,
            request.ServingCount,
            request.ServingSize,
            request.PanVolume);

        var outcome = YieldReconciliationCalculator.Reconcile(input);

        if (outcome.Error is not null)
        {
            // Unreachable through the validator's own positivity rules in the ordinary case — kept as
            // defense in depth, the same reasoning ScaleAsync's ScalingErrorField fallback gives. The
            // calculator's error carries no field of its own to name.
            return OperationResult<RecipeYieldReconciliationResultServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.YieldRecalculationInvalidRequest,
                "That yield could not be recalculated as described.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<RecipeYieldReconciliationResultServiceModel>.Success(
            new RecipeYieldReconciliationResultServiceModel(sourceVersionNumber, outcome.Preview!));
    }

    public async Task<OperationResult<RecipeQuantityDisplayResultServiceModel>> NormalizeDisplayAsync(
        Guid recipeId,
        int sourceVersionNumber,
        RecipeQuantityDisplayRequest request,
        CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindCalculationSourceAsync(
            recipeId, sourceVersionNumber, cancellationToken);

        if (!visible)
        {
            return NotFound<RecipeQuantityDisplayResultServiceModel>();
        }

        if (source is null)
        {
            return OperationResult<RecipeQuantityDisplayResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That value could not be rendered as described.",
                [(SourceVersionNumberField, $"This recipe has no version {sourceVersionNumber}.")]));
        }

        var result = QuantityDisplayCalculator.Format(
            request.Value, request.UpperValue, request.Unit, request.Precision, request.UseAbbreviation, request.Rounding);

        return OperationResult<RecipeQuantityDisplayResultServiceModel>.Success(
            new RecipeQuantityDisplayResultServiceModel(sourceVersionNumber, result));
    }

    private const string MultiplierField = "multiplier";

    private const string TargetYieldQuantityField = "targetYieldQuantity";

    private static (string Field, string Error) ScalingErrorField(RecipeScalingRequestError error) => error switch
    {
        RecipeScalingRequestError.NonPositiveMultiplier =>
            (MultiplierField, "The multiplier must be greater than zero."),
        RecipeScalingRequestError.NonPositiveTargetYield =>
            (TargetYieldQuantityField, "The target yield must be greater than zero."),
        RecipeScalingRequestError.RecipeYieldNotStructured =>
            (TargetYieldQuantityField, "This recipe has no structured yield to scale toward."),
        RecipeScalingRequestError.FactorOutOfRange =>
            (MultiplierField, "That is too large a change to scale by. Try a factor closer to the recipe's own size."),
        _ => (MultiplierField, "That request could not be resolved."),
    };

    private static RecipeVersionComparisonSideServiceModel ToComparisonSide(RecipeVersionSnapshotRecord row) =>
        new()
        {
            VersionId = row.Id,
            VersionNumber = row.VersionNumber,
            Source = row.Source,
            Readiness = row.Readiness,
            CreatedAt = row.CreatedAt,
        };

    private static RecipeVersionHistoryServiceModel ToHistoryEntry(RecipeVersionHistoryRecord row) =>
        new()
        {
            Id = row.Id,
            VersionNumber = row.VersionNumber,
            Source = row.Source,
            Readiness = row.Readiness,
            Reason = row.Reason,
            CreatedAt = row.CreatedAt,
            // Left for the Facade, which is the only layer that may ask another module who this membership
            // belongs to. See RecipeVersionHistoryPageResult.
            CreatedByName = null,

            ParentVersionId = row.ParentVersionId,
            RestoredFromVersionId = row.RestoredFromVersionId,
            AiProposalId = row.AiProposalId,
        };

    public async Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(
        Guid recipeId,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetDetailAsync(recipeId, cancellationToken);

        return loaded is null
            ? NotFound<RecipeDetailServiceModel>()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
    }

    public async Task<OperationResult<RecipeSnapshotServiceModel>> GetSnapshotAsync(
        Guid recipeId, Guid versionId, CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindSnapshotSourceAsync(recipeId, versionId, cancellationToken);

        if (!visible)
        {
            return NotFound<RecipeSnapshotServiceModel>();
        }

        if (source is null)
        {
            return OperationResult<RecipeSnapshotServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That version could not be read.",
                [(SourceVersionIdField, "This recipe has no such version.")]));
        }

        return OperationResult<RecipeSnapshotServiceModel>.Success(new RecipeSnapshotServiceModel(
            source.VersionNumber, RecipeSnapshotSerializer.Deserialize(source.Document)));
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
        Guid recipeId,
        CanonicalRecipePatch patch,
        MeasurementDimension? submittedYieldUnitDimension,
        IReadOnlyDictionary<Guid, MeasurementDimension> ingredientUnitDimensions,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound<RecipeDetailServiceModel>();
        }

        return await MergeAsync(
            loaded,
            patch,
            submittedYieldUnitDimension,
            ingredientUnitDimensions,
            RecipeVersionSource.CreatorEdit,
            null,
            cancellationToken);
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> ApplyProposedChangesAsync(
        Guid recipeId,
        Guid expectedVersionId,
        Guid aiProposalId,
        IReadOnlyList<ProposedRecipeChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound<RecipeDetailServiceModel>();
        }

        // The staleness rule, at the point of writing. The creator reviewed a diff computed against exactly
        // this version; if the recipe has since moved on, every before value they were shown describes content
        // that is no longer there. Refusing is the only honest answer — rebasing silently would apply their
        // decision to text they never read.
        if (loaded.Recipe.CurrentVersion?.Id != expectedVersionId)
        {
            return Conflict();
        }

        var plan = RecipeProposalApplication.Plan(loaded, changes);

        if (!plan.Succeeded)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.RecipeInvalidRequest,
                plan.Error!,
                new Dictionary<string, string[]>()));
        }

        // No submitted yield unit and no resolved ingredient unit dimensions, because no proposal can name a
        // unit at all: AiDiffFields lists no identifier field, on the grounds that a model choosing a Guid is a
        // model inventing one. So the recipe's stored dimensions stand, and an empty map is the accurate input
        // rather than a missing one.
        return await MergeAsync(
            loaded,
            plan.Patch!,
            null,
            ReadOnlyDictionary<Guid, MeasurementDimension>.Empty,
            RecipeVersionSource.AiProposalAccepted,
            aiProposalId,
            cancellationToken);
    }

    /// <summary>
    /// Merges a patch into a loaded recipe and captures the one version that records it.
    /// </summary>
    /// <param name="ingredientUnitDimensions">
    /// The dimension of each measurement unit the submitted ingredient lines name, resolved by the caller.
    /// Empty when no ingredient line was submitted — including on the AI path, where no proposal can name a
    /// unit.
    /// </param>
    /// <param name="source">What produced this edit. Recorded on the version, and pinned to its proposal.</param>
    /// <param name="aiProposalId">
    /// The accepted proposal, when <paramref name="source"/> is
    /// <see cref="RecipeVersionSource.AiProposalAccepted"/>, and <c>null</c> otherwise. A check constraint on
    /// the table refuses either without the other.
    /// </param>
    /// <remarks>
    /// Shared by a creator's own edit and by an accepted AI proposal, and that sharing is the point rather than
    /// a convenience: AIREC-GR-007 requires accepted changes to pass ordinary recipe validation, and the only
    /// way to be sure they do is for there to be one merge path. A second one written for proposals would be a
    /// second definition of what a valid recipe is, and would drift.
    /// </remarks>
    private async Task<OperationResult<RecipeDetailServiceModel>> MergeAsync(
        TaggedRecipe loaded,
        CanonicalRecipePatch patch,
        MeasurementDimension? submittedYieldUnitDimension,
        IReadOnlyDictionary<Guid, MeasurementDimension> ingredientUnitDimensions,
        RecipeVersionSource source,
        Guid? aiProposalId,
        CancellationToken cancellationToken)
    {
        var recipe = loaded.Recipe.Recipe;

        // Checked before anything is merged, so a refused edit is answered from the state it was composed
        // against rather than from a half-applied one. The database repeats this check when the save runs;
        // this one is not redundant, because it is the only one that can produce a readable answer, and it
        // catches the far commoner case — a token that went stale minutes ago, not microseconds.
        if (!RecipeConcurrencyToken.Matches(patch.ExpectedConcurrencyToken, recipe.RowVersion))
        {
            return Conflict();
        }

        // After the token check, deliberately. A caller holding a stale token has not seen the recipe as it
        // now stands — possibly including that someone archived it — so "re-read this" is the more useful of
        // the two answers and the one that leads them to the other.
        if (!RecipePolicy.AcceptsContentChanges(recipe.Status))
        {
            return ArchivedConflict();
        }

        // The yield unit and its dimension move together or not at all. When the unit was not submitted the
        // stored dimension stands, because the unit is not changing; when it was submitted as null the
        // dimension goes with it, because a dimension describes a unit that is no longer there.
        var yieldUnitId = patch.YieldUnitId.Or(recipe.YieldUnitId);
        var yieldUnitDimension = patch.YieldUnitId.IsSubmitted
            ? patch.YieldUnitId.Value is null ? null : submittedYieldUnitDimension
            : recipe.YieldUnitDimension;

        if (Validate(patch, recipe, yieldUnitId, yieldUnitDimension) is { } error)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(error);
        }

        // Collected rather than applied, so that "did anything actually change" is answered before the
        // aggregate is touched. A submitted field holding the value it already holds is not a change, and
        // treating it as one would write a version recording nothing.
        var changes = new List<Action>();

        Change<string?>(patch.Title, recipe.Title, value => recipe.Title = value!);
        Change(patch.Description, recipe.Description, value => recipe.Description = value);
        Change(patch.Headnote, recipe.Headnote, value => recipe.Headnote = value);
        Change(patch.Notes, recipe.Notes, value => recipe.Notes = value);
        Change(patch.StorageNotes, recipe.StorageNotes, value => recipe.StorageNotes = value);
        Change(patch.AttributionText, recipe.AttributionText, value => recipe.AttributionText = value);
        Change(patch.SourceUrl, recipe.SourceUrl, value => recipe.SourceUrl = value);
        Change(patch.CuisineId, recipe.CuisineId, value => recipe.CuisineId = value);
        Change(patch.CourseId, recipe.CourseId, value => recipe.CourseId = value);
        Change(patch.PrimaryTechniqueId, recipe.PrimaryTechniqueId, value => recipe.PrimaryTechniqueId = value);
        Change(patch.PrepTimeMinutes, recipe.PrepTimeMinutes, value => recipe.PrepTimeMinutes = value);
        Change(patch.CookTimeMinutes, recipe.CookTimeMinutes, value => recipe.CookTimeMinutes = value);
        Change(patch.RestTimeMinutes, recipe.RestTimeMinutes, value => recipe.RestTimeMinutes = value);
        Change(patch.TotalTimeMinutes, recipe.TotalTimeMinutes, value => recipe.TotalTimeMinutes = value);
        Change(patch.YieldText, recipe.YieldText, value => recipe.YieldText = value);
        Change(patch.YieldQuantity, recipe.YieldQuantity, value => recipe.YieldQuantity = value);
        Change(patch.YieldUnitId, recipe.YieldUnitId, value =>
        {
            recipe.YieldUnitId = value;

            // Derived, never accepted from the request: a client able to send this could contradict the unit
            // it names, and the composite foreign key would then be pinning a lie.
            recipe.YieldUnitDimension = yieldUnitDimension;
        });
        Change(patch.ServingCount, recipe.ServingCount, value => recipe.ServingCount = value);
        Change(patch.ServingSize, recipe.ServingSize, value => recipe.ServingSize = value);

        // No status change is staged here, and there is nothing left to stage: the validation above refuses a
        // submitted status that differs from the recipe's own, so anything that reaches this point is asking
        // for the state the recipe is already in. Editorial state moves through the transition seam
        // (TESTRUN-005) and nowhere else — including the reopen an edit to an approved recipe triggers, which
        // is a transition in its own right rather than a field assignment hidden in a merge.

        var tags = patch.Tags.IsSubmitted ? patch.Tags.Value : null;
        var tagsChanged = tags is not null && !SameTags(loaded.Tags, tags);

        // Reconciled eagerly rather than collected into `changes` like the scalar fields: there is no single
        // "current value" to compare a submission against, only a graph to reconcile against, and the
        // reconciliation already returns whether it touched anything. A true no-op resubmission mutates
        // nothing here, so running it before the no-op check below is safe.
        var instructionsChanged = patch.Instructions.TryGetSubmitted(out var submittedInstructions)
            && ReconcileInstructions(recipe, submittedInstructions);

        var ingredientsChanged = patch.IngredientGroups.TryGetSubmitted(out var submittedIngredients)
            && ReconcileIngredientGroups(recipe, submittedIngredients, ingredientUnitDimensions);

        if (changes.Count == 0 && !tagsChanged && !instructionsChanged && !ingredientsChanged)
        {
            // Nothing to record. Answering with the recipe as it stands is the honest reply — the creator
            // asked for a state it is already in — and writing a version for it would put a change with no
            // content into a history they read, while invalidating every token their collaborators hold.
            return OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
        }

        foreach (var change in changes)
        {
            change();
        }

        // One read of the clock for the whole edit, so the recipe and the version recording it agree on when
        // it happened. The actor is the resolved membership, never a request field.
        recipe.UpdatedAt = clock.UtcNow;
        recipe.UpdatedByMembershipId = workspace.MembershipId;

        // Editing an approved recipe reopens it, because an approval is a claim that somebody cleared these
        // words and the words are about to change (RecipeStatusTransitions.EditReopens). Staged here so it
        // commits in the edit's own transaction: a recipe that had been edited and left reading as approved,
        // even for an instant, is the state this rule exists to make unreachable.
        //
        // No role check, deliberately, and it is the one asymmetry in the machine: the reopen rule asks for
        // an Editor, and this reopen is a consequence of an edit rather than a move anybody requested. A
        // Contributor allowed to edit the recipe is allowed to edit it, and refusing them because of a
        // transition they did not ask for would make the edit route's permissions depend on a state they
        // cannot see the significance of. The transition row records them as the actor, so the history still
        // says who caused it.
        var reopen = RecipeStatusTransitions.EditReopens(recipe.Status)
            ? StageReopen(recipe)
            : null;

        var facts = new RecipeVersionFacts(
            source,

            // Draft, always, for the reason CreateAsync gives: the approval transition is the only write that
            // mints a ready version. An edit to an approved recipe is emphatically not one — it reopens the
            // recipe (RecipeStatusTransitions.EditReopens), so the version it captures is of content that is
            // once again being worked on.
            RecipeVersionReadiness.Draft,
            patch.Reason,
            AiProposalId: aiProposalId);

        // Tags are handed down only when they are actually changing. Submitting the set a recipe already has
        // is not a request to rewrite its links.
        var outcome = await dataLayer.UpdateAsync(
            loaded, facts, tagsChanged ? tags : null, reopen, cancellationToken);

        return outcome.Version is null
            ? Conflict()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(
                // A `with` expression, not a fresh construction: the write changed the aggregate, its
                // version and its tags, and everything else the read resolved — the duplication lineage
                // today — has to survive unchanged. A constructor call here would drop it silently, and
                // would drop the next such member too.
                loaded with
                {
                    Recipe = new CompleteRecipe(recipe, outcome.Version),
                    Tags = outcome.Tags,
                }));

        void Change<T>(PatchField<T> field, T current, Action<T> apply)
        {
            if (field.IsSubmitted && !EqualityComparer<T>.Default.Equals(field.Value, current))
            {
                changes.Add(() => apply(field.Value));
            }
        }
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> RestoreVersionAsync(
        Guid recipeId,
        int versionNumber,
        CanonicalRestoreRecipeVersion request,
        CancellationToken cancellationToken)
    {
        var unit = await dataLayer.GetForRestoreAsync(recipeId, versionNumber, cancellationToken);
        if (unit is null)
        {
            return NotFound<RecipeDetailServiceModel>();
        }

        var loaded = unit.Recipe;
        var recipe = loaded.Recipe.Recipe;

        // Before the token check, deliberately — see the interface remarks. A version this recipe does not
        // have is a mistake in the request, and no amount of re-reading will make it right.
        if (unit.Snapshot is null)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That version could not be restored.",
                [(VersionNumberField, $"This recipe has no version {versionNumber}.")]));
        }

        // Checked before anything is reconciled, so a refused restore is answered from the state it was
        // composed against rather than from a half-applied one. The database repeats this when the save runs;
        // this one is not redundant, because it is the only one that can produce a readable answer, and it
        // catches the far commoner case — a token that went stale minutes ago, not microseconds.
        if (!RecipeConcurrencyToken.Matches(request.ExpectedConcurrencyToken, recipe.RowVersion))
        {
            return Conflict();
        }

        // A restore rewrites the recipe's content, so an archived recipe refuses it for the same reason it
        // refuses an edit — REC-006's freeze covers every content write, not only the obvious one. The
        // creator's remedy is to bring the recipe back first, which the error code says.
        if (!RecipePolicy.AcceptsContentChanges(recipe.Status))
        {
            return ArchivedConflict();
        }

        // Deserialized here rather than in the repository, for the reason CompareVersionsAsync gives: reading
        // stored text as a document is translation, and what an unreadable one means is a domain decision.
        var document = RecipeSnapshotSerializer.Deserialize(unit.Snapshot.Document);

        // Read before reconciling and unconditionally when the archive names any tag, because the reconciler
        // must be told which of those tags still exist — a link to a vocabulary row that is gone is an insert
        // the Restrict foreign key refuses. Going through the DataLayer, not a repository: Business calls one
        // layer.
        var tagIds = document.Tags.Select(tag => tag.WorkspaceTagId).ToList();
        IReadOnlyList<WorkspaceTag> vocabulary = tagIds.Count == 0
            ? []
            : await dataLayer.FindWorkspaceTagsAsync(tagIds, cancellationToken);

        // Reconciled eagerly rather than compared first, exactly as an edit's instructions are: there is no
        // single "current value" to test a document against, only a graph to reconcile with, and the
        // reconciliation already reports whether it touched anything. A restore onto content that already
        // matches mutates nothing, which is what makes asking afterwards safe.
        if (!RecipeSnapshotReconciler.Apply(recipe, document, vocabulary))
        {
            // Nothing to record. The creator asked for a state the recipe is already in, and writing a
            // version for it would put a change with no content into a history they read while invalidating
            // every token their collaborators hold.
            return OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
        }

        // One read of the clock for the whole restore, so the recipe and the version recording it agree on
        // when it happened. The actor is the resolved membership: a restore is attributed to whoever performed
        // it, never to the author of the version whose content came back.
        recipe.UpdatedAt = clock.UtcNow;
        recipe.UpdatedByMembershipId = workspace.MembershipId;

        var facts = new RecipeVersionFacts(
            RecipeVersionSource.Restore,

            // Draft, always, for the reason CreateAsync gives. A restore no longer puts the status back at
            // all (RecipeSnapshotReconciler), so there is no longer a status here to derive from — and a
            // restore of an approved version producing another ready version would have been an approval
            // nobody made.
            RecipeVersionReadiness.Draft,
            request.Reason,

            // The version the content came from. The one the restore replaces is the parent, and the
            // DataLayer derives that from the aggregate it already holds.
            unit.Snapshot.Id);

        var outcome = await dataLayer.RestoreAsync(loaded, facts, vocabulary, cancellationToken);

        return outcome.Version is null
            ? Conflict()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(
                // A `with` expression, not a fresh construction: the write changed the aggregate, its
                // version and its tags, and everything else the read resolved — the duplication lineage
                // today — has to survive unchanged. A constructor call here would drop it silently, and
                // would drop the next such member too.
                loaded with
                {
                    Recipe = new CompleteRecipe(recipe, outcome.Version),
                    Tags = outcome.Tags,
                }));
    }

    public async Task<OperationResult<CreatedRecipeServiceModel>> DuplicateAsync(
        Guid recipeId,
        CanonicalDuplicateRecipe request,
        CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindDuplicateSourceAsync(
            recipeId, request.SourceVersionNumber, cancellationToken);

        if (!visible)
        {
            return NotFound<CreatedRecipeServiceModel>();
        }

        if (source is null)
        {
            // Two shapes of the same code, because only one of them is the caller's mistake. A number they
            // sent names the field they sent it in; a recipe with no archived version at all — unreachable
            // through this API, since every recipe it writes has version 1 — is nothing they can correct, so
            // naming a field would tell them to fix a request that was right.
            return OperationResult<CreatedRecipeServiceModel>.Failure(
                request.SourceVersionNumber is { } number
                    ? OperationError.Validation(
                        RecipeErrorCodes.VersionNotFound,
                        CannotDuplicate,
                        [(SourceVersionNumberField, $"This recipe has no version {number}.")])
                    : new OperationError(
                        RecipeErrorCodes.VersionNotFound,
                        "That recipe has no version to copy.",
                        new Dictionary<string, string[]>()));
        }

        // Deserialized here rather than in the repository, for the reason CompareVersionsAsync gives: reading
        // stored text as a document is translation, and deciding what an unreadable one means is a domain
        // decision. An unreadable document raises, which is a 500 and the truthful status — the only way here
        // is a document written by a newer build than the one serving the request.
        var document = RecipeSnapshotSerializer.Deserialize(source.Document);

        // One read of the clock for the whole operation, so the recipe, its version and any tag born with it
        // all agree on when this happened.
        var now = clock.UtcNow;

        // Fresh identities throughout, and the archived content underneath. WorkspaceId is left unset on
        // every entity: the ownership interceptor stamps it, and Business assigning it is the defect
        // tenancy.md names.
        var copy = RecipeSnapshotDuplicator.Duplicate(document, Guid.NewGuid());

        // What the copy does not inherit, in one place. Everything else came from the archive.
        copy.Title = request.Title;
        copy.Status = RecipeStatus.Draft;
        copy.DuplicatedFromVersionId = source.Id;

        // The authenticated creator making the copy — never the author of the recipe it came from.
        copy.CreatedByMembershipId = workspace.MembershipId;
        copy.UpdatedByMembershipId = workspace.MembershipId;
        copy.CreatedAt = now;
        copy.UpdatedAt = now;

        // Resolved back to names so the create's own tag path can reuse the existing vocabulary rows. Ids
        // whose rows are gone resolve to nothing and are not applied — see RecipeSnapshotDuplicator for why
        // the links are not built from the document directly.
        var tagIds = document.Tags.Select(tag => tag.WorkspaceTagId).ToList();
        IReadOnlyList<WorkspaceTag> vocabulary = tagIds.Count == 0
            ? []
            : await dataLayer.FindWorkspaceTagsAsync(tagIds, cancellationToken);

        var facts = new RecipeVersionFacts(
            RecipeVersionSource.Duplicate,

            // Draft, not derived from the status like a create's or an edit's: the status above is forced, so
            // a conditional here would be a branch that can never take its other path.
            RecipeVersionReadiness.Draft,

            // No reason. A copy's first version is explained by its source and its lineage, and inventing a
            // sentence would put user-visible English in the domain that nothing can localize — the argument
            // CreateAsync makes for version 1.
            Reason: null);

        var created = await dataLayer.CreateAsync(
            copy,
            facts,
            [.. vocabulary.Select(tag => new RecipeTagName(tag.Name, tag.NormalizedName))],
            cancellationToken);

        return OperationResult<CreatedRecipeServiceModel>.Success(new CreatedRecipeServiceModel(
            created.RecipeId,
            copy.Title,
            copy.Status,
            created.VersionId,
            created.VersionNumber,
            copy.CreatedAt));
    }

    private const string CannotDuplicate = "That recipe cannot be copied as described.";

    public Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            recipeId,
            RecipeStatus.Archived,

            // No reason. The route says what happened and RecipeLifecycleViewModel deliberately carries no
            // field for one; the archive rule asks for none, so this is an absence the machine agrees with
            // rather than a value being dropped.
            reason: null,
            readiness: null,
            actorUserId,
            expectedConcurrencyToken,
            cancellationToken);

    public Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            recipeId,

            // The one target the machine has out of the archive. A recipe that is not archived is answered
            // as a repeat rather than demoted, which is the asymmetry this command always had: the old
            // implementation spelled it as a predicate, and the machine gets it from there being no rule
            // from any other state to here.
            RecipePolicy.UnarchivedStatus,
            reason: null,
            readiness: null,
            actorUserId,
            expectedConcurrencyToken,
            cancellationToken);

    public async Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
        Guid recipeId,
        RecipeStatus target,
        string? reason,
        RecipeReadinessServiceModel? readiness,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound<RecipeDetailServiceModel>();
        }

        var recipe = loaded.Recipe.Recipe;

        // Before anything is touched, so a refused command is answered from the state it was composed
        // against. Checked even before the "already there" answer below: a creator quoting a stale token has
        // not seen what the recipe looks like now, and telling them "already approved" would hide a
        // collaborator's work from them.
        if (!RecipeConcurrencyToken.Matches(expectedConcurrencyToken, recipe.RowVersion))
        {
            return Conflict();
        }

        var from = recipe.Status;

        if (from == target)
        {
            // A repeat, not a jump. The honest answer is the recipe, and writing a transition row or an audit
            // entry for a move that did not happen would put a lie in the two records that have to be
            // trustworthy. This is also what makes a replayed approval safe: no second version, no second
            // row.
            return OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
        }

        if (RecipeStatusTransitions.Find(from, target) is not { } rule)
        {
            return Invalid(
                RecipeErrorCodes.TransitionInvalidRequest,
                $"A recipe cannot go from {from} to {target}.",
                RecipeStatusTransitions.From(from) is { Count: > 0 } available
                    ? $"From {from} it can go to {string.Join(", ", available)}."
                    : $"Nothing can move a recipe out of {from}.");
        }

        // Role before reason, so a Contributor asking for an approval is told they may not rather than told
        // to write a sentence first and then told they may not.
        if (workspace.Role < rule.MinimumRole)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.TransitionForbidden,
                $"Moving a recipe from {from} to {target} is a {rule.MinimumRole} action.",
                new Dictionary<string, string[]>()));
        }

        if (rule.RequiresReason && string.IsNullOrWhiteSpace(reason))
        {
            return Invalid(
                RecipeErrorCodes.TransitionInvalidRequest,
                $"Moving a recipe from {from} to {target} needs a reason.",
                "Say why, so the recipe's history explains itself later.");
        }

        if (rule.RequiresReadinessClear)
        {
            if (readiness is null)
            {
                // Not reachable through the facade, which evaluates whenever the target needs it. Refused
                // rather than assumed clear, because the assumption in the other direction is an approval
                // nothing gated — and a caller that skipped the evaluation is a caller with a bug, not a
                // caller to be trusted.
                return Invalid(
                    RecipeErrorCodes.TransitionInvalidRequest,
                    $"Moving a recipe from {from} to {target} needs a readiness evaluation.",
                    "No evaluation was supplied with this transition.");
            }

            // The evaluation has to be of the recipe as it stands now. One made before a collaborator's edit
            // says nothing about the content this approval would name, however clear it was — so a token that
            // no longer matches is a conflict, which tells the approver to go and look.
            if (readiness.ConcurrencyToken != RecipeConcurrencyToken.From(recipe.RowVersion))
            {
                return Conflict();
            }

            if (readiness.HasBlockers)
            {
                return OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                    RecipeErrorCodes.TransitionBlockedConflict,
                    $"This recipe has {readiness.BlockerCount} readiness blocker"
                        + $"{(readiness.BlockerCount == 1 ? string.Empty : "s")} outstanding.",

                    // The rule ids, so a client can name them rather than send the approver back to the
                    // readiness screen to work out which. Ids and not details: the details are that screen's
                    // to render, and repeating them here would be two places for the same sentence.
                    new Dictionary<string, string[]>
                    {
                        ["blockingRules"] =
                        [
                            .. readiness.Findings
                                .Where(finding => finding.Status is RecipeReadinessStatus.Blocker)
                                .Select(finding => finding.RuleId),
                        ],
                    }));
            }
        }

        // One read of the clock, and the actor from the resolved membership — never a request field. The
        // audit entry's own timestamp comes from the same clock inside the writer.
        var occurredAt = clock.UtcNow;

        recipe.Status = target;
        recipe.UpdatedAt = occurredAt;
        recipe.UpdatedByMembershipId = workspace.MembershipId;

        var transition = new RecipeStatusTransition
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            FromStatus = from,
            ToStatus = target,

            // Trimmed, because a reason of three spaces satisfied the check above only by accident, and
            // nulled when empty so that "no reason" has one spelling in the column.
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ActorMembershipId = workspace.MembershipId,
            OccurredAt = occurredAt,
            MachineVersion = RecipeStatusTransitions.Version,

            // Set together with the readiness gate or not at all, which is what
            // CK_RecipeStatusTransitions_Approval_Columns enforces. CreatedVersionId is filled in by the
            // DataLayer, which is where the version is built.
            ReadinessRuleSetVersion = rule.RequiresReadinessClear ? readiness!.RuleSetVersion : null,
            ReadinessEvaluatedVersionId = rule.RequiresReadinessClear ? readiness!.EvaluatedVersionId : null,
        };

        var version = rule.WritesVersion
            ? new RecipeVersionFacts(
                RecipeVersionSource.ReadinessApproval,

                // The only write that mints one. Every other path now records Draft; see CreateAsync.
                RecipeVersionReadiness.Ready,

                // The approver's own words where they wrote any. A version's reason is creator text and this
                // is the same kind of sentence, so it travels into the version as well as the transition
                // rather than being paraphrased for one of them.
                transition.Reason)
            : null;

        var (committed, captured) = await dataLayer.TryTransitionAsync(
            loaded,
            transition,
            version,
            new AuditEntry(
                actorUserId,
                AuditActionFor(target, from),
                RecipeAuditActions.ResourceType,
                recipeId.ToString("D"),
                CorrelationId(),
                $"Moved the recipe from {from} to {target}.",

                // State names, not content. AuditLog requires these to stay safe to display, and "which
                // editorial state" is exactly the kind of pointer they are for — no title, no creator text,
                // and in particular not the reason, which is the creator's own words.
                BeforeReference: from.ToString(),
                AfterReference: target.ToString()),
            cancellationToken);

        if (!committed)
        {
            return Conflict();
        }

        // The recipe as it now stands, which on an approval means the version the approval wrote rather than
        // the one the read came back with. A `with` expression, not a fresh construction, for the reason
        // MergeAsync gives: everything else the read resolved has to survive unchanged.
        return OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(
            captured is null
                ? loaded
                : loaded with { Recipe = new CompleteRecipe(recipe, captured) }));
    }

    /// <summary>
    /// Which audit code a move is written under.
    /// </summary>
    /// <remarks>
    /// The three moves auth.md and REC-006 name get their own codes, and the rest share one. Keyed on the
    /// target except for the restore, which is the one move whose meaning is in where it came <em>from</em>:
    /// a recipe arriving at Draft from the archive was unarchived, and one arriving there any other way
    /// cannot happen, so the pair is read rather than the target alone.
    /// </remarks>
    private static string AuditActionFor(RecipeStatus target, RecipeStatus from) => (from, target) switch
    {
        (_, RecipeStatus.Archived) => RecipeAuditActions.Archived,
        (RecipeStatus.Archived, _) => RecipeAuditActions.Unarchived,
        (_, RecipeStatus.Approved) => RecipeAuditActions.Approved,
        _ => RecipeAuditActions.StatusTransitioned,
    };

    /// <summary>
    /// Builds the reopen an edit to an approved recipe triggers, and applies it to the aggregate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status is set here rather than by the caller so that the row and the column cannot be built from
    /// two different readings of "where is this going". The clock is not read again: the recipe's
    /// <c>UpdatedAt</c> was set a moment ago by the edit, and a transition timestamped even a tick apart from
    /// the edit that caused it would read as two events instead of one.
    /// </para>
    /// <para>
    /// <strong>No audit entry, unlike every commanded transition.</strong> The edit seam writes none —
    /// <see cref="RecipeAuditActions"/> argues that a recipe's version history records an edit with more
    /// fidelity than an audit summary could, and it has no actor account id to hand because of it. The same
    /// argument covers this: the transition row records the reopen with its actor, its instant, its two
    /// states and its reason, which is strictly more than the log would say. Every transition somebody
    /// <em>asked</em> for is still audited.
    /// </para>
    /// </remarks>
    private static RecipeStatusTransition StageReopen(Recipe recipe)
    {
        var from = recipe.Status;
        var target = RecipeStatusTransitions.ReopenTarget;

        recipe.Status = target;

        return new RecipeStatusTransition
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            FromStatus = from,
            ToStatus = target,

            // Nobody typed a reason, so the domain supplies the sentence — the reopen rule requires one, and
            // this reopen has no author to ask. See RecipeStatusTransitions.EditReopenReason.
            Reason = RecipeStatusTransitions.EditReopenReason,

            // The membership the edit recorded a moment ago, so the two rows name the same person.
            ActorMembershipId = recipe.UpdatedByMembershipId,
            OccurredAt = recipe.UpdatedAt,
            MachineVersion = RecipeStatusTransitions.Version,
        };
    }

    private static OperationResult<RecipeDetailServiceModel> Invalid(
        string code,
        string message,
        string detail) =>
        OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
            code,
            message,
            new Dictionary<string, string[]> { ["transition"] = [detail] }));

    /// <summary>
    /// The identifier tying an audit entry to the request that produced it.
    /// </summary>
    /// <remarks>
    /// Taken from the ambient <see cref="System.Diagnostics.Activity"/>, which is the correlation the
    /// gateway already starts and ServiceDefaults already propagates through every hop — so an audit row can
    /// be joined to the traces and logs of the request that wrote it without a fourth parameter threaded
    /// through four layers to say the same thing. A new id when there is no activity, so an entry written
    /// outside a traced request is still correlatable with itself rather than carrying <c>Guid.Empty</c>
    /// alongside every other such entry.
    /// </remarks>
    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so the two are the same value in two
        // spellings rather than a hash of one into the other — an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default
            ? Guid.ParseExact(id.ToHexString(), "N")
            : Guid.NewGuid();
    }

    /// <summary>
    /// The body field a duplicate's source version arrives in, named in a refusal so a creator is told which
    /// part of their request was wrong.
    /// </summary>
    /// <remarks>
    /// The JSON name rather than <c>nameof</c>, matching how the validator's refusals reach the wire.
    /// <c>RecipeDuplicateEndpointTests</c> asserts the two agree.
    /// </remarks>
    private const string SourceVersionNumberField = "sourceVersionNumber";

    private const string SourceVersionIdField = "versionId";

    /// <summary>
    /// The route segment a restore's version number arrives on, named in a refusal so a creator is told which
    /// part of the URL was wrong.
    /// </summary>
    /// <remarks>
    /// A literal rather than <c>nameof</c> over a controller parameter, because the domain may not reference
    /// the API project. <c>RecipeRestoreEndpointTests</c> asserts the two agree.
    /// </remarks>
    private const string VersionNumberField = "versionNumber";

    /// <summary>
    /// Whether the submitted set names exactly the tags the recipe already carries.
    /// </summary>
    /// <remarks>
    /// Compared by normalized name, because that is what tag identity is: re-submitting "Weeknight" for a
    /// recipe tagged "weeknight" is not a change, and treating it as one would write a version whose only
    /// difference is capitalisation the vocabulary does not store.
    /// </remarks>
    private static bool SameTags(IReadOnlyList<WorkspaceTag> current, IReadOnlyList<RecipeTagName> submitted)
    {
        var held = current.Select(tag => tag.NormalizedName).ToHashSet(StringComparer.Ordinal);

        return held.Count == submitted.Count
            && submitted.All(tag => held.Contains(tag.NormalizedName));
    }

    /// <remarks>
    /// <see cref="RecipeInstructionGroup.WorkspaceId"/> is set here explicitly, from <paramref name="recipe"/>
    /// — not left for <c>WorkspaceOwnershipInterceptor</c> to stamp the way a scalar field would be. That
    /// interceptor runs at <c>SavingChanges</c>, after EF's own relationship fixup has already tried to
    /// propagate a value onto this new entity for adding it under an already-loaded parent (the update path,
    /// where <paramref name="recipe"/> is a real tracked row with a real <c>WorkspaceId</c>) — and
    /// <c>WorkspaceId</c> is part of this entity's own alternate key (<c>(WorkspaceId, RecipeId, Id)</c>, the
    /// principal key its steps' foreign key points at), which EF refuses to set via fixup on a still-forming
    /// entity. Setting it up front avoids the conflict; on a create, where <paramref name="recipe"/> has not
    /// been stamped yet either, this simply carries the same not-yet-set value the interceptor would apply
    /// moments later.
    /// </remarks>
    private static RecipeInstructionGroup BuildInstructionGroup(Recipe recipe, CanonicalInstructionGroup source, int sortOrder)
    {
        var group = new RecipeInstructionGroup
        {
            Id = Guid.NewGuid(),
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            Title = source.Title,
            SortOrder = sortOrder,
        };

        for (var index = 0; index < source.Steps.Count; index++)
        {
            group.Steps.Add(BuildInstructionStep(recipe, group.Id, source.Steps[index], index));
        }

        return group;
    }

    /// <inheritdoc cref="BuildInstructionGroup" path="//remarks"/>
    private static RecipeInstructionStep BuildInstructionStep(Recipe recipe, Guid groupId, CanonicalInstructionStep source, int sortOrder)
    {
        var step = new RecipeInstructionStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            RecipeInstructionGroupId = groupId,
        };
        ApplyStep(step, source, sortOrder);

        return step;
    }

    /// <summary>
    /// Makes <paramref name="recipe"/>'s method match <paramref name="submitted"/> exactly: a group named by
    /// id is updated and reordered in place, one submitted without an id is staged as new, and one the
    /// recipe currently has but the submission does not name is removed.
    /// </summary>
    /// <returns>Whether anything about the method actually changed.</returns>
    /// <remarks>
    /// An id the submission names that does not belong to this recipe is not distinguished from no id at
    /// all: it is silently treated as a new group. Refusing it would first have to decide whether the id is
    /// simply unknown or names a row in a different workspace, and answering that at all is the disclosure
    /// tenancy.md forbids. Treating both alike costs nothing — the result is identical either way.
    /// </remarks>
    private static bool ReconcileInstructions(Recipe recipe, IReadOnlyList<CanonicalInstructionGroup> submitted)
    {
        var changed = false;
        var existing = recipe.InstructionGroups.ToDictionary(group => group.Id);
        var kept = new HashSet<Guid>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];
            RecipeInstructionGroup group;

            if (source.Id is { } groupId && existing.TryGetValue(groupId, out var found))
            {
                group = found;
                kept.Add(groupId);

                if (group.Title != source.Title)
                {
                    group.Title = source.Title;
                    changed = true;
                }

                if (group.SortOrder != index)
                {
                    group.SortOrder = index;
                    changed = true;
                }
            }
            else
            {
                group = new RecipeInstructionGroup
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = recipe.WorkspaceId, // see BuildInstructionGroup's remarks
                    RecipeId = recipe.Id,
                    Title = source.Title,
                    SortOrder = index,
                };
                recipe.InstructionGroups.Add(group);
                changed = true;
            }

            if (ReconcileSteps(recipe, group, source.Steps))
            {
                changed = true;
            }
        }

        foreach (var orphan in existing.Values.Where(group => !kept.Contains(group.Id)).ToList())
        {
            recipe.InstructionGroups.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <inheritdoc cref="ReconcileInstructions"/>
    private static bool ReconcileSteps(Recipe recipe, RecipeInstructionGroup group, IReadOnlyList<CanonicalInstructionStep> submitted)
    {
        var changed = false;
        var existing = group.Steps.ToDictionary(step => step.Id);
        var kept = new HashSet<Guid>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];

            if (source.Id is { } stepId && existing.TryGetValue(stepId, out var found))
            {
                kept.Add(stepId);
                changed |= ApplyStep(found, source, index);
            }
            else
            {
                group.Steps.Add(BuildInstructionStep(recipe, group.Id, source, index));
                changed = true;
            }
        }

        foreach (var orphan in existing.Values.Where(step => !kept.Contains(step.Id)).ToList())
        {
            group.Steps.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies <paramref name="source"/> and <paramref name="sortOrder"/> onto <paramref name="step"/> in
    /// place, deriving <see cref="RecipeInstructionStep.TemperatureUnitDimension"/> rather than trusting it
    /// from the request — exactly as <see cref="Recipe.YieldUnitDimension"/> is derived on the recipe
    /// itself, and safe to hardcode here: the facade's reference check already refused any submitted
    /// <see cref="CanonicalInstructionStep.TemperatureUnitId"/> whose unit is not
    /// <see cref="MeasurementDimension.Temperature"/>, unlike a yield unit, where more than one dimension is
    /// acceptable and Business genuinely needs the resolved value.
    /// </summary>
    /// <returns>Whether anything about the step actually changed.</returns>
    private static bool ApplyStep(RecipeInstructionStep step, CanonicalInstructionStep source, int sortOrder)
    {
        var dimension = source.TemperatureUnitId is null ? (MeasurementDimension?)null : MeasurementDimension.Temperature;

        var changed = step.SortOrder != sortOrder
            || step.Text != source.Text
            || step.TechniqueId != source.TechniqueId
            || step.DurationMinutes != source.DurationMinutes
            || step.TemperatureValue != source.TemperatureValue
            || step.TemperatureUnitId != source.TemperatureUnitId
            || step.TemperatureUnitDimension != dimension
            || step.Note != source.Note;

        if (!changed)
        {
            return false;
        }

        step.SortOrder = sortOrder;
        step.Text = source.Text;
        step.TechniqueId = source.TechniqueId;
        step.DurationMinutes = source.DurationMinutes;
        step.TemperatureValue = source.TemperatureValue;
        step.TemperatureUnitId = source.TemperatureUnitId;
        step.TemperatureUnitDimension = dimension;
        step.Note = source.Note;

        return true;
    }

    /// <inheritdoc cref="BuildInstructionGroup" path="//remarks"/>
    private static RecipeIngredientGroup BuildIngredientGroup(
        Recipe recipe,
        CanonicalIngredientGroup source,
        int sortOrder,
        IReadOnlyDictionary<Guid, MeasurementDimension> unitDimensions)
    {
        var group = new RecipeIngredientGroup
        {
            Id = Guid.NewGuid(),
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            Title = source.Title,
            SortOrder = sortOrder,
        };

        for (var index = 0; index < source.Ingredients.Count; index++)
        {
            group.Ingredients.Add(BuildIngredientLine(recipe, group.Id, source.Ingredients[index], index, unitDimensions));
        }

        return group;
    }

    /// <inheritdoc cref="BuildInstructionGroup" path="//remarks"/>
    private static RecipeIngredient BuildIngredientLine(
        Recipe recipe,
        Guid groupId,
        CanonicalIngredientLine source,
        int sortOrder,
        IReadOnlyDictionary<Guid, MeasurementDimension> unitDimensions)
    {
        var line = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            RecipeIngredientGroupId = groupId,
        };
        ApplyIngredientLine(line, source, sortOrder, unitDimensions);

        return line;
    }

    /// <summary>
    /// Makes <paramref name="recipe"/>'s ingredient list match <paramref name="submitted"/> exactly — the
    /// ingredient counterpart of <see cref="ReconcileInstructions"/>, and identical in every particular
    /// including the one worth restating: an id the submission names that does not belong to this recipe is
    /// silently treated as a new group rather than refused, because refusing it would first have to decide
    /// whether the id is unknown or names a row in a different workspace, and answering that at all is the
    /// disclosure tenancy.md forbids.
    /// </summary>
    /// <returns>Whether anything about the ingredient list actually changed.</returns>
    private static bool ReconcileIngredientGroups(
        Recipe recipe,
        IReadOnlyList<CanonicalIngredientGroup> submitted,
        IReadOnlyDictionary<Guid, MeasurementDimension> unitDimensions)
    {
        var changed = false;
        var existing = recipe.IngredientGroups.ToDictionary(group => group.Id);
        var kept = new HashSet<Guid>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];
            RecipeIngredientGroup group;

            if (source.Id is { } groupId && existing.TryGetValue(groupId, out var found))
            {
                group = found;
                kept.Add(groupId);

                if (group.Title != source.Title)
                {
                    group.Title = source.Title;
                    changed = true;
                }

                if (group.SortOrder != index)
                {
                    group.SortOrder = index;
                    changed = true;
                }
            }
            else
            {
                group = new RecipeIngredientGroup
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = recipe.WorkspaceId, // see BuildInstructionGroup's remarks
                    RecipeId = recipe.Id,
                    Title = source.Title,
                    SortOrder = index,
                };
                recipe.IngredientGroups.Add(group);
                changed = true;
            }

            if (ReconcileIngredientLines(recipe, group, source.Ingredients, unitDimensions))
            {
                changed = true;
            }
        }

        foreach (var orphan in existing.Values.Where(group => !kept.Contains(group.Id)).ToList())
        {
            recipe.IngredientGroups.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <inheritdoc cref="ReconcileIngredientGroups"/>
    private static bool ReconcileIngredientLines(
        Recipe recipe,
        RecipeIngredientGroup group,
        IReadOnlyList<CanonicalIngredientLine> submitted,
        IReadOnlyDictionary<Guid, MeasurementDimension> unitDimensions)
    {
        var changed = false;
        var existing = group.Ingredients.ToDictionary(line => line.Id);
        var kept = new HashSet<Guid>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];

            if (source.Id is { } lineId && existing.TryGetValue(lineId, out var found))
            {
                kept.Add(lineId);
                changed |= ApplyIngredientLine(found, source, index, unitDimensions);
            }
            else
            {
                group.Ingredients.Add(BuildIngredientLine(recipe, group.Id, source, index, unitDimensions));
                changed = true;
            }
        }

        foreach (var orphan in existing.Values.Where(line => !kept.Contains(line.Id)).ToList())
        {
            group.Ingredients.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies <paramref name="source"/> and <paramref name="sortOrder"/> onto <paramref name="line"/> in
    /// place — the ingredient counterpart of <see cref="ApplyStep"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><see cref="RecipeIngredient.DisplayText"/> is copied verbatim, never derived.</strong> A
    /// recognised <see cref="CanonicalIngredientLine.IngredientId"/> enriches the line; recipes.md forbids it
    /// ever replacing or normalizing what the creator typed, so this never reads <c>IngredientId</c> to decide
    /// what <c>DisplayText</c> should say.
    /// </para>
    /// <para>
    /// <see cref="RecipeIngredient.MeasurementUnitDimension"/> is derived from <paramref name="unitDimensions"/>
    /// rather than trusted from the request, exactly as <see cref="ApplyStep"/> derives a step's temperature
    /// dimension — except that an ingredient's unit is not restricted to one acceptable dimension, so this
    /// cannot hardcode the value the way that one does and instead looks up what the Facade already resolved.
    /// </para>
    /// <para>
    /// <see cref="RecipeIngredient.MatchStatus"/> is derived from whether <c>IngredientId</c> is present, for
    /// the same reason the dimension is: <c>CK_RecipeIngredients_Match_Status</c> requires the two to agree,
    /// and this is the one submission that can only ever report <see cref="IngredientMatchStatus.Matched"/> or
    /// <see cref="IngredientMatchStatus.NotAttempted"/> — a creator naming an id has not asked this system to
    /// attempt a match and fail one, the way AI-assisted matching elsewhere can.
    /// </para>
    /// </remarks>
    /// <returns>Whether anything about the line actually changed.</returns>
    private static bool ApplyIngredientLine(
        RecipeIngredient line,
        CanonicalIngredientLine source,
        int sortOrder,
        IReadOnlyDictionary<Guid, MeasurementDimension> unitDimensions)
    {
        var dimension = source.MeasurementUnitId is { } unitId && unitDimensions.TryGetValue(unitId, out var resolved)
            ? resolved
            : (MeasurementDimension?)null;
        var matchStatus = source.IngredientId is null ? IngredientMatchStatus.NotAttempted : IngredientMatchStatus.Matched;

        var changed = line.SortOrder != sortOrder
            || line.DisplayText != source.DisplayText
            || line.DisplayTextSource != source.DisplayTextSource
            || line.IngredientNameText != source.IngredientNameText
            || line.UnitText != source.UnitText
            || line.Quantity != source.Quantity
            || line.QuantityUpper != source.QuantityUpper
            || line.MeasurementUnitId != source.MeasurementUnitId
            || line.MeasurementUnitDimension != dimension
            || line.IngredientId != source.IngredientId
            || line.MatchStatus != matchStatus
            || line.PreparationNote != source.PreparationNote
            || line.IsOptional != source.IsOptional
            || line.ScalingBehavior != source.ScalingBehavior;

        if (!changed)
        {
            return false;
        }

        line.SortOrder = sortOrder;
        line.DisplayText = source.DisplayText;
        line.DisplayTextSource = source.DisplayTextSource;
        line.IngredientNameText = source.IngredientNameText;
        line.UnitText = source.UnitText;
        line.Quantity = source.Quantity;
        line.QuantityUpper = source.QuantityUpper;
        line.MeasurementUnitId = source.MeasurementUnitId;
        line.MeasurementUnitDimension = dimension;
        line.IngredientId = source.IngredientId;
        line.MatchStatus = matchStatus;
        line.PreparationNote = source.PreparationNote;
        line.IsOptional = source.IsOptional;
        line.ScalingBehavior = source.ScalingBehavior;

        return true;
    }

    /// <summary>
    /// The one answer for a recipe that does not exist and for one belonging to another workspace.
    /// </summary>
    /// <remarks>
    /// Generic over what the caller was reading, so that every read of a recipe — the recipe itself, its
    /// history, and whatever follows — refuses in the same words under the same code. Two spellings of "that
    /// recipe could not be found" would eventually become two ways to tell those two cases apart.
    /// </remarks>
    private static OperationResult<T> NotFound<T>() =>
        OperationResult<T>.Failure(new OperationError(
            RecipeErrorCodes.RecipeNotFound,
            "That recipe could not be found.",
            new Dictionary<string, string[]>()));

    private static OperationResult<RecipeDetailServiceModel> Conflict() =>
        OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeConflict,
            "This recipe has changed since you opened it. Reload it and make your edit again.",
            new Dictionary<string, string[]>()));

    /// <summary>
    /// The refusal an archived recipe gives every write that would change its content.
    /// </summary>
    /// <remarks>
    /// Its own message as well as its own code, because the two conflicts want opposite things from the
    /// reader: <see cref="Conflict"/> says reload and retry, and this says the retry will not work until the
    /// recipe is brought back. See <see cref="RecipeErrorCodes.RecipeArchivedConflict"/>.
    /// </remarks>
    private static OperationResult<RecipeDetailServiceModel> ArchivedConflict() =>
        OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeArchivedConflict,
            "This recipe is archived. Bring it back from the archive before changing it.",
            new Dictionary<string, string[]>()));

    /// <summary>
    /// The invariants an edit must satisfy, judged against the recipe it would produce.
    /// </summary>
    /// <remarks>
    /// Every check reads a merged value rather than a submitted one, because a patch names only part of the
    /// recipe: clearing the yield quantity is wrong only because of a unit the request never mentioned, and
    /// a total time that was coherent an hour ago stops being so when the cook time grows.
    /// </remarks>
    private static OperationError? Validate(
        CanonicalRecipePatch patch,
        Recipe recipe,
        Guid? yieldUnitId,
        MeasurementDimension? yieldUnitDimension)
    {
        var errors = new List<(string Field, string Error)>();

        // Defence, not duplication: the validator already refuses both of these, and Business is the last
        // thing standing between them and the aggregate if some other caller — a worker, an AI plugin, a
        // future facade overload — ever builds a patch without it.
        if (patch.Title.IsSubmitted && string.IsNullOrEmpty(patch.Title.Value))
        {
            errors.Add((nameof(UpdateRecipeViewModel.Title), "A recipe must keep a title."));
        }

        // Left unchecked, a cleared status would be silently read as Draft further down, and an archived or
        // finished recipe would quietly reappear in the working set as an ordinary-looking creator edit.
        if (patch.Status.IsSubmitted && patch.Status.Value is null)
        {
            errors.Add((nameof(UpdateRecipeViewModel.Status), "A recipe must keep an editorial state."));
        }

        // Editorial state is a machine since TESTRUN-005, and an edit is not one of its moves: a submitted
        // status that asks for a different state is refused rather than applied. Submitting the state the
        // recipe is already in stays legal, because that is a request an edit can honour by doing nothing to
        // it.
        //
        // Here rather than in the validator because it is data-dependent — whether Draft is the state this
        // recipe is already in is a fact about the recipe, and backend.md keeps those in Business. The
        // validator's own rule narrows the field to Draft on shape alone, which catches the common client
        // mistake earlier and with a field error of its own.
        if (patch.Status.IsSubmitted && patch.Status.Value is { } submittedStatus && submittedStatus != recipe.Status)
        {
            errors.Add((
                nameof(UpdateRecipeViewModel.Status),
                "Move a recipe between editorial states with a readiness transition, not by editing its status."));
        }

        // A total below the longest single phase is incoherent under any amount of overlap. Deliberately not
        // "total equals the sum": prep overlaps cooking and resting is unattended, so a total legitimately
        // runs less than the sum, and a creator who writes "about 2 hours, mostly waiting" means it.
        var longestPhase = new[]
            {
                patch.PrepTimeMinutes.Or(recipe.PrepTimeMinutes),
                patch.CookTimeMinutes.Or(recipe.CookTimeMinutes),
                patch.RestTimeMinutes.Or(recipe.RestTimeMinutes),
            }
            .Where(minutes => minutes.HasValue)
            .Select(minutes => minutes!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (patch.TotalTimeMinutes.Or(recipe.TotalTimeMinutes) is { } total && total < longestPhase)
        {
            errors.Add((
                nameof(UpdateRecipeViewModel.TotalTimeMinutes),
                $"A total time of {total} minutes is less than the longest single step, which takes {longestPhase}."));
        }

        // Mirrors CK_Recipes_YieldUnit_RequiresQuantity, and unlike on a create it cannot be asked at the
        // edge: either half of the pair may be the half that is not being submitted.
        if (yieldUnitId is not null && patch.YieldQuantity.Or(recipe.YieldQuantity) is null)
        {
            errors.Add((nameof(UpdateRecipeViewModel.YieldQuantity), "Give the yield a number as well as a unit."));
        }

        if (yieldUnitId is not null && yieldUnitDimension == MeasurementDimension.Temperature)
        {
            errors.Add((nameof(UpdateRecipeViewModel.YieldUnitId), "A yield cannot be measured in degrees."));
        }

        // Mirrors CK_Recipes_ServingSize_RequiresYieldUnit, and asked here rather than at the edge for the
        // reason the yield pairing is: either half may be the half this patch does not mention, so clearing
        // the unit and keeping the serving size is a request the edge cannot see is incoherent.
        if (patch.ServingSize.Or(recipe.ServingSize) is not null && yieldUnitId is null)
        {
            errors.Add((
                nameof(UpdateRecipeViewModel.ServingSize),
                "Give the yield a unit as well, so a serving size has something to be measured in."));
        }

        // Mirrors the three positivity constraints. The shape validators already refuse a non-positive number,
        // so for an ordinary request this is unreachable — but the apply path for an accepted AI proposal
        // deliberately does not run them (see ProposedRecipeValues), and it builds a patch straight from
        // strings a model produced. Without this, an accepted "serves -3" reaches the check constraint and the
        // creator loses their decision to a 500. This method is the documented last line of defence for
        // exactly those non-ViewModel callers.
        Positive(
            patch.ServingCount.Or(recipe.ServingCount),
            nameof(UpdateRecipeViewModel.ServingCount),
            "A serving count must be greater than zero.");
        Positive(
            patch.ServingSize.Or(recipe.ServingSize),
            nameof(UpdateRecipeViewModel.ServingSize),
            "A serving size must be greater than zero.");
        Positive(
            patch.YieldQuantity.Or(recipe.YieldQuantity),
            nameof(UpdateRecipeViewModel.YieldQuantity),
            "A yield must be greater than zero.");

        void Positive(decimal? value, string field, string message)
        {
            if (value is <= 0m)
            {
                errors.Add((field, message));
            }
        }

        return errors.Count == 0
            ? null
            : OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                "That recipe cannot be changed as described.",
                errors);
    }

    /// <summary>
    /// The invariants a shape validator structurally cannot check.
    /// </summary>
    private static OperationError? Validate(CanonicalCreateRecipe input, MeasurementDimension? yieldUnitDimension)
    {
        var errors = new List<(string Field, string Error)>();

        // A total below the longest single phase is incoherent under any amount of overlap. Deliberately not
        // "total equals the sum": prep overlaps cooking and resting is unattended, so a total legitimately
        // runs less than the sum, and a creator who writes "about 2 hours, mostly waiting" means it.
        var longestPhase = new[] { input.PrepTimeMinutes, input.CookTimeMinutes, input.RestTimeMinutes }
            .Where(minutes => minutes.HasValue)
            .Select(minutes => minutes!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (input.TotalTimeMinutes is { } total && total < longestPhase)
        {
            errors.Add((
                nameof(CreateRecipeViewModel.TotalTimeMinutes),
                $"A total time of {total} minutes is less than the longest single step, which takes {longestPhase}."));
        }

        if (input.YieldUnitId is not null && yieldUnitDimension == MeasurementDimension.Temperature)
        {
            errors.Add((nameof(CreateRecipeViewModel.YieldUnitId), "A yield cannot be measured in degrees."));
        }

        // Mirrored here as well as at the edge, for the reason the patch overload gives: the validator runs on
        // a request, and a worker or plugin composing a CanonicalCreateRecipe directly never passes one.
        if (input.ServingSize is not null && input.YieldUnitId is null)
        {
            errors.Add((
                nameof(CreateRecipeViewModel.ServingSize),
                "Give the yield a unit as well, so a serving size has something to be measured in."));
        }

        Positive(input.ServingCount, nameof(CreateRecipeViewModel.ServingCount), "A serving count must be greater than zero.");
        Positive(input.ServingSize, nameof(CreateRecipeViewModel.ServingSize), "A serving size must be greater than zero.");
        Positive(input.YieldQuantity, nameof(CreateRecipeViewModel.YieldQuantity), "A yield must be greater than zero.");

        void Positive(decimal? value, string field, string message)
        {
            if (value is <= 0m)
            {
                errors.Add((field, message));
            }
        }

        return errors.Count == 0
            ? null
            : OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                "That recipe cannot be created as described.",
                errors);
    }
}
