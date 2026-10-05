namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// How sure one observation of a reference photograph is (IMG-004).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Required on every observation, which is the point of it.</strong> IMG-004's SCOPE asks for
/// observations "with uncertainty", and uncertainty expressed in prose is uncertainty a creator skims past:
/// "probably unglazed ceramic" and "unglazed ceramic" read almost alike in a paragraph and differently in a
/// column. Making it a field means the screen can show it, the stored row keeps it, and a model cannot
/// quietly drop it — an answer without one fails validation rather than arriving as confident.
/// </para>
/// <para>
/// Three levels rather than a percentage. A model's "0.82" is not a calibrated probability and printing one
/// would dress a guess as a measurement; three bands are what a creator can actually act on.
/// </para>
/// </remarks>
public enum AiReferenceImageConfidence
{
    /// <summary>Not declared. Never valid in an answer — the validator refuses it.</summary>
    /// <remarks>
    /// The whole reason this enum exists is that uncertainty must be stated, so an omission defaulting to
    /// anything at all would defeat it. Zero is that omission, and it is refused by name.
    /// </remarks>
    Unspecified = 0,

    /// <summary>Plainly visible in the photograph.</summary>
    Clear = 1,

    /// <summary>Consistent with the photograph, but an inference from it rather than a reading of it.</summary>
    Probable = 2,

    /// <summary>
    /// Not really determinable from this photograph.
    /// </summary>
    /// <remarks>
    /// Worth saying rather than omitting: "the surface cannot be made out" tells a creator their reference
    /// does not answer that question, where silence leaves them assuming it was read and agreed with.
    /// </remarks>
    Unclear = 3,
}
