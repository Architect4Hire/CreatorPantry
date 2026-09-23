using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
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
    /// <remarks>
    /// The dimension is a parameter rather than something looked up here, and that is a boundary decision
    /// rather than a convenience. Measurement units belong to another module, whose only permitted entry
    /// point is its facade — and Business may call nothing but its own DataLayer. So the Facade resolves it
    /// and passes the fact down. The same applies to whether the cuisine, course and technique ids exist and
    /// are active: <strong>the Facade must verify those before calling this method</strong>, because nothing
    /// below will, and an unverified id becomes a foreign-key violation at save time — a 500 where the honest
    /// answer is a 400 naming the field.
    /// </remarks>
    Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CanonicalCreateRecipe input,
        MeasurementDimension? yieldUnitDimension,
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
        CancellationToken cancellationToken);
}

internal sealed class RecipeBusiness(
    IRecipeDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IRecipeBusiness
{
    public async Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CanonicalCreateRecipe input,
        MeasurementDimension? yieldUnitDimension,
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

            Status = input.Status,

            // The authenticated creator, from the resolved membership. Never from the request body.
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var version = new RecipeVersionFacts(
            RecipeVersionSource.CreatorEdit,

            // The version inherits the recipe's editorial state rather than inventing one: a recipe created
            // as Ready has a first version that is ready, and one created as a draft does not.
            input.Status == RecipeStatus.Ready ? RecipeVersionReadiness.Ready : RecipeVersionReadiness.Draft,

            // No reason. RecipeVersion.Reason is documented as optional — "a routine save has no reason" —
            // and "this is the first version" is already carried by the version number. Inventing a sentence
            // here would put user-visible English in the domain that nothing can localize.
            Reason: null);

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

    public async Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(
        Guid recipeId,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetDetailAsync(recipeId, cancellationToken);

        return loaded is null
            ? NotFound()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
        Guid recipeId,
        CanonicalRecipePatch patch,
        MeasurementDimension? submittedYieldUnitDimension,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound();
        }

        var recipe = loaded.Recipe.Recipe;

        // Checked before anything is merged, so a refused edit is answered from the state it was composed
        // against rather than from a half-applied one. The database repeats this check when the save runs;
        // this one is not redundant, because it is the only one that can produce a readable answer, and it
        // catches the far commoner case — a token that went stale minutes ago, not microseconds.
        if (!RecipeConcurrencyToken.Matches(patch.ExpectedConcurrencyToken, recipe.RowVersion))
        {
            return Conflict();
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

        // Handled outside the generic helper because the request's type and the recipe's differ: a status
        // may be submitted as null, and the validation above has already refused that.
        if (patch.Status.IsSubmitted && patch.Status.Value is { } requestedStatus && requestedStatus != recipe.Status)
        {
            changes.Add(() => recipe.Status = requestedStatus);
        }

        var tags = patch.Tags.IsSubmitted ? patch.Tags.Value : null;
        var tagsChanged = tags is not null && !SameTags(loaded.Tags, tags);

        if (changes.Count == 0 && !tagsChanged)
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

        var facts = new RecipeVersionFacts(
            RecipeVersionSource.CreatorEdit,

            // The version inherits the recipe's editorial state, exactly as version 1 does: an edit that
            // leaves it Ready captures a ready version, and one that does not, does not.
            recipe.Status == RecipeStatus.Ready ? RecipeVersionReadiness.Ready : RecipeVersionReadiness.Draft,
            patch.Reason);

        // Tags are handed down only when they are actually changing. Submitting the set a recipe already has
        // is not a request to rewrite its links.
        var outcome = await dataLayer.UpdateAsync(loaded, facts, tagsChanged ? tags : null, cancellationToken);

        return outcome.Version is null
            ? Conflict()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(
                new TaggedRecipe(new CompleteRecipe(recipe, outcome.Version), outcome.Tags)));

        void Change<T>(PatchField<T> field, T current, Action<T> apply)
        {
            if (field.IsSubmitted && !EqualityComparer<T>.Default.Equals(field.Value, current))
            {
                changes.Add(() => apply(field.Value));
            }
        }
    }

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

    private static OperationResult<RecipeDetailServiceModel> NotFound() =>
        OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeNotFound,
            "That recipe could not be found.",
            new Dictionary<string, string[]>()));

    private static OperationResult<RecipeDetailServiceModel> Conflict() =>
        OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeConflict,
            "This recipe has changed since you opened it. Reload it and make your edit again.",
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

        return errors.Count == 0
            ? null
            : OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                "That recipe cannot be created as described.",
                errors);
    }
}
