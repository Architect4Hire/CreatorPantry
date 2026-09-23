using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// An update request reduced to what it actually means: text trimmed, blanks resolved to clears, tags
/// deduplicated and sorted, the requested status translated to a domain state. Two requests with the same
/// canonical form ask for the same edit.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="CanonicalCreateRecipe"/> counterpart, and it exists for the same reason: the facade hashes
/// it as the idempotency fingerprint and Business applies the edit from it, so "same fingerprint" and "same
/// edit" cannot drift apart, and a field added to <see cref="UpdateRecipeViewModel"/> but forgotten here
/// stops being applied at all — a visible bug rather than a silent gap in the fingerprint.
/// </para>
/// <para>
/// <strong>Absence survives canonicalization.</strong> Every content field stays a
/// <see cref="PatchField{T}"/>, because the difference between "leave this alone" and "clear this" is the
/// entire meaning of the request and collapsing it here would undo the work the serializer did to preserve
/// it.
/// </para>
/// <para>
/// It carries no workspace, no actor and no timestamp, for the reason
/// <see cref="CanonicalCreateRecipe"/> records: those would make every request unique, which is the opposite
/// of what a fingerprint is for.
/// </para>
/// </remarks>
public sealed record CanonicalRecipePatch
{
    /// <summary>The state of the recipe this edit was composed against.</summary>
    public required string ExpectedConcurrencyToken { get; init; }

    /// <summary>Why the creator made this edit, or <c>null</c>. Never a change in itself.</summary>
    public string? Reason { get; init; }

    public PatchField<string?> Title { get; init; }

    public PatchField<string?> Description { get; init; }

    public PatchField<string?> Headnote { get; init; }

    public PatchField<string?> Notes { get; init; }

    public PatchField<string?> StorageNotes { get; init; }

    public PatchField<string?> AttributionText { get; init; }

    public PatchField<string?> SourceUrl { get; init; }

    public PatchField<Guid?> CuisineId { get; init; }

    public PatchField<Guid?> CourseId { get; init; }

    public PatchField<Guid?> PrimaryTechniqueId { get; init; }

    public PatchField<int?> PrepTimeMinutes { get; init; }

    public PatchField<int?> CookTimeMinutes { get; init; }

    public PatchField<int?> RestTimeMinutes { get; init; }

    public PatchField<int?> TotalTimeMinutes { get; init; }

    public PatchField<string?> YieldText { get; init; }

    public PatchField<decimal?> YieldQuantity { get; init; }

    public PatchField<Guid?> YieldUnitId { get; init; }

    /// <summary>
    /// The domain state the request asked for, or a submitted <c>null</c> asking for the state to be
    /// cleared — which no recipe may be, and which Business refuses.
    /// </summary>
    /// <remarks>
    /// Nullable rather than resolved, unlike <see cref="CanonicalCreateRecipe.Status"/>. On a create an
    /// omitted status sensibly means <see cref="RecipeStatus.Draft"/>; on an edit "not mentioned" and
    /// "cleared" are already distinguished by the <see cref="PatchField{T}"/>, and folding a submitted
    /// <c>null</c> into <c>Draft</c> here would turn a refusal into a silent un-archiving for any caller
    /// that reached Business without the validator.
    /// </remarks>
    public PatchField<RecipeStatus?> Status { get; init; }

    /// <summary>
    /// The complete set of tags the recipe should end up with, deduplicated and ordered by normalized name.
    /// </summary>
    /// <remarks>
    /// Sorted for the reason <see cref="CanonicalCreateRecipe.Tags"/> gives — a recipe's tags are a set, so
    /// two requests listing them in different orders are one request — and an explicit empty list when the
    /// creator asked for every tag to be removed.
    /// </remarks>
    public PatchField<IReadOnlyList<RecipeTagName>> Tags { get; init; }

    /// <summary>
    /// The recipe's complete method, or absent to leave it alone. Order is creator-defined and preserved
    /// exactly as submitted — see <see cref="CanonicalInstructionGroup"/> for why this does not sort the way
    /// <see cref="Tags"/> does.
    /// </summary>
    public PatchField<IReadOnlyList<CanonicalInstructionGroup>> Instructions { get; init; }

    /// <summary>Reduces a validated request to its meaning.</summary>
    /// <remarks>
    /// Pure and total. It assumes shape validation has already run: it does not reject anything, it only
    /// normalizes, so a request that never passed the validator canonicalizes just as happily and is simply
    /// wrong in the same way it was before.
    /// </remarks>
    public static CanonicalRecipePatch From(UpdateRecipeViewModel model) => new()
    {
        ExpectedConcurrencyToken = model.ExpectedConcurrencyToken ?? string.Empty,
        Reason = Text(model.Reason),

        Title = Clearable(model.Title),
        Description = Clearable(model.Description),
        Headnote = Clearable(model.Headnote),
        Notes = Clearable(model.Notes),
        StorageNotes = Clearable(model.StorageNotes),
        AttributionText = Clearable(model.AttributionText),
        SourceUrl = Clearable(model.SourceUrl),

        CuisineId = model.CuisineId,
        CourseId = model.CourseId,
        PrimaryTechniqueId = model.PrimaryTechniqueId,

        PrepTimeMinutes = model.PrepTimeMinutes,
        CookTimeMinutes = model.CookTimeMinutes,
        RestTimeMinutes = model.RestTimeMinutes,
        TotalTimeMinutes = model.TotalTimeMinutes,

        YieldText = Clearable(model.YieldText),
        YieldQuantity = model.YieldQuantity,
        YieldUnitId = model.YieldUnitId,

        // The one place a requested status becomes a domain state. Everything below this line — the
        // fingerprint, the merge, the version's readiness — speaks only RecipeStatus. A submitted null is
        // carried through as a null rather than resolved, because it is a request to clear and clearing is
        // a refusal, not a default.
        Status = model.Status.IsSubmitted
            ? PatchField<RecipeStatus?>.Submitted(
                model.Status.Value is { } requested ? SettableRecipeStatus.ToDomain(requested) : null)
            : PatchField<RecipeStatus?>.Absent,

        Tags = model.Tags.IsSubmitted
            ? PatchField<IReadOnlyList<RecipeTagName>>.Submitted(TagNames(model.Tags.Value))
            : PatchField<IReadOnlyList<RecipeTagName>>.Absent,

        Instructions = model.Instructions.IsSubmitted
            ? PatchField<IReadOnlyList<CanonicalInstructionGroup>>.Submitted(CanonicalInstructions.From(model.Instructions.Value))
            : PatchField<IReadOnlyList<CanonicalInstructionGroup>>.Absent,
    };

    /// <summary>
    /// The value the idempotency executor hashes: the submitted fields and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dictionary rather than this record, because the record cannot be serialized — a
    /// <see cref="PatchField{T}"/> has no honest wire form for "absent", which is exactly why
    /// <see cref="PatchFieldJsonConverter{T}"/> refuses to write one. A dictionary spells the same three
    /// states perfectly: an unsubmitted field is a missing key, and a cleared one is a present key with a
    /// <c>null</c> value. The executor sorts object keys before hashing, so this is canonical already.
    /// </para>
    /// <para>
    /// <see cref="ExpectedConcurrencyToken"/> is part of the fingerprint deliberately. A caller who re-read
    /// the recipe and re-sent the same key is asking for a different edit — one composed against a different
    /// state — and should be told the key was reused, not handed the earlier answer.
    /// </para>
    /// <para>
    /// The keys are the JSON field names, so two requests that differ only in property casing or ordering
    /// still fingerprint alike, and a reader comparing a stored fingerprint to a request body is comparing
    /// the same vocabulary.
    /// </para>
    /// </remarks>
    public object Fingerprint(Guid recipeId)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        Add("title", Title);
        Add("description", Description);
        Add("headnote", Headnote);
        Add("notes", Notes);
        Add("storageNotes", StorageNotes);
        Add("attributionText", AttributionText);
        Add("sourceUrl", SourceUrl);
        Add("cuisineId", CuisineId);
        Add("courseId", CourseId);
        Add("primaryTechniqueId", PrimaryTechniqueId);
        Add("prepTimeMinutes", PrepTimeMinutes);
        Add("cookTimeMinutes", CookTimeMinutes);
        Add("restTimeMinutes", RestTimeMinutes);
        Add("totalTimeMinutes", TotalTimeMinutes);
        Add("yieldText", YieldText);
        Add("yieldQuantity", YieldQuantity);
        Add("yieldUnitId", YieldUnitId);
        Add("status", Status);

        if (Tags.IsSubmitted)
        {
            fields["tags"] = Tags.Value.Select(tag => tag.NormalizedName).ToArray();
        }

        Add("instructions", Instructions);

        return new
        {
            RecipeId = recipeId,
            ExpectedConcurrencyToken,
            Reason,
            Fields = fields,
        };

        void Add<T>(string name, PatchField<T> field)
        {
            if (field.IsSubmitted)
            {
                fields[name] = field.Value;
            }
        }
    }

    /// <summary>
    /// Normalizes a submitted text field, resolving whitespace to a clear.
    /// </summary>
    /// <remarks>
    /// Trimming only — internal spacing is the creator's, and recipes.md makes their text canonical.
    /// Submitting <c>"   "</c> collapses to <c>null</c> so that "cleared" has one representation rather than
    /// two that look identical to a reader and compare differently to a machine.
    /// </remarks>
    private static PatchField<string?> Clearable(PatchField<string?> field) =>
        field.IsSubmitted ? PatchField<string?>.Submitted(Text(field.Value)) : PatchField<string?>.Absent;

    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// The tags the recipe should end up with. A <c>null</c> list and an empty one both mean "no tags".
    /// </summary>
    private static IReadOnlyList<RecipeTagName> TagNames(IReadOnlyList<string?>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var byIdentity = new Dictionary<string, RecipeTagName>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            var name = Text(tag);
            if (name is null)
            {
                continue;
            }

            var normalized = NameNormalization.NormalizeName(name);

            // First wins, so the creator's own capitalisation of a tag they listed twice is the one stored.
            // The validator already refuses that request; this only decides what happens if something
            // bypassed it.
            if (normalized.Length > 0)
            {
                byIdentity.TryAdd(normalized, new RecipeTagName(name, normalized));
            }
        }

        return [.. byIdentity.Values.OrderBy(tag => tag.NormalizedName, StringComparer.Ordinal)];
    }
}
