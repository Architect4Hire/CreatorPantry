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
    /// <summary>
    /// The most bytes one returned image may be, before the staging write abandons it.
    /// </summary>
    /// <remarks>
    /// A bound on what a provider may make this host hold, not a judgement about image quality. The write is
    /// metered as it streams, so a response that runs past this stores nothing rather than being read whole
    /// and then rejected.
    /// </remarks>
    public const long ImageMaxBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Whether one provider call may ask for more than one image.
    /// </summary>
    /// <remarks>
    /// False, and a constant rather than a remembered rule. <c>ImageGenerationOptions.Count</c> exists in the
    /// abstraction, but what an implementation does with it is the implementation's business: some return
    /// fewer images than asked for, some ignore it. Nothing has verified a provider here, because no image
    /// deployment is configured at all — so each variant is its own call, which is also what makes a partial
    /// result a real outcome rather than one inferred from a short list.
    /// </remarks>
    public const bool ProviderSupportsBatchGeneration = false;

    /// <summary>How many times a worker may claim one generation before it is abandoned.</summary>
    /// <remarks>
    /// Lower than the embedding queue's four. Every attempt that reaches the provider is charged for, and a
    /// request that has failed twice is far more likely to be a prompt the provider refuses than a blip.
    /// </remarks>
    public const int GenerationMaxAttempts = 3;

    /// <summary>How many due operations one claim pass inspects before giving up on winning a race.</summary>
    public const int GenerationClaimBatchSize = 10;

    /// <summary>How many lapsed leases one maintenance sweep recovers.</summary>
    public const int GenerationMaintenanceBatchSize = 50;

    /// <summary>
    /// How long a claim holds an operation before the sweep may recover it.
    /// </summary>
    /// <remarks>
    /// Longer than the embedding lease, because this job makes up to four provider calls in series and image
    /// generation is slow. A lease that lapses while the worker is still inside a provider call is the one
    /// way two workers can both generate for one operation, which is the expensive mistake.
    /// </remarks>
    public static readonly TimeSpan GenerationLeaseDuration = TimeSpan.FromMinutes(15);

    /// <summary>How long one provider call may take before it is treated as unavailable.</summary>
    public static readonly TimeSpan GenerationProviderTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How often the worker looks for due generations.</summary>
    public static readonly TimeSpan GenerationPollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often the sweep recovers lapsed leases.</summary>
    public static readonly TimeSpan GenerationMaintenanceInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Doubling backoff before a requeued generation is claimable again: 30s, 60s, 120s, capped at ten
    /// minutes, spread by up to a fifth either way.
    /// </summary>
    /// <remarks>
    /// The same curve as <c>BrandPolicy.EmbeddingBackoffFor</c>, and the jitter matters more here: a provider
    /// that rate-limited four variants of one request rate-limited them within seconds of each other, so an
    /// unspread backoff would send the whole batch back at the same instant.
    /// </remarks>
    /// <param name="attempts">How many times the operation has been claimed.</param>
    /// <param name="jitter">A value in [0, 1). Supplied rather than drawn here so a test can pin it.</param>
    public static TimeSpan GenerationBackoffFor(int attempts, double jitter)
    {
        var exponent = Math.Max(0, attempts - 1);
        var seconds = Math.Min(30 * Math.Pow(2, exponent), TimeSpan.FromMinutes(10).TotalSeconds);
        var spread = 1 + ((Math.Clamp(jitter, 0, 0.999999) * 0.4) - 0.2);

        return TimeSpan.FromSeconds(seconds * spread);
    }

    /// <summary>How many staged images one retention pass expires, purges, or reconciles.</summary>
    public const int RetentionBatchSize = 100;

    /// <summary>How many staging objects one reconciliation page holds.</summary>
    /// <remarks>
    /// One database round trip per page, so a page is worth making large. Blob listings cap at 5,000 and
    /// this is well inside that.
    /// </remarks>
    public const int ReconciliationPageSize = 500;

    /// <summary>
    /// How many pages one workspace's reconciliation walks before it stops for this sweep.
    /// </summary>
    /// <remarks>
    /// A bound on one workspace's share of an hourly sweep, not a bound on correctness: what a very large
    /// workspace does not reach this hour it reaches the next, and an orphan is in no hurry. Without it a
    /// single workspace with an enormous prefix could hold the sweep open past its own interval.
    /// </remarks>
    public const int ReconciliationMaxPages = 20;

    /// <summary>How often the retention sweep runs.</summary>
    /// <remarks>
    /// Hourly, not by the minute. Nothing here is urgent: an image's deadline is a fortnight away, and a
    /// sweep that lists a storage container is the most expensive background read this product makes.
    /// </remarks>
    public static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// How old an unreferenced staging object must be before reconciliation will remove it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the whole safety of orphan reconciliation.</strong> A generation writes an object
    /// and then commits the row that owns it, so between those two steps every in-flight image looks
    /// exactly like an orphan. A sweep without this window would race the job and delete images a creator
    /// was about to be shown.
    /// </para>
    /// <para>
    /// Comfortably longer than <see cref="GenerationLeaseDuration"/>, because the longest a worker can
    /// legitimately sit between writing an object and committing its row is bounded by its lease: once the
    /// lease lapses the operation is recovered and that worker's writes are refused. An hour of margin on
    /// top costs nothing — the bytes are already paid for and the row is what decides whether a creator
    /// ever sees them.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(2);

    /// <summary>The largest multipart body a DAM upload may carry.</summary>
    /// <remarks>
    /// Above <see cref="ImageMaxBytes"/>, because a multipart body is the file plus its metadata parts and
    /// a request refused at the edge cannot say which field was at fault. The file's own limit is what
    /// actually bounds what is stored.
    /// </remarks>
    public const long AssetUploadRequestMaxBytes = ImageMaxBytes + (1024 * 1024);

    /// <summary>The longest an asset title may be.</summary>
    public const int AssetTitleMaxLength = 200;

    /// <summary>The longest an asset description may be.</summary>
    public const int AssetDescriptionMaxLength = 2000;

    /// <summary>
    /// The longest alt text may be.
    /// </summary>
    /// <remarks>
    /// Generous rather than tight. Alt text that has to describe a plated dish for someone who cannot see
    /// it is a sentence or three, and a limit that forces a creator to truncate is a limit that produces
    /// worse descriptions than no limit at all.
    /// </remarks>
    public const int AltTextMaxLength = 1000;

    /// <summary>The longest a channel, platform or style key may be. Matches the content module's.</summary>
    public const int VocabularyKeyMaxLength = 64;

    /// <summary>The longest a rights holder or attribution line may be.</summary>
    public const int RightsTextMaxLength = 500;

    /// <summary>The longest a creator's own filename may be, kept for display only.</summary>
    public const int FileNameMaxLength = 260;

    /// <summary>The longest a campaign name may be.</summary>
    public const int CampaignNameMaxLength = 200;

    /// <summary>The longest a utilization note may be.</summary>
    public const int NotesMaxLength = 2000;
}
