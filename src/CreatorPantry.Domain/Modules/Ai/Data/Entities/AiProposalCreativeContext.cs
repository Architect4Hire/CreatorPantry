using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// The creative context one proposal was generated from (AF.6.3): which piece of work, at which version, and
/// the checksum naming the assembled package exactly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The sibling of <see cref="AiProposalBrandContext"/>, for the other package a generation can be
/// grounded on.</strong> It exists so that whatever is later made from the proposal — a post revision, first —
/// can record the exact sources it was written from without re-reading them and getting a later answer.
/// </para>
/// <para>
/// <strong>Identifiers and a checksum, and no text.</strong> The working title, the picture brief and the
/// recipe lines are the creator's and stay where they live; <see cref="Checksum"/> already pins the words the
/// package carried, and a second copy here would be a second copy to erase.
/// </para>
/// <para>
/// <strong>The recipe pin is the package's first recipe, or nothing.</strong> A piece of work need not be about
/// a recipe. When it is, this names the immutable version that was read, which is what a post's staleness is
/// later decided against.
/// </para>
/// </remarks>
public class AiProposalCreativeContext : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiProposalId { get; set; }

    /// <summary>The piece of work. Not a foreign key; see the configuration for why.</summary>
    public Guid CreativeContextId { get; set; }

    /// <summary>The context's concurrency token when the package was assembled, or null when it carried none.</summary>
    public string? ContextVersion { get; set; }

    /// <summary>The package's content checksum, as <c>CreativeContextPackageSelection.Checksum</c> computed it.</summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>The recipe the package read first, or null. Set together with <see cref="RecipeVersionId"/>.</summary>
    public Guid? RecipeId { get; set; }

    /// <summary>The immutable version of that recipe the package read.</summary>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>What the package was estimated to cost. An estimate, and labelled one everywhere it surfaces.</summary>
    public int EstimatedTokens { get; set; }

    public DateTimeOffset AssembledAt { get; set; }
}
