namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Where one staged generated image has got to. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// <para>
/// A staged image is not a DAM asset and never becomes one by sitting here: <see cref="Kept"/> records that
/// an asset was made from it. All three of <see cref="Kept"/>, <see cref="Rejected"/> and
/// <see cref="Expired"/> make the staging bytes deletable, and 12.8's sweep is what deletes them — the
/// first because something else now owns a copy, the other two because nobody wanted them.
/// </para>
/// <para>
/// Every state but <see cref="Staged"/> is terminal. A creator who rejects an image and changes their mind
/// generates again — reviving a rejected image would mean the retention deadline had been extended by a
/// decision nobody recorded.
/// </para>
/// </remarks>
public enum GeneratedImageStatus
{
    /// <summary>
    /// Not declared. Never valid on a stored row — the check constraint refuses it.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The bytes are in private staging storage, waiting for the creator to choose.
    /// </summary>
    /// <remarks>
    /// The only state the retention sweep looks at, which is why its index is filtered to this value: a kept
    /// image's deadline is a historical fact rather than a thing to act on.
    /// </remarks>
    Staged = 1,

    /// <summary>
    /// The creator chose it, and a DAM asset was made from it in the same transaction (12.9a).
    /// </summary>
    /// <remarks>
    /// <strong>Its staging bytes are redundant from the moment it reaches this state</strong>, because a
    /// committed asset owns a copy of them — so the retention sweep collects them like a rejected image's,
    /// and retention proper passes to the asset. That is how "copy or move" is settled safely: the staged
    /// copy is only removed once something else demonstrably holds it.
    /// </remarks>
    Kept = 2,

    /// <summary>
    /// The creator did not choose it.
    /// </summary>
    /// <remarks>
    /// IMG-006's rule, and the restriction this prompt states outright: a rejected image is not a DAM asset.
    /// Nothing in the DAM reads a row in this state, and the bytes are the sweep's to remove.
    /// </remarks>
    Rejected = 3,

    /// <summary>
    /// The retention deadline passed before the creator chose, and the sweep expired it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Rejected"/> on purpose: "I did not want this" and "nobody looked in time" are
    /// different facts about a creator's work, and only the second is worth telling them about.
    /// </remarks>
    Expired = 4,
}
