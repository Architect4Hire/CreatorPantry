namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Why a <c>CreativeContextReference</c> is on the context: what the work takes from it, or what the
/// work produced.</summary>
/// <remarks>
/// <para>
/// <strong>It replaces a convention that could not survive a second picture.</strong> Until AF.4.3 the working
/// surfaces read "the picture this work takes its cues from" as *the first* picture reference, and kept exactly
/// one so that reading held. The Content Pipeline's keepers are pictures the work *made*, several of them, so
/// the first-one rule stopped being able to tell a cue from an output — and the surface that replaces a cue
/// removes the reference it found, which would have removed a keeper.
/// </para>
/// <para>
/// <strong>A purpose is not a kind.</strong> The kind says what a row points at; this says what it is for. The
/// same picture can honestly be both — an output of this run, and the cue the next prompt is planned from — so
/// the two picture kinds are unique per purpose rather than per target. Every other kind is named once however
/// it is used.
/// </para>
/// <para>
/// Starts at one, as <see cref="CreativeContextReferenceKind"/> does: zero is not a purpose, so a row whose
/// purpose was never set is refused by the range check rather than quietly read as a source. Stored as its
/// integer, so members are appended and never renumbered.
/// </para>
/// </remarks>
public enum CreativeContextReferencePurpose
{
    /// <summary>
    /// Something the work draws on: the recipe it is about, a picture it takes cues from, a prompt it reuses.
    /// The default, and what every reference made before AF.4.3 is.
    /// </summary>
    Source = 1,

    /// <summary>
    /// Something the work produced and the creator chose to keep: a generated picture marked as a keeper, and
    /// the library asset it becomes once it is saved.
    /// </summary>
    Keeper = 2,
}
