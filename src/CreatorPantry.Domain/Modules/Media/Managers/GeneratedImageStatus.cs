namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Where one staged generated image has got to. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// <para>
/// A staged image is not a DAM asset and never becomes one by sitting here: <see cref="Kept"/> records that
/// the creator chose it, and 12.9 is what makes an asset from a kept one. <see cref="Rejected"/> and
/// <see cref="Expired"/> are the two ways bytes become deletable, and 12.8's sweep is what deletes them.
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

    /// <summary>The creator chose it. 12.9 makes a DAM asset from it; retention passes to that asset.</summary>
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
