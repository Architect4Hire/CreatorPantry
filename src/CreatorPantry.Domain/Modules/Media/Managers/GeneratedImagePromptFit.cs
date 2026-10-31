namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>How a prompt and its avoid list measure against the model a quality class sends to (AP.3.2).</summary>
public enum GeneratedImagePromptFitOutcome
{
    /// <summary>What would be sent is within the model's limit, and is sent as written.</summary>
    Fits = 1,

    /// <summary>
    /// Over the limit, with room left for a shorter prompt once the avoid list is set aside: the creator can ask
    /// for a compact variant of the prompt, and the avoid list goes with it word for word.
    /// </summary>
    NeedsShortening = 2,

    /// <summary>
    /// The avoid list alone leaves less than <see cref="MediaPolicy.ShortestPromptBudget"/> for the prompt. No
    /// shorter prompt can fix that, and the avoid list is the creator's and is never cut.
    /// </summary>
    AvoidTooLong = 3,
}

/// <summary>
/// What one prompt measures against one model.
/// </summary>
/// <param name="Outcome">Whether it fits, and if not, which part has to change.</param>
/// <param name="Limit">The model's prompt character limit.</param>
/// <param name="Length">The length of everything that would be sent: the prompt, and the avoid list under its heading.</param>
/// <param name="PromptBudget">
/// How long the prompt itself may be with the avoid list kept whole: the limit less the heading and the avoid
/// list. Below <see cref="MediaPolicy.ShortestPromptBudget"/> exactly when the outcome is
/// <see cref="GeneratedImagePromptFitOutcome.AvoidTooLong"/>, and may be negative then.
/// </param>
public sealed record GeneratedImagePromptFitServiceModel(
    GeneratedImagePromptFitOutcome Outcome, int Limit, int Length, int PromptBudget)
{
    public bool Fits => Outcome is GeneratedImagePromptFitOutcome.Fits;
}

/// <summary>
/// The one definition of what an image model is sent for a prompt and an avoid list, and of whether that fits
/// the model's limit (AP.3.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Measured here so it is never cut there.</strong> The generator refuses a prompt over its model's
/// limit rather than truncating it, and a refusal from the worker arrives after the pictures were held for. This
/// is the same arithmetic asked at the request, against the same string, so a request that would be refused is
/// refused before anything is held or queued.
/// </para>
/// <para>
/// <strong>The avoid list is set aside first, and never shortened.</strong> It is the creator's negative
/// guidance, and a shorter prompt that quietly lost "no cutlery" would make a picture they told it not to.
/// Whatever room is left after it is the prompt's.
/// </para>
/// </remarks>
public static class GeneratedImagePromptComposition
{
    /// <summary>What separates the avoid list from the prompt in what is sent.</summary>
    public const string AvoidHeading = "\n\nAvoid the following: ";

    /// <summary>
    /// Puts the avoid list after the prompt, under a plain heading of its own.
    /// </summary>
    /// <remarks>
    /// Both halves are the creator's own text and neither is a system instruction: an image provider is given
    /// one string, has no tools to be talked into using, and this workspace's words are all that is in it
    /// (ai.md). The heading is prose, not a delimiter a model is bound by — it tells the two apart for the
    /// provider and does not stop an avoid list worded as a directive from being read as one. That is
    /// acceptable here because the worst it can do is change a picture the same creator asked for.
    /// </remarks>
    public static string Compose(string prompt, string? avoid) =>
        string.IsNullOrWhiteSpace(avoid) ? prompt : $"{prompt}{AvoidHeading}{avoid}";

    /// <summary>How <paramref name="prompt"/> and <paramref name="avoid"/> measure against <paramref name="limit"/>.</summary>
    public static GeneratedImagePromptFitServiceModel Measure(string prompt, string? avoid, int limit)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var length = Compose(prompt, avoid).Length;
        var budget = limit - (length - prompt.Length);

        var outcome = length <= limit ? GeneratedImagePromptFitOutcome.Fits
            : budget < MediaPolicy.ShortestPromptBudget ? GeneratedImagePromptFitOutcome.AvoidTooLong
            : GeneratedImagePromptFitOutcome.NeedsShortening;

        return new GeneratedImagePromptFitServiceModel(outcome, limit, length, budget);
    }
}
