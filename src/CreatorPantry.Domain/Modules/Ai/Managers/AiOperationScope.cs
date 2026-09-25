namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which parts of the source an operation's proposal may touch.
/// </summary>
/// <remarks>
/// <para>
/// A bound the server enforces, not a label. A proposal whose structured changes reach outside its
/// operation's scope is rejected rather than trimmed: the creator asked for a bounded change, and silently
/// widening it is the failure this column exists to make impossible.
/// </para>
/// <para>
/// Recorded on the operation rather than inferred from the proposal, so the bound is fixed before the model
/// is called and cannot be argued about afterwards from the output.
/// </para>
/// </remarks>
public enum AiOperationScope
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>Anything in the recipe. The widest scope, and the one that needs the closest review.</summary>
    WholeRecipe = 1,

    /// <summary>Ingredient lines, groups, and their quantities.</summary>
    Ingredients = 2,

    /// <summary>Instruction steps and their grouping.</summary>
    Instructions = 3,

    /// <summary>Title, description, headnote, tags, times, and yield — the recipe's framing, not its method.</summary>
    Metadata = 4,

    /// <summary>Alt text, visual briefs, and asset links. Never the bytes of an original upload.</summary>
    Media = 5,
}
