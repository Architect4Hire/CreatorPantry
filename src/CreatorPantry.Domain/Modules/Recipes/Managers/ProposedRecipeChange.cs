namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>What one accepted change does to the recipe.</summary>
/// <remarks>
/// A deliberate near-duplicate of the AI module's own change kind. Neither module may name the other's
/// internal types, so the vocabulary is restated at the boundary and the caller translates into it — which is
/// also the point at which a kind this module cannot apply becomes impossible to express.
/// </remarks>
public enum ProposedRecipeChangeKind
{
    /// <summary>Replace one field's value on the target.</summary>
    Set = 1,

    /// <summary>Add the named child. Only a tag, whose whole content is its name.</summary>
    Add = 2,

    /// <summary>Remove the named child.</summary>
    Remove = 3,

    /// <summary>Move the named child to a position among its siblings.</summary>
    Move = 4,
}

/// <summary>Which part of the recipe an accepted change addresses.</summary>
/// <remarks>
/// Shorter than the AI module's target list on purpose: these are the parts the recipe patch contract has a
/// field for, and so the only parts an accepted change can reach through ordinary recipe validation.
/// Ingredients, equipment and asset links are absent because <c>UpdateRecipeViewModel</c> has no field for
/// them.
/// </remarks>
public enum ProposedRecipeTarget
{
    /// <summary>The recipe's own header fields. Named by no id.</summary>
    Recipe = 1,

    InstructionGroup = 2,

    InstructionStep = 3,

    /// <summary>A tag, named by its <c>WorkspaceTag</c> id on a removal and by its text on an addition.</summary>
    Tag = 4,
}

/// <summary>
/// One change a creator accepted, addressed structurally, as the recipe module receives it.
/// </summary>
/// <param name="TargetId">
/// The row the change addresses; <c>null</c> when the target is the recipe itself or a tag being added.
/// </param>
/// <param name="FieldName">
/// The field to set, in the same vocabulary the proposal used — <c>headnote</c>, <c>text</c>,
/// <c>durationMinutes</c>. Required for <see cref="ProposedRecipeChangeKind.Set"/> and <c>null</c> otherwise.
/// </param>
/// <param name="Value">
/// The accepted value, as an invariant string. <c>null</c> clears the field, which is a legitimate accepted
/// change for anything but a title. Carries the tag's text for an addition.
/// </param>
/// <param name="Position">Where a move puts the child among its siblings, zero-based.</param>
/// <remarks>
/// <para>
/// <strong>No before value.</strong> The caller computed one against a pinned version and showed it to the
/// creator, and this module re-checks that pinned version by id — so a before value arriving here would be a
/// second, weaker version of a check already made, with nothing to do if it disagreed.
/// </para>
/// <para>
/// Strings rather than typed values, because the diff a creator reviewed was strings: a value that parsed
/// differently on the way in than it displayed on the way out would be a different change from the one they
/// accepted. Parsing happens once, here, against the field it is destined for.
/// </para>
/// </remarks>
public sealed record ProposedRecipeChange(
    ProposedRecipeChangeKind Kind,
    ProposedRecipeTarget Target,
    Guid? TargetId,
    string? FieldName,
    string? Value,
    int? Position);
