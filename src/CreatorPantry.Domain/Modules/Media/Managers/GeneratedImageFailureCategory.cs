namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The stable categories a failed image generation is recorded under.
/// </summary>
/// <remarks>
/// <para>
/// Strings rather than an enum, because <c>GeneratedImageOperation.FailureCategory</c> is a string column:
/// an operation's failure is read by a creator-facing surface and by a sweep deciding what is worth
/// retrying, and a name survives both better than a number nobody can renumber. Constants rather than
/// literals so the two readers cannot drift.
/// </para>
/// <para>
/// <strong>This is the whole vocabulary a creator is told.</strong> Nothing a provider said is ever stored
/// or logged: an image provider's error body can quote the prompt back, and a prompt is private creator
/// content (ai.md). The summary beside the category is one of this application's own sentences.
/// </para>
/// </remarks>
public static class GeneratedImageFailureCategory
{
    /// <summary>No image deployment is configured for this host. Recoverable by configuring one.</summary>
    public const string ProviderNotConfigured = "provider-not-configured";

    /// <summary>
    /// A deployment answered but will not say which model it is, so provenance could not be written.
    /// </summary>
    /// <remarks>
    /// Terminal, and not a technicality. <c>GeneratedImage.ProviderName</c> and <c>ModelName</c> are
    /// required columns because a staged image with an unnamed origin can never afterwards be told from one
    /// whose origin is known — and a creator deciding whether to publish an image is entitled to know what
    /// made it. Nothing is staged rather than something whose provenance is a guess.
    /// </remarks>
    public const string ModelUnidentified = "model-unidentified";

    /// <summary>The provider could not be reached, timed out, or failed transiently on every attempt.</summary>
    public const string ProviderUnavailable = "provider-unavailable";

    /// <summary>The provider rate-limited this workspace on every attempt the operation had.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>
    /// The provider refused the request and would refuse it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal and never retried: a refusal is a decision about the request, so an identical second call
    /// buys the same answer and another charge.
    /// </para>
    /// <para>
    /// <strong>Deliberately not "content-blocked".</strong> Telling a safety refusal from a malformed
    /// request or a rejected credential means reading a provider SDK's own exception type, which this
    /// module may not name — the Ai module solves the same problem with a provider-specific
    /// <c>IAiFailureClassifier</c>, and that seam lives in its <c>Gateways</c> namespace, which no other
    /// module may cross into. Naming this what it is beats guessing: a creator told their prompt was
    /// blocked when the real fault was an expired key would go and rewrite a prompt that was never wrong.
    /// A narrower category arrives with a real deployment and a classifier that can see the difference.
    /// </para>
    /// </remarks>
    public const string ProviderRefused = "provider-refused";

    /// <summary>
    /// The provider answered, but with nothing this can stage.
    /// </summary>
    /// <remarks>
    /// No image content at all, or a link instead of bytes. A provider-supplied URL is never fetched — that
    /// is a request this host makes to an address a response chose, and no response gets to pick what this
    /// server connects to.
    /// </remarks>
    public const string InvalidResponse = "invalid-response";

    /// <summary>The returned bytes are not a supported image format, whatever the provider labelled them.</summary>
    public const string UnsupportedMediaType = "unsupported-media-type";

    /// <summary>The returned bytes start like an image and do not hold together as one.</summary>
    public const string CorruptImage = "corrupt-image";

    /// <summary>The returned image is larger than this workspace will stage, in bytes or in pixels.</summary>
    public const string ImageTooLarge = "image-too-large";

    /// <summary>The scanner refused the returned bytes.</summary>
    public const string MalwareDetected = "malware-detected";

    /// <summary>Private staging storage could not be reached, on every attempt the operation had.</summary>
    public const string Storage = "storage";

    /// <summary>Every attempt was claimed and never completed: a worker died, or kept dying, holding the lease.</summary>
    public const string LeaseAbandoned = "lease-abandoned";

    /// <summary>
    /// The categories a later attempt could plausibly get past.
    /// </summary>
    /// <remarks>
    /// Used by the sweep to decide whether a lapsed lease is worth requeueing, and asserted against the
    /// constants above so a category added without a decision about retrying it is a test failure rather
    /// than a silent "no".
    /// </remarks>
    public static readonly IReadOnlySet<string> Retryable =
        new HashSet<string>(StringComparer.Ordinal) { ProviderUnavailable, RateLimited, Storage };

    /// <summary>Every category this application records. The set a reader has to be able to name.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ProviderNotConfigured,
        ModelUnidentified,
        ProviderUnavailable,
        RateLimited,
        ProviderRefused,
        InvalidResponse,
        UnsupportedMediaType,
        CorruptImage,
        ImageTooLarge,
        MalwareDetected,
        Storage,
        LeaseAbandoned,
    };
}
