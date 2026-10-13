using System.ComponentModel;
using CreatorPantry.Domain.Managers.Patching;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The body of <c>POST .../content-cards</c> (13.6, KAN-003).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is not here is decided by the server.</strong> There is no workspace, no author, no place in
/// the column, no weekday and no history: the workspace and the author come from the route and the resolved
/// membership, a new card goes to the bottom of its column, the weekday is the planned date's own, and the
/// creation is recorded as the card's first move.
/// </para>
/// <para>
/// Every lineage id names a record this workspace already owns. Each is checked through its owner before
/// anything is written, and one that cannot be found is refused the same way whether it never existed or
/// belongs to someone else.
/// </para>
/// </remarks>
public sealed record CreateContentCardViewModel
{
    /// <summary>The creator's name for the work. Required; stored as typed apart from surrounding space.</summary>
    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>The column to start in. Left out, a card starts in <see cref="ContentCardColumn.ToDo"/>.</summary>
    public ContentCardColumn? Column { get; init; }

    /// <summary>A content channel key the catalogue offers, or null for no channel in particular.</summary>
    public string? ChannelKey { get; init; }

    /// <summary>The planned calendar date in the workspace's own zone, as <c>yyyy-MM-dd</c>.</summary>
    public DateOnly? PlannedDate { get; init; }

    /// <summary>A cuisine code from the cooking vocabulary.</summary>
    public string? CuisineCode { get; init; }

    /// <summary>A dish type code from the cooking vocabulary.</summary>
    public string? DishTypeCode { get; init; }

    /// <summary>A cooking method code from the cooking vocabulary.</summary>
    public string? MethodCode { get; init; }

    public Guid? CreativeContextId { get; init; }

    public Guid? RecipeId { get; init; }

    /// <summary>A version of <see cref="RecipeId"/>. Not valid without it.</summary>
    public Guid? RecipeVersionId { get; init; }

    public Guid? PromptRecordId { get; init; }

    public Guid? MediaAssetId { get; init; }

    /// <summary>A version of <see cref="MediaAssetId"/>. Not valid without it.</summary>
    public int? MediaAssetVersionNumber { get; init; }

    public Guid? SocialPackageId { get; init; }
}

/// <summary>
/// The body of <c>PATCH .../content-cards/{cardId}</c> (13.6a, KAN-004): a JSON Merge Patch over a card.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A field not mentioned is left alone, a field sent with a value is set, and a field sent as
/// <c>null</c> is cleared.</strong> Nothing is inferred from what was left out — in particular, no linked
/// record is ever unlinked by a patch that did not send its field as <c>null</c>.
/// </para>
/// <para>
/// <see cref="ExpectedConcurrencyToken"/> is not a card field: it is the <c>concurrencyToken</c> of the read
/// this edit was composed against. There is no weekday — it is the planned date's own — and
/// <see cref="Column"/> is here only so that sending one can be refused by name rather than ignored.
/// </para>
/// </remarks>
public sealed record UpdateContentCardViewModel
{
    public string? ExpectedConcurrencyToken { get; init; }

    public PatchField<string?> Title { get; init; }

    public PatchField<string?> Description { get; init; }

    /// <summary>Not editable: a card changes column through the move route. Refused when sent.</summary>
    public PatchField<object?> Column { get; init; }

    public PatchField<string?> ChannelKey { get; init; }

    public PatchField<DateOnly?> PlannedDate { get; init; }

    public PatchField<string?> CuisineCode { get; init; }

    public PatchField<string?> DishTypeCode { get; init; }

    public PatchField<string?> MethodCode { get; init; }

    public PatchField<Guid?> CreativeContextId { get; init; }

    public PatchField<Guid?> RecipeId { get; init; }

    public PatchField<Guid?> RecipeVersionId { get; init; }

    public PatchField<Guid?> PromptRecordId { get; init; }

    public PatchField<Guid?> MediaAssetId { get; init; }

    public PatchField<int?> MediaAssetVersionNumber { get; init; }

    public PatchField<Guid?> SocialPackageId { get; init; }
}

/// <summary>
/// The body of <c>POST .../content-cards/{cardId}/move</c> (13.6b, KAN-005): where the card should be.
/// </summary>
/// <remarks>
/// One request covers both a move to another column and a new place in the same one; which it is follows from
/// <see cref="Column"/> and where the card is now. The place is named by a neighbour rather than by a number,
/// so it means the same thing whatever part of the board the client is showing.
/// </remarks>
public sealed record MoveContentCardViewModel
{
    /// <summary>The <c>concurrencyToken</c> of the read this move was composed against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }

    /// <summary>The column the card should be in. Required, even when it is the one the card is already in.</summary>
    public ContentCardColumn? Column { get; init; }

    /// <summary>
    /// The card this one goes immediately before, which must be in <see cref="Column"/>. Left out, the card
    /// goes to the bottom of the column.
    /// </summary>
    public Guid? BeforeCardId { get; init; }

    /// <summary>Why it moved, in the creator's words. Only for a move to another column, where it is kept on the move.</summary>
    public string? Note { get; init; }
}

/// <summary>What a card's deletion does about the picture the card points at.</summary>
public enum ContentCardLinkedAssetAction
{
    /// <summary>The picture stays in the library exactly as it is. What happens when nothing is said.</summary>
    Keep = 1,

    /// <summary>The picture is removed from the library with the card. Never the default.</summary>
    Remove = 2,
}

/// <summary>
/// The body of <c>DELETE .../content-cards/{cardId}</c> (13.6c, KAN-006).
/// </summary>
/// <remarks>
/// Deleting a card never removes anything from the library unless <see cref="LinkedAsset"/> says
/// <see cref="ContentCardLinkedAssetAction.Remove"/> in so many words. A picture is the creator's own work and
/// outlives the plan that pointed at it.
/// </remarks>
public sealed record DeleteContentCardViewModel
{
    [Description("Must be true. States that a person confirmed this deletion; a request without it is refused.")]
    public bool? Confirmed { get; init; }

    /// <summary>The <c>concurrencyToken</c> of the read this deletion was decided against. Required.</summary>
    public string? ExpectedConcurrencyToken { get; init; }

    /// <summary>What to do about the card's linked picture. Left out, it is kept.</summary>
    public ContentCardLinkedAssetAction? LinkedAsset { get; init; }

    /// <summary>The picture's own <c>concurrencyToken</c>. Required with <see cref="ContentCardLinkedAssetAction.Remove"/>, and only then.</summary>
    public string? LinkedAssetConcurrencyToken { get; init; }
}

/// <summary>
/// The shape rules for creating a card, as <c>(field, message)</c>. One place, read by the validator at the
/// edge and again by Business as the backstop for a caller that reaches it without the facade.
/// </summary>
public static class ContentCardInputChecks
{
    /// <summary>Trimmed, and null when nothing is left. The form every optional text field is stored in.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// The shape rules for an edit: only what was sent is judged. What a patch may do to the card's lineage
    /// depends on what the card already points at, so those rules are Business's.
    /// </summary>
    public static IEnumerable<(string Field, string Message)> Update(UpdateContentCardViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The one field that may be changed but never cleared.
        if (model.Title.IsSubmitted)
        {
            if (Normalize(model.Title.Value) is not { } title)
            {
                yield return ("title", "A card needs a title.");
            }
            else if (title.Length > ContentCardPolicy.TitleMaxLength)
            {
                yield return ("title", $"A title can be at most {ContentCardPolicy.TitleMaxLength} characters.");
            }
        }

        if (model.Description.TryGetSubmitted(out var description)
            && Normalize(description) is { Length: > ContentCardPolicy.DescriptionMaxLength })
        {
            yield return ("description", $"A description can be at most {ContentCardPolicy.DescriptionMaxLength} characters.");
        }

        // A move is its own route, with its own history. Refused rather than ignored: a client that sent a
        // column expected the card to move, and saying nothing would leave it believing it had.
        if (model.Column.IsSubmitted)
        {
            yield return ("column", "A card changes column by being moved, not by being edited.");
        }

        if (model.ChannelKey.TryGetSubmitted(out var channelKey)
            && Normalize(channelKey) is { Length: > ContentPolicy.ChannelKeyMaxLength })
        {
            yield return ("channelKey", $"A channel key is at most {ContentPolicy.ChannelKeyMaxLength} characters.");
        }

        foreach (var (field, code) in new[]
                 {
                     ("cuisineCode", model.CuisineCode), ("dishTypeCode", model.DishTypeCode), ("methodCode", model.MethodCode),
                 })
        {
            if (code.TryGetSubmitted(out var value) && Normalize(value) is { Length: > ContentCardPolicy.VocabularyCodeMaxLength })
            {
                yield return (field, $"A code is at most {ContentCardPolicy.VocabularyCodeMaxLength} characters.");
            }
        }

        foreach (var (field, id) in new[]
                 {
                     ("creativeContextId", model.CreativeContextId), ("recipeId", model.RecipeId),
                     ("recipeVersionId", model.RecipeVersionId), ("promptRecordId", model.PromptRecordId),
                     ("mediaAssetId", model.MediaAssetId), ("socialPackageId", model.SocialPackageId),
                 })
        {
            if (id.TryGetSubmitted(out var value) && value == Guid.Empty)
            {
                yield return (field, "That is not an identifier.");
            }
        }

        if (model.MediaAssetVersionNumber.TryGetSubmitted(out var version) && version < 1)
        {
            yield return ("mediaAssetVersionNumber", "Version numbers start at 1.");
        }
    }

    /// <summary>The shape rules for a move. Whether a note is allowed depends on where the card is now, which is Business's.</summary>
    public static IEnumerable<(string Field, string Message)> Move(MoveContentCardViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Column is not { } column || !Enum.IsDefined(column))
        {
            yield return ("column", "Name the column the card should be in: ToDo, InProcess, ReadyForReview or Approved.");
        }

        if (model.BeforeCardId == Guid.Empty)
        {
            yield return ("beforeCardId", "That is not an identifier.");
        }

        if (Normalize(model.Note) is { Length: > ContentCardPolicy.MoveNoteMaxLength })
        {
            yield return ("note", $"A note can be at most {ContentCardPolicy.MoveNoteMaxLength} characters.");
        }
    }

    /// <summary>The shape rules for a deletion. Whether the card has a picture to remove is Business's to know.</summary>
    public static IEnumerable<(string Field, string Message)> Delete(DeleteContentCardViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // A deletion should be a step a creator took, not somewhere a well-formed body arrives.
        if (model.Confirmed is not true)
        {
            yield return ("confirmed", "Confirm the deletion by sending confirmed as true.");
        }

        if (model.LinkedAsset is { } action && !Enum.IsDefined(action))
        {
            yield return ("linkedAsset", "Say Keep or Remove.");
        }

        var removing = model.LinkedAsset is ContentCardLinkedAssetAction.Remove;
        var hasAssetToken = !string.IsNullOrWhiteSpace(model.LinkedAssetConcurrencyToken);

        if (removing && !hasAssetToken)
        {
            yield return (
                "linkedAssetConcurrencyToken",
                "Send the picture's own concurrencyToken to remove it: read the picture first, so you know what you are removing.");
        }

        // A token for a picture that is being kept is a request that does not say what it means.
        if (!removing && hasAssetToken)
        {
            yield return ("linkedAssetConcurrencyToken", "Send this only with linkedAsset set to Remove.");
        }
    }

    public static IEnumerable<(string Field, string Message)> Create(CreateContentCardViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Normalize(model.Title) is not { } title)
        {
            yield return ("title", "A card needs a title.");
        }
        else if (title.Length > ContentCardPolicy.TitleMaxLength)
        {
            yield return ("title", $"A title can be at most {ContentCardPolicy.TitleMaxLength} characters.");
        }

        if (Normalize(model.Description) is { Length: > ContentCardPolicy.DescriptionMaxLength })
        {
            yield return ("description", $"A description can be at most {ContentCardPolicy.DescriptionMaxLength} characters.");
        }

        if (model.Column is { } column && !Enum.IsDefined(column))
        {
            yield return ("column", "Name a column: ToDo, InProcess, ReadyForReview or Approved.");
        }

        if (Normalize(model.ChannelKey) is { Length: > ContentPolicy.ChannelKeyMaxLength })
        {
            yield return ("channelKey", $"A channel key is at most {ContentPolicy.ChannelKeyMaxLength} characters.");
        }

        foreach (var (field, code) in new[]
                 {
                     ("cuisineCode", model.CuisineCode), ("dishTypeCode", model.DishTypeCode), ("methodCode", model.MethodCode),
                 })
        {
            if (Normalize(code) is { Length: > ContentCardPolicy.VocabularyCodeMaxLength })
            {
                yield return (field, $"A code is at most {ContentCardPolicy.VocabularyCodeMaxLength} characters.");
            }
        }

        foreach (var (field, id) in new[]
                 {
                     ("creativeContextId", model.CreativeContextId), ("recipeId", model.RecipeId),
                     ("recipeVersionId", model.RecipeVersionId), ("promptRecordId", model.PromptRecordId),
                     ("mediaAssetId", model.MediaAssetId), ("socialPackageId", model.SocialPackageId),
                 })
        {
            if (id == Guid.Empty)
            {
                yield return (field, "That is not an identifier.");
            }
        }

        // A version is a version of something. Without its parent the pin would name a version of nothing.
        if (model.RecipeVersionId is not null && model.RecipeId is null)
        {
            yield return ("recipeVersionId", "Name the recipe this is a version of.");
        }

        if (model.MediaAssetVersionNumber is { } version)
        {
            if (model.MediaAssetId is null)
            {
                yield return ("mediaAssetVersionNumber", "Name the picture this is a version of.");
            }
            else if (version < 1)
            {
                yield return ("mediaAssetVersionNumber", "Version numbers start at 1.");
            }
        }
    }
}

public sealed class UpdateContentCardViewModelValidator : AbstractValidator<UpdateContentCardViewModel>
{
    public UpdateContentCardViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in ContentCardInputChecks.Update(model))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class MoveContentCardViewModelValidator : AbstractValidator<MoveContentCardViewModel>
{
    public MoveContentCardViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in ContentCardInputChecks.Move(model))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class DeleteContentCardViewModelValidator : AbstractValidator<DeleteContentCardViewModel>
{
    public DeleteContentCardViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in ContentCardInputChecks.Delete(model))
            {
                context.AddFailure(field, message);
            }
        });
}

public sealed class CreateContentCardViewModelValidator : AbstractValidator<CreateContentCardViewModel>
{
    public CreateContentCardViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in ContentCardInputChecks.Create(model))
            {
                context.AddFailure(field, message);
            }
        });
}
