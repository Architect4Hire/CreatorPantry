namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Limits for the workspace brand profile, shared by request validation and EF configuration.
/// </summary>
public static class BrandPolicy
{
    public const int BrandNameMaxLength = 200;

    public const int ShortDescriptionMaxLength = 500;

    public const int DefaultAudienceMaxLength = 500;

    /// <summary>A BCP-47 language tag; 35 is the length the RFC recommends providing for.</summary>
    public const int LocaleMaxLength = 35;

    /// <summary>An IANA zone identifier such as <c>America/Chicago</c>.</summary>
    public const int TimeZoneIdMaxLength = 64;

    /// <summary>
    /// An opaque key into the channel profiles that own channel constraints. Not a provider name: channel
    /// constraints live in channel profiles and adapters, never here (content.md).
    /// </summary>
    public const int ChannelKeyMaxLength = 64;

    /// <summary>The creator's note on why an edit was made, recorded on its revision.</summary>
    public const int ReasonMaxLength = 500;

    public const int UrlMaxLength = 2048;

    public const int LinkLabelMaxLength = 200;

    /// <summary>Caps per profile, so one request or import cannot attach unbounded rows.</summary>
    public const int MaxChannelDefaults = 20;

    public const int MaxLinks = 20;

    public const int MaxAssetLinks = 10;

    public const int SourceDocumentTitleMaxLength = 200;

    public const int SourceDocumentAudienceMaxLength = 500;

    public const int SourceTagNameMaxLength = 64;

    /// <summary>An RFC 6838 media type: two 127-character names and the slash between them.</summary>
    public const int MediaTypeMaxLength = 255;

    /// <summary>The name the file arrived with. Shown back to the creator; never part of an object key.</summary>
    public const int OriginalFileNameMaxLength = 255;

    /// <summary>A server-generated blob name under the workspace/document/version prefix.</summary>
    public const int ObjectKeyMaxLength = 512;

    /// <summary><c>sha256:</c> plus 64 hex characters.</summary>
    public const int ChecksumMaxLength = 71;

    /// <summary>The largest source file accepted, whatever its format.</summary>
    public const long SourceUploadMaxBytes = 20 * 1024 * 1024;

    /// <summary>The largest Markdown, plain-text or HTML source accepted: these are read whole to be checked.</summary>
    public const long SourceTextUploadMaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// The upload route's request-body limit: the largest file plus room for the multipart framing and the
    /// metadata fields. The gateway's route for the same path carries the same number.
    /// </summary>
    public const long SourceUploadRequestMaxBytes = SourceUploadMaxBytes + (1024 * 1024);

    /// <summary>The most pixels a source image may declare, so a small file cannot describe an enormous canvas.</summary>
    public const long SourceImageMaxPixels = 50_000_000;

    /// <summary>Caps on a DOCX package, against an archive that expands far past its upload size.</summary>
    public const int SourceDocxMaxEntries = 2000;

    public const long SourceDocxMaxUncompressedBytes = 200L * 1024 * 1024;

    public const int MaxSourceDocumentTags = 10;

    public const int StyleGuideDisplayNameMaxLength = 200;

    /// <summary>The creator's own words on what a guide is for.</summary>
    public const int StyleGuidePurposeMaxLength = 500;

    public const int StyleGuideSectionBodyMaxLength = 8000;

    public const int StyleGuideRuleTextMaxLength = 500;

    /// <summary>Caps per guide version, so one request or accepted proposal cannot attach unbounded rows.</summary>
    public const int MaxStyleGuideRules = 50;

    public const int MaxStyleGuideChannelVariants = 20;

    public const int MaxStyleGuideSourceLinks = 50;

    /// <summary>The largest extracted-text artifact stored. Longer text is truncated and says so.</summary>
    /// <remarks>
    /// Below <see cref="SourceUploadMaxBytes"/> deliberately: a 20 MB DOCX is mostly images, and a document
    /// whose text alone runs past four megabytes is not brand evidence anybody is going to read. Truncation is
    /// recorded on the extraction's reason rather than applied silently.
    /// </remarks>
    public const long ExtractedTextMaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The correction route's request-body limit: room for the longest text a creator may submit, once it has
    /// been written out as JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twice <see cref="ExtractedTextMaxBytes"/> plus a megabyte, because the limit applies to the encoded body
    /// and the text inside it is escaped: quotes, backslashes and newlines each cost two bytes where they cost
    /// one in the stored artifact, so a legitimate four-megabyte text can arrive as more than four megabytes of
    /// JSON. Without the headroom the edge would refuse a correction the domain would have accepted.
    /// </para>
    /// <para>
    /// Deliberately a limit and not an absence of one: what this buys is that the <em>domain</em> decides a text
    /// is too long, with a stable code and a field error naming it, rather than the transport refusing it as a
    /// 413 with nothing a client can act on. The gateway's route for the same path carries the same number.
    /// </para>
    /// </remarks>
    public const long ExtractionCorrectionRequestMaxBytes = (ExtractedTextMaxBytes * 2) + (1024 * 1024);

    /// <summary>The most pages a PDF is read from, against a file that declares thousands.</summary>
    public const int ExtractionMaxPdfPages = 500;

    /// <summary>The most blocks — headings, paragraphs, list items — one extraction produces.</summary>
    public const int ExtractionMaxBlocks = 50_000;

    /// <summary>A parser's stable identity, recorded as provenance: <c>pdf/pdfpig@0.1.16</c>.</summary>
    public const int ExtractorIdMaxLength = 100;

    /// <summary>Why an extraction operation gave up, in operator-facing terms. Never creator content.</summary>
    public const int ExtractionFailureSummaryMaxLength = 1000;

    /// <summary>
    /// How many times an extraction may be claimed before it is abandoned for good.
    /// </summary>
    /// <remarks>
    /// The bound on the lease-recovery cycle, for the reason <c>AiOperation.Attempts</c> gives: a document
    /// that kills its worker every time would otherwise cycle between Running and Queued indefinitely. Four
    /// rather than the AI queue's three, because nothing here spends a provider budget on a pass.
    /// </remarks>
    public const int ExtractionMaxAttempts = 4;

    /// <summary>How many due operations one claim pass reads at once.</summary>
    public const int ExtractionClaimBatchSize = 10;

    /// <summary>
    /// How long a claim holds before another worker may take the operation.
    /// </summary>
    /// <remarks>
    /// Generous for the work: the ceiling is a 20 MB file parsed in this process, which is seconds. There is
    /// deliberately no heartbeat — a renewal hook is what to add if that stops being true, rather than a
    /// longer lease, because a long lease delays recovery from a worker that actually died.
    /// </remarks>
    public static readonly TimeSpan ExtractionLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How often the Worker's extraction loop looks for work.</summary>
    public static readonly TimeSpan ExtractionPollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often lapsed extraction leases are recovered. Shorter than <see cref="ExtractionLeaseDuration"/>,
    /// so a worker that died is noticed without sweeping on every claim pass.
    /// </summary>
    public static readonly TimeSpan ExtractionMaintenanceInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many dimensions a brand-source embedding has, and therefore the width of the stored vector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One number for the whole product, by necessity.</strong> A <c>vector(n)</c> column fixes n in
    /// DDL, so the dimension is a schema fact rather than a configuration one: every environment's
    /// <c>embeddings</c> deployment must produce vectors this wide, including Foundry Local in development. The
    /// small local models — all-MiniLM-L6-v2 at 384, nomic-embed-text at 768 — are therefore not usable here.
    /// Development points <c>embeddings</c> at a 1536-dimension deployment even when <c>chat</c> stays local.
    /// </para>
    /// <para>
    /// 1536 because that is what text-embedding-3-small and ada-002 return, and because SQL Server 2025 caps a
    /// float32 vector at 1,998 dimensions: text-embedding-3-large's native 3,072 cannot be stored at all
    /// without the preview float16 base type, and would have to be requested at a reduced width instead.
    /// </para>
    /// <para>
    /// A change here is a migration, not a setting, and it invalidates every stored chunk: a vector is only
    /// comparable with others from the same model at the same width, which is why
    /// <see cref="Data.Entities.BrandSourceChunkSet"/> records both and retrieval filters on the model.
    /// </para>
    /// </remarks>
    public const int EmbeddingDimension = 1536;

    /// <summary>The embedding deployment's stable identity, as <c>text-embedding-3-small</c>.</summary>
    public const int EmbeddingModelMaxLength = 100;

    /// <summary>
    /// A chunking strategy's stable identity, as <c>text/paragraph-1600c-200o@1</c>.
    /// </summary>
    /// <remarks>
    /// The role <see cref="ExtractorIdMaxLength"/> plays for an extraction. A strategy change moves boundaries,
    /// which changes what every vector in a set means, so a chunk nobody can attribute to a named chunker is
    /// one nobody can tell is stale.
    /// </remarks>
    public const int ChunkerIdMaxLength = 100;

    /// <summary>
    /// The characters one chunk aims for: roughly 400 tokens at the usual four-characters-per-token rule.
    /// </summary>
    /// <remarks>
    /// A character budget rather than a token count, deliberately. Counting tokens exactly would mean a
    /// tokenizer dependency in this assembly, per-model and provider-shaped, to place a boundary that is
    /// approximate anyway — the chunker's job is passages a creator would recognize, not packing a context
    /// window to the last token. <see cref="ChunkTextMaxLength"/> is what keeps the approximation safe.
    /// </remarks>
    public const int ChunkTargetLength = 1600;

    /// <summary>Characters repeated from the previous chunk, so a passage split mid-sentence stays findable.</summary>
    public const int ChunkOverlapLength = 200;

    /// <summary>
    /// The longest chunk text stored, and the width of the column that holds it.
    /// </summary>
    /// <remarks>
    /// Well above <see cref="ChunkTargetLength"/> so a chunker that will not split a long unbroken paragraph
    /// has somewhere to put it, and far below every embedding model's input limit so that no stored chunk can
    /// be silently truncated by the provider.
    /// </remarks>
    public const int ChunkTextMaxLength = 4000;

    /// <summary>
    /// The most UTF-8 bytes one chunk may span in the extracted artifact.
    /// </summary>
    /// <remarks>
    /// Four times <see cref="ChunkTextMaxLength"/>, because the offsets are byte offsets into the stored UTF-8
    /// artifact while the column is measured in UTF-16 characters, and a character costs up to four bytes
    /// there. The two limits describe one slice in two units; neither implies the other.
    /// </remarks>
    public const int ChunkMaxBytes = ChunkTextMaxLength * 4;

    /// <summary>Doubling backoff before a requeued extraction is claimable again: 15s, 30s, 60s, capped at five minutes.</summary>
    public static TimeSpan ExtractionBackoffFor(int attempts)
    {
        var exponent = Math.Max(0, attempts - 1);
        var seconds = Math.Min(15 * Math.Pow(2, exponent), TimeSpan.FromMinutes(5).TotalSeconds);

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>How many passages one embedding request carries. Bounded so a failure costs one batch, not a run.</summary>
    /// <remarks>
    /// Sixteen passages is roughly 6,400 tokens at the chunker's target — far under every embedding model's
    /// per-request limit, and small enough that a rate-limited or failed call wastes little to repeat.
    /// </remarks>
    public const int EmbeddingBatchSize = 16;

    /// <summary>How many times an embedding operation may be claimed before it is abandoned for good.</summary>
    public const int EmbeddingMaxAttempts = 4;

    /// <summary>How many due embedding operations one claim pass reads at once.</summary>
    public const int EmbeddingClaimBatchSize = 10;

    /// <summary>
    /// How long an embedding claim holds before another worker may take the operation. Each written batch
    /// renews it, so the lease only has to outlast one provider call rather than the whole document.
    /// </summary>
    public static readonly TimeSpan EmbeddingLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How often the Worker's embedding loop looks for work.</summary>
    public static readonly TimeSpan EmbeddingPollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often lapsed embedding leases are recovered and stale chunk sets are retired.</summary>
    public static readonly TimeSpan EmbeddingMaintenanceInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a superseded chunk set is kept before it is deleted, so a reader that selected it a moment
    /// before the swap is not stranded.
    /// </summary>
    public static readonly TimeSpan SupersededSetRetention = TimeSpan.FromHours(1);

    /// <summary>How long a set may stay <c>Building</c> before the sweep treats it as abandoned.</summary>
    public static readonly TimeSpan AbandonedBuildAge = TimeSpan.FromDays(1);

    /// <summary>How many identifiers one maintenance step reads at once.</summary>
    public const int EmbeddingMaintenanceBatchSize = 50;

    /// <summary>
    /// How many source document versions one grounded answer may be built from.
    /// </summary>
    /// <remarks>
    /// A bound on how much creator text one request assembles, and on how many rows it has to resolve before a
    /// provider is called. Ten is more evidence than a brand guide needs and far less than a library holds.
    /// </remarks>
    public const int MaxGroundingSourceVersions = 10;

    /// <summary>How many passages one document version may contribute to a grounded answer.</summary>
    public const int MaxGroundingPassagesPerVersion = 12;

    /// <summary>
    /// How many passages one grounded answer may be built from in total, across every version it names.
    /// </summary>
    /// <remarks>
    /// Below <see cref="MaxGroundingSourceVersions"/> times <see cref="MaxGroundingPassagesPerVersion"/> on
    /// purpose: the per-version cap keeps one long document from being the whole answer, and this one keeps a
    /// wide selection from assembling a prompt nothing can read. Forty passages at
    /// <see cref="ChunkTargetLength"/> is roughly sixteen thousand tokens of reference material.
    /// </remarks>
    public const int MaxGroundingPassages = 40;

    /// <summary>
    /// Doubling backoff before a requeued embedding is claimable again: 30s, 60s, 120s, capped at ten minutes,
    /// spread by up to a fifth either way so a provider recovering from a rate limit is not met by every
    /// requeued operation at the same instant.
    /// </summary>
    /// <param name="attempts">How many times the operation has been claimed.</param>
    /// <param name="jitter">A value in [0, 1). Supplied rather than drawn here so a test can pin it.</param>
    public static TimeSpan EmbeddingBackoffFor(int attempts, double jitter)
    {
        var exponent = Math.Max(0, attempts - 1);
        var seconds = Math.Min(30 * Math.Pow(2, exponent), TimeSpan.FromMinutes(10).TotalSeconds);
        var spread = 1 + ((Math.Clamp(jitter, 0, 0.999999) * 0.4) - 0.2);

        return TimeSpan.FromSeconds(seconds * spread);
    }
}
