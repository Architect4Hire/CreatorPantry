namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>How a card came to be on the board (13.7a).</summary>
/// <remarks>
/// <para>
/// <strong>It exists for one rule: a piece of work has at most one card the Content Pipeline put there.</strong>
/// A creator may plan the same work on as many cards as they like by hand, so "one card per piece of work"
/// cannot be a rule about cards in general. It is a rule about the pipeline's commit, which must be safe to
/// send again a minute later or a month later, and the store keeps it: a unique index over the work's id,
/// filtered to <see cref="ContentPipeline"/>.
/// </para>
/// <para>
/// Stored as its integer, so members are appended and never renumbered. Zero is not an origin.
/// </para>
/// </remarks>
public enum ContentCardOrigin
{
    /// <summary>Put on the board directly: by hand, or as a recipe package. The default.</summary>
    Board = 1,

    /// <summary>The card the Content Pipeline made for the piece of work it names.</summary>
    ContentPipeline = 2,
}
