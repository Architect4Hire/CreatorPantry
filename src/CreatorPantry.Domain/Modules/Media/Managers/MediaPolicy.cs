namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The fixed limits and deadlines of the generated-image workspace.
/// </summary>
public static class MediaPolicy
{
    /// <summary>
    /// How long a staged image waits for the creator before the sweep may expire it.
    /// </summary>
    /// <remarks>
    /// The same fortnight <c>AiPolicy.ProposalTimeToLive</c> allows a proposal, and for the same reason:
    /// reviewing generated work is creative work a creator returns to, not a notification they action. Long
    /// enough to be forgiving; short enough that private staging storage does not quietly become an archive
    /// of images nobody chose.
    /// </remarks>
    public static readonly TimeSpan StagedImageTimeToLive = TimeSpan.FromDays(14);

    /// <summary>The most variants one request may ask a provider for.</summary>
    /// <remarks>
    /// Four is a contact sheet a creator can actually compare. It is also a spend guard: every variant is a
    /// separate charge, and an uncapped count is the easiest way to spend an allowance by mistyping a number.
    /// </remarks>
    public const int MaxVariantsPerOperation = 4;

    /// <summary>The fewest variants a request may ask for.</summary>
    public const int MinVariantsPerOperation = 1;

    /// <summary>
    /// The longest a staging object key may be.
    /// </summary>
    /// <remarks>
    /// Server-generated and opaque, so this is a storage bound rather than a creator-facing one — see
    /// <c>GeneratedImage.ObjectKey</c> for why it is never a URL.
    /// </remarks>
    public const int ObjectKeyMaxLength = 400;

    /// <summary>The longest a stored media type may be.</summary>
    public const int MediaTypeMaxLength = 100;

    /// <summary>The longest a stored content checksum may be, including its algorithm prefix.</summary>
    public const int ChecksumMaxLength = 80;

    /// <summary>The longest a stored provider or model identifier may be.</summary>
    public const int ProviderIdentifierMaxLength = 200;

    /// <summary>
    /// The longest a sanitized provider failure summary may be.
    /// </summary>
    /// <remarks>
    /// Sanitized before it is stored, and never the provider's raw body: an image provider's error can quote
    /// the prompt back, and a prompt is private creator content that <c>ai.md</c> keeps out of logs and rows
    /// that are not the prompt itself.
    /// </remarks>
    public const int FailureSummaryMaxLength = 1000;

    /// <summary>
    /// The most pixels a staged image may claim, as a sanity bound on what a provider returned.
    /// </summary>
    /// <remarks>
    /// The same ceiling the brand source library applies to an upload. A provider is trusted to return what
    /// was asked for, but a stored dimension is read back by code that sizes things, and a nonsense value is
    /// better refused at the boundary than carried.
    /// </remarks>
    public const long ImageMaxPixels = 50_000_000;
}
