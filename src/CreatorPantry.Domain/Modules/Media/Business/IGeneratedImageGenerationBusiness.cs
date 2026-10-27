using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Media.Business;

/// <summary>How one claimed generation ended, for the worker's pass summary.</summary>
public enum GeneratedImageRunOutcome
{
    /// <summary>Every variant asked for is staged.</summary>
    Generated = 1,

    /// <summary>Some variants are staged and the rest permanently failed.</summary>
    PartiallyGenerated = 2,

    /// <summary>Handed back for another attempt after a failure worth retrying.</summary>
    Requeued = 3,

    /// <summary>Nothing is staged and nothing more will be.</summary>
    Failed = 4,

    /// <summary>The worker is stopping. The lease is released uncharged.</summary>
    Cancelled = 5,

    /// <summary>Nothing was changed: the lease was lost, or the operation is unreadable in this workspace.</summary>
    Skipped = 6,
}

/// <summary>
/// Runs one image-generation request: one provider call per variant, each validated before it is staged.
/// </summary>
public interface IGeneratedImageGenerationBusiness
{
    Task<GeneratedImageRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>Records a request for this workspace, or returns the one its idempotency key already bought.</summary>
    Task<GeneratedImageRequestResult> RequestAsync(
        string promptText,
        string? avoidText,
        Guid? aiProposalId,
        int variantCount,
        Guid requestedByMembershipId,
        string idempotencyKey,
        GeneratedImageSubject subject,
        GeneratedImageQualityClass? qualityClass,
        CancellationToken cancellationToken);

    /// <summary>How many images one operation of the resolved workspace has staged.</summary>
    Task<int> StagedCountAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// What a request for <paramref name="variants"/> pictures at <paramref name="qualityClass"/> would cost the
    /// caller's account right now, priced for the model, tier and quality that class sends (AP.2.6, AP.3.1).
    /// Writes nothing.
    /// </summary>
    /// <param name="qualityClass">Already resolved by the facade; null on a host that offers no classes.</param>
    Task<GeneratedImageQuoteServiceModel> QuoteAsync(
        int variants, GeneratedImageQualityClass? qualityClass, CancellationToken cancellationToken);

    /// <summary>The quality classes this host offers, each at the rate it is charged (AP.3.1, B-55).</summary>
    GeneratedImageQualityClassesServiceModel ListQualityClasses();

    /// <summary>
    /// The class a request or quote is made at: the one named, or the default when none was (AP.3.1). False when
    /// the named class is not offered here — including any class at all on a host that offers none, where a
    /// request naming none is made at the deployment's own default and <paramref name="chosen"/> is null.
    /// </summary>
    bool TryChooseQualityClass(GeneratedImageQualityClass? requested, out GeneratedImageQualityClass? chosen);

    /// <summary>Whether this key has already bought an operation in the resolved workspace.</summary>
    Task<bool> IsReplayAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// How a prompt and its avoid list measure against the model <paramref name="qualityClass"/> sends to
    /// (AP.3.2), or null when there is nothing to measure against: no image deployment, or one that will not say
    /// its limit. Reads configuration only.
    /// </summary>
    /// <param name="qualityClass">Already resolved; null on a host that offers no classes.</param>
    GeneratedImagePromptFitServiceModel? MeasurePrompt(
        string promptText, string? avoidText, GeneratedImageQualityClass? qualityClass);

    /// <summary>
    /// The in-progress cap and the daily variant budget (15.3), or null when this request may be queued. A key that
    /// has already bought an operation is never refused: its answer is the operation it bought.
    /// </summary>
    Task<OperationError?> RefuseIfOverLimitsAsync(
        string idempotencyKey, int variantCount, CancellationToken cancellationToken);

    /// <summary>
    /// One operation of the resolved workspace with its images, or null when it has none with that id.
    /// </summary>
    /// <remarks>
    /// <strong>No resource-level rule of its own, deliberately.</strong> An operation belongs to the workspace
    /// rather than to the member who asked for it: a colleague picking up someone's shoot is the ordinary case,
    /// and a read gated on `RequestedByMembershipId` would make shared work impossible while disclosing nothing
    /// extra. The workspace filter is the authorization, and the role gate is the facade's.
    /// </remarks>
    Task<GeneratedImageOperationDetailServiceModel?> FindDetailAsync(
        Guid operationId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageGenerationBusiness"/>
/// <remarks>
/// <para>
/// <strong>The order is the safety.</strong> Name the model, record it, then for each variant that has no
/// row: call the provider once, read what came back, stage it. Nothing a creator can see changes until a
/// variant's row commits, and every row that commits describes bytes that are actually in storage.
/// </para>
/// <para>
/// <strong>A failure about the bytes stops that variant; a failure about the provider stops the pass.</strong>
/// A corrupt image is one bad answer and the next variant may be fine — that is what partial success is.
/// A rate limit, an outage or a refusal is a fact about the request, and calling three more times would
/// buy three more charges for three more copies of the same answer.
/// </para>
/// <para>
/// Nothing here logs a prompt or a provider message. An image prompt is private creator content and an
/// image provider's error can quote it back (ai.md).
/// </para>
/// </remarks>
internal sealed class GeneratedImageGenerationBusiness(
    IGeneratedImageGenerationDataLayer dataLayer,
    IAiQuotaAdmissionFacade quota,
    IWorkspaceContext workspace,
    IClock clock,
    IOptions<LimitsOptions> limits,
    IOptions<GeneratedImageQualityClassOptions> qualityClasses,
    ILogger<GeneratedImageGenerationBusiness> logger) : IGeneratedImageGenerationBusiness
{
    /// <summary>
    /// What a request at <paramref name="qualityClass"/> sends, or null for the deployment's own default — the
    /// answer on a host that offers no classes.
    /// </summary>
    private GeneratedImageSettings? SettingsFor(GeneratedImageQualityClass? qualityClass) =>
        qualityClass is { } chosen
            ? GeneratedImageQualityClassCatalogue.Settings(qualityClasses.Value, chosen)
            : null;

    /// <summary>
    /// What will actually be sent: the class's model and settings, or the deployment's defaults when there is
    /// no class. Priced as one, so the quote, the hold and the request can never describe different pictures.
    /// </summary>
    private static GeneratedImageSettings Sent(GeneratedImageSettings? settings, GeneratedImageModel model) =>
        settings ?? new GeneratedImageSettings(null, model.ModelName!, model.Resolution, model.Quality);

    public async Task<OperationError?> RefuseIfOverLimitsAsync(
        string idempotencyKey, int variantCount, CancellationToken cancellationToken)
    {
        var options = limits.Value;

        if (!options.Enabled || await dataLayer.IsReplayAsync(idempotencyKey, cancellationToken))
        {
            return null;
        }

        var now = clock.UtcNow;
        var (inFlight, variantsToday) = await dataLayer.CountForLimitsAsync(LimitDay.StartOf(now), cancellationToken);

        if (inFlight >= options.ImageInFlightPerWorkspace)
        {
            LimitsTelemetry.Refused(LimitedOperation.ImageGeneration, "concurrency", LimitScope.Workspace);
            return LimitRefusal.Concurrency(
                LimitedOperation.ImageGeneration, LimitScope.Workspace, options.ImageInFlightPerWorkspace);
        }

        // All of this request or none of it: a request for four that would end the day at two over is refused,
        // rather than queued to be cut short by the worker.
        if (variantsToday + variantCount > options.ImageVariantsPerWorkspacePerDay)
        {
            LimitsTelemetry.Refused(LimitedOperation.ImageGeneration, "budget", LimitScope.Workspace);
            return LimitRefusal.Budget(
                LimitedOperation.ImageGeneration, options.ImageVariantsPerWorkspacePerDay, LimitDay.NextAfter(now));
        }

        LimitsTelemetry.Admitted(LimitedOperation.ImageGeneration, "budget", LimitScope.Workspace);

        return null;
    }

    public Task<GeneratedImageOperationDetailServiceModel?> FindDetailAsync(
        Guid operationId, CancellationToken cancellationToken) =>
        dataLayer.FindDetailAsync(operationId, cancellationToken);

    public Task<GeneratedImageRequestResult> RequestAsync(
        string promptText,
        string? avoidText,
        Guid? aiProposalId,
        int variantCount,
        Guid requestedByMembershipId,
        string idempotencyKey,
        GeneratedImageSubject subject,
        GeneratedImageQualityClass? qualityClass,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var now = clock.UtcNow;

        // What the pictures will cost, read from the deployment rather than guessed: the tier and quality are
        // decided by the model's own sizing profile, and holding allowance for a picture at a size nobody will
        // send would charge the wrong amount (AP.2.4, AP.2.5). A host with no image provider holds nothing —
        // the request is queued and the worker fails it as provider-not-configured, which is what it did
        // before allowance existed.
        var model = dataLayer.DescribeModel();

        // At the quality class's own model and settings when there is one (AP.3.1): its rate is what is held,
        // and so what is charged — the class's price is the row it sends, never a figure of its own (B-55).
        var sent = model is { Status: GeneratedImageModelStatus.Identified }
            ? Sent(SettingsFor(qualityClass), model)
            : null;

        var admission = sent is not null
            ? new GeneratedImageAdmissionRequest(workspace.AccountId, sent.ModelName, sent.Resolution, sent.Quality)
            : null;

        return dataLayer.RequestAsync(
            new GeneratedImageOperation
            {
                Id = Guid.NewGuid(),

                // From the resolved context, never a request field (tenancy.md). The ownership interceptor
                // stamps it as well; setting it here keeps the graph coherent before it is saved.
                WorkspaceId = workspace.WorkspaceId,
                Status = GeneratedImageOperationStatus.Requested,
                PromptText = promptText,
                AvoidText = avoidText,
                AiProposalId = aiProposalId,
                RecipeId = subject.RecipeId,
                RecipeVersionId = subject.RecipeVersionId,
                CreativeContextId = subject.CreativeContextId,
                VariantCount = variantCount,
                QualityClass = qualityClass,
                IdempotencyKey = idempotencyKey,
                RequestedByMembershipId = requestedByMembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                Attempts = 0,
                AvailableAt = now,
            },
            admission,
            cancellationToken);
    }

    public Task<int> StagedCountAsync(Guid operationId, CancellationToken cancellationToken) =>
        dataLayer.StagedCountAsync(operationId, cancellationToken);

    public async Task<GeneratedImageQuoteServiceModel> QuoteAsync(
        int variants, GeneratedImageQualityClass? qualityClass, CancellationToken cancellationToken)
    {
        // The same description RequestAsync admits against, so the quote prices the picture the request would
        // make: the model, the tier and the quality its sizing profile decides. A host with no image provider
        // has nothing to quote.
        var model = dataLayer.DescribeModel();

        if (model is not { Status: GeneratedImageModelStatus.Identified, ModelName: not null })
        {
            return GeneratedImageQuoteServiceModel.Unavailable(variants, qualityClass);
        }

        // The account from the resolved context — the creator asking — never a request field (USAGE-001). Priced
        // at what the class sends, by the same Sent the request admits with, so the quote is the hold.
        var quote = await dataLayer.QuoteAsync(
            workspace.AccountId, Sent(SettingsFor(qualityClass), model), variants, cancellationToken);

        return quote.Outcome switch
        {
            AiImageQuoteOutcome.Affordable or AiImageQuoteOutcome.Exhausted => new GeneratedImageQuoteServiceModel(
                quote.Outcome is AiImageQuoteOutcome.Affordable
                    ? GeneratedImageQuoteOutcome.Affordable
                    : GeneratedImageQuoteOutcome.Exhausted,
                quote.Unit.ToString(),
                quote.Variants,
                quote.PerPicture,
                quote.Total,
                quote.Allowance,
                quote.Remaining,
                quote.RemainingAfter,
                quote.AffordableVariants,
                quote.LeavesNearlySpent,
                quote.ResetsAt,
                quote.TimeZoneId,
                qualityClass),

            // No balance and no reset date, as the request's own suspension refusal carries none.
            AiImageQuoteOutcome.Suspended => new GeneratedImageQuoteServiceModel(
                GeneratedImageQuoteOutcome.Suspended,
                quote.Unit.ToString(), variants, null, null, null, null, null, 0, false, null, null, qualityClass),

            // Unpriced is this host's misconfiguration, not the creator's balance; it reads as no provider.
            _ => GeneratedImageQuoteServiceModel.Unavailable(variants, qualityClass),
        };
    }

    /// <remarks>
    /// <para>
    /// <strong>Each price is read, never written down (B-55).</strong> The rate <c>Ai:Quota:ImageRates</c> holds
    /// for what the class sends, through the AiUsage module's own converter — the figure a hold for one picture
    /// of it takes. A class with no rate is left off rather than shown at nothing: startup refuses such a host,
    /// so its absence here is a guard for a table changed while running, not a state anyone should see.
    /// </para>
    /// <para>
    /// Reads configuration only. No account and no period: a price list is the same for everyone who may ask.
    /// </para>
    /// </remarks>
    public bool TryChooseQualityClass(GeneratedImageQualityClass? requested, out GeneratedImageQualityClass? chosen)
    {
        var options = qualityClasses.Value;

        if (!GeneratedImageQualityClassCatalogue.Offers(options))
        {
            chosen = null;

            return requested is null;
        }

        chosen = GeneratedImageQualityClassCatalogue.Choose(options, requested);

        return chosen is not null;
    }

    public Task<bool> IsReplayAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        dataLayer.IsReplayAsync(idempotencyKey, cancellationToken);

    /// <remarks>
    /// The model is the one the class sends to, or the deployment's own when there is no class — the same
    /// choice <see cref="Sent"/> makes for pricing, so what is measured is what the worker will send to.
    /// </remarks>
    public GeneratedImagePromptFitServiceModel? MeasurePrompt(
        string promptText, string? avoidText, GeneratedImageQualityClass? qualityClass)
    {
        ArgumentNullException.ThrowIfNull(promptText);

        var modelName = SettingsFor(qualityClass)?.ModelName
            ?? (dataLayer.DescribeModel() is { Status: GeneratedImageModelStatus.Identified, ModelName: { } named }
                ? named
                : null);

        return modelName is not null && dataLayer.PromptCharacterLimit(modelName) is { } limit and > 0
            ? GeneratedImagePromptComposition.Measure(promptText, avoidText, limit)
            : null;
    }

    public GeneratedImageQualityClassesServiceModel ListQualityClasses()
    {
        var options = qualityClasses.Value;

        var classes = GeneratedImageQualityClassCatalogue.All(options)
            .Select(settings => (settings.QualityClass!.Value, Price: dataLayer.PriceInCredits(settings)))
            .Where(priced => priced.Price is not null)
            .Select(priced => new GeneratedImageQualityClassPriceServiceModel(
                priced.Value, priced.Price!.Value, priced.Value == options.DefaultClass))
            .ToList();

        // The default only when it is in the list, so a page never preselects a class it cannot show.
        return new GeneratedImageQualityClassesServiceModel(
            classes.Any(entry => entry.IsDefault) ? options.DefaultClass : null, AiQuotaUnit.Credits.ToString(), classes);
    }

    public async Task<GeneratedImageRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(operationId, leaseToken, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not failure. Handed back uncharged and immediately claimable, so the next worker
            // resumes at the first variant with no row — the ones already staged are kept. Not cancellable
            // by the token that was just cancelled.
            await dataLayer.ReleaseAsync(operationId, leaseToken, CancellationToken.None);

            return GeneratedImageRunOutcome.Cancelled;
        }
    }

    private async Task<GeneratedImageRunOutcome> RunAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var work = await dataLayer.OpenAsync(operationId, leaseToken, cancellationToken);

        switch (work.Outcome)
        {
            case GeneratedImageWriteOutcome.Missing:
                // Not readable in the workspace the worker resolved, which is what a claim naming another
                // workspace's row looks like. Nothing can be written: the row is workspace-owned.
                logger.LogWarning(
                    "Generation {OperationId} is not readable in its resolved workspace; its lease will expire.",
                    operationId);

                return GeneratedImageRunOutcome.Skipped;

            case GeneratedImageWriteOutcome.LeaseLost:
                return GeneratedImageRunOutcome.Skipped;
        }

        // The budget the request was admitted against, asked again before anything is spent (15.3). Two requests
        // that raced past the request-time check are told apart here: the later one is past the day's allowance in
        // request order, and fails before a single provider call rather than after.
        if (limits.Value.Enabled
            && await dataLayer.VariantsRequestedThroughAsync(operationId, cancellationToken) is { } through
            && through > limits.Value.ImageVariantsPerWorkspacePerDay)
        {
            LimitsTelemetry.Refused(LimitedOperation.ImageGeneration, "budget", LimitScope.Workspace);

            return await FailAsync(
                operationId,
                leaseToken,
                GeneratedImageFailureCategory.BudgetExhausted,
                "This workspace had already used today's image allowance when this request's turn came.",
                cancellationToken);
        }

        // The allowance this generation was admitted against, confirmed still live before a single picture is
        // made (PRICE-001). The hold is taken at the request and never renewed, so a generation that waited
        // long enough — behind its workspace's running cap — can be claimed after the expiry sweep has already
        // given it back. Settlement would charge nothing and the pictures would have been free.
        if (work.ReservationId is { } hold && !await quota.IsImageHoldLiveAsync(hold, cancellationToken))
        {
            return await FailAsync(
                operationId,
                leaseToken,
                GeneratedImageFailureCategory.Quota,
                "This request waited longer than the allowance held for it; ask for the pictures again.",
                cancellationToken);
        }

        var model = dataLayer.DescribeModel();

        switch (model.Status)
        {
            case GeneratedImageModelStatus.NotConfigured:
                return await FailAsync(
                    operationId,
                    leaseToken,
                    GeneratedImageFailureCategory.ProviderNotConfigured,
                    "No image deployment is configured for this host.",
                    cancellationToken);

            case GeneratedImageModelStatus.Unidentified:
                return await FailAsync(
                    operationId,
                    leaseToken,
                    GeneratedImageFailureCategory.ModelUnidentified,
                    "The image deployment does not report which provider and model it is.",
                    cancellationToken);
        }

        // What this generation's class sends, read now (AP.3.1). A class this host no longer offers is not sent
        // at some other setting: the hold priced that class, and a picture of anything else is not what was
        // asked for or paid for.
        var settings = SettingsFor(work.QualityClass);

        if (work.QualityClass is not null && settings is null)
        {
            return await FailAsync(
                operationId,
                leaseToken,
                GeneratedImageFailureCategory.ProviderNotConfigured,
                "The picture quality this request asked for is no longer offered on this host.",
                cancellationToken);
        }

        // The model actually asked, which for a class is the class's — not the deployment's default model.
        var asked = settings is null
            ? model
            : model with
            {
                ModelName = settings.ModelName,
                ModelDeployment = settings.ModelName,
                Resolution = settings.Resolution,
                Quality = settings.Quality,
            };

        if (await dataLayer.RecordProviderAsync(operationId, leaseToken, asked, cancellationToken)
            is not GeneratedImageWriteOutcome.Applied)
        {
            return GeneratedImageRunOutcome.Skipped;
        }

        var staged = work.StagedVariants ?? new HashSet<int>();
        var run = new GeneratedImagePassState(staged.Count);

        for (var variant = 0; variant < work.VariantCount; variant++)
        {
            if (staged.Contains(variant))
            {
                // The replay guard, and the reason a crashed attempt costs one variant rather than a whole
                // request: a row for this variant means its bytes are already in storage.
                continue;
            }

            if (!await RunVariantAsync(operationId, leaseToken, variant, work, settings, run, cancellationToken))
            {
                break;
            }
        }

        return await SettleAsync(operationId, leaseToken, work, run, cancellationToken);
    }

    /// <summary>
    /// Runs one variant. Returns whether the pass should go on to the next one.
    /// </summary>
    /// <remarks>
    /// False stops the pass. That is reserved for failures that are facts about the request or the
    /// provider — another call would buy the same answer and another charge. A bad image is a fact about
    /// one answer, so it records itself and the pass continues.
    /// </remarks>
    private async Task<bool> RunVariantAsync(
        Guid operationId,
        Guid leaseToken,
        int variant,
        GeneratedImageWork work,
        GeneratedImageSettings? settings,
        GeneratedImagePassState run,
        CancellationToken cancellationToken)
    {
        var generated = await dataLayer.GenerateAsync(work.PromptText, work.AvoidText, settings, cancellationToken);

        switch (generated.Outcome)
        {
            case GeneratedImageProviderOutcome.NotConfigured:
                run.Terminal(GeneratedImageFailureCategory.ProviderNotConfigured,
                    "No image deployment is configured for this host.");
                return false;

            case GeneratedImageProviderOutcome.RateLimited:
                run.Retryable(GeneratedImageFailureCategory.RateLimited,
                    "The image provider is rate-limiting this workspace.");
                return false;

            case GeneratedImageProviderOutcome.Unavailable:
                run.Retryable(GeneratedImageFailureCategory.ProviderUnavailable,
                    "The image provider could not be reached, or did not answer in time.");
                return false;

            case GeneratedImageProviderOutcome.Refused:
                run.Terminal(GeneratedImageFailureCategory.ProviderRefused,
                    "The image provider refused this request.");
                return false;

            case GeneratedImageProviderOutcome.Flagged:
                // Generated and withheld, which the provider bills for either way (AP.2.5) — so it is counted
                // before the pass stops. Terminal rather than retryable: the same prompt gets the same answer,
                // and asking again would buy another charge for it.
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Flagged, run, cancellationToken);
                run.Terminal(GeneratedImageFailureCategory.ProviderFlagged,
                    "The image provider made this picture and would not hand it over.");
                return false;

            case GeneratedImageProviderOutcome.InvalidResponse:
                // One bad answer rather than a verdict on the request: the next variant may well arrive.
                run.Terminal(GeneratedImageFailureCategory.InvalidResponse,
                    "The image provider answered without image data.");
                return true;
        }

        // Bytes arrived, so the provider has generated and billed for this picture whatever this host now
        // makes of it (AP.2.5). Which way it is counted is settled below, once its fate is known — a count
        // written only where staging succeeded would hand an account a free picture for every one of this
        // host's own refusals, and one written before the inspection would record a refused picture as a
        // staged one in the ledger.
        var inspection = GeneratedImageInspector.Inspect(generated.Bytes.Span);

        switch (inspection.Outcome)
        {
            case GeneratedImageInspectionOutcome.Empty:
            case GeneratedImageInspectionOutcome.Unsupported:
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Terminal(GeneratedImageFailureCategory.UnsupportedMediaType,
                    "The image provider returned something that is not a supported image format.");
                return true;

            case GeneratedImageInspectionOutcome.Corrupt:
            case GeneratedImageInspectionOutcome.TrailingData:
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Terminal(GeneratedImageFailureCategory.CorruptImage,
                    "The image provider returned an image that does not hold together as one.");
                return true;

            case GeneratedImageInspectionOutcome.TooLarge:
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Terminal(GeneratedImageFailureCategory.ImageTooLarge,
                    "The image provider returned an image with more pixels than this workspace will stage.");
                return true;

            case GeneratedImageInspectionOutcome.Accepted:
                break;

            default:
                // An outcome added to the inspector and not named here is a refusal, never a staged image.
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Terminal(GeneratedImageFailureCategory.CorruptImage,
                    "The image provider returned an image that does not hold together as one.");
                return true;
        }

        var stage = await dataLayer.StageVariantAsync(
            operationId,
            leaseToken,
            variant,
            generated.Bytes,
            inspection.MediaType!,
            inspection.Width,
            inspection.Height,
            generated.QualitySent,
            generated.SizeSent,
            cancellationToken);

        switch (stage)
        {
            case GeneratedImageStageOutcome.Staged:
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Staged, run, cancellationToken);
                run.Staged();
                return true;

            case GeneratedImageStageOutcome.AlreadyStaged:
                // Another attempt won this variant while this one was at the provider. Its bytes are the
                // ones in storage; these are discarded, which is the cost of losing that race. Still billed:
                // this pass asked the provider for a picture and the provider made one, so two passes racing
                // one variant cost two pictures however many of them end up in storage.
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Staged();
                return true;

            case GeneratedImageStageOutcome.Rejected:
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Terminal(GeneratedImageFailureCategory.MalwareDetected,
                    "The scanner refused the bytes the image provider returned.");
                return true;

            case GeneratedImageStageOutcome.TooLarge:
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Terminal(GeneratedImageFailureCategory.ImageTooLarge,
                    "The image provider returned more bytes than this workspace will stage.");
                return true;

            case GeneratedImageStageOutcome.Unavailable:
                // The picture was made and billed for; reaching storage is this host's problem, not the
                // provider's. Counted, then retried — and the retry buys another picture, which the provider
                // charges for again, which the count will say.
                await BillAsync(operationId, leaseToken, GeneratedImageBilling.Unusable, run, cancellationToken);
                run.Retryable(GeneratedImageFailureCategory.Storage,
                    "Private staging storage could not be reached.");
                return false;

            default:
                // The lease is gone, so nothing can be written — the count included. Another worker owns this
                // operation and its own pass is what will be billed.
                run.LeaseLost();
                return false;
        }
    }

    /// <summary>
    /// Counts one variant the provider generated and bills for, under the lease (AP.2.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns whether it was written. A lost lease is the only failure and it means another worker owns this
    /// operation, so the pass stops rather than generating pictures nobody will be charged for.
    /// </para>
    /// <para>
    /// <strong>Its own write, between provider calls.</strong> It has to survive a crash before the next
    /// variant: the settlement at the end charges from this count, and a count that lived only in memory would
    /// let a resumed generation re-run the variants it had already paid for and charge for neither pass.
    /// </para>
    /// </remarks>
    private async Task<bool> BillAsync(
        Guid operationId,
        Guid leaseToken,
        GeneratedImageBilling billing,
        GeneratedImagePassState run,
        CancellationToken cancellationToken)
    {
        if (await dataLayer.RecordBilledVariantAsync(operationId, leaseToken, billing, cancellationToken)
            is GeneratedImageWriteOutcome.Applied)
        {
            return true;
        }

        run.LeaseLost();

        return false;
    }

    /// <summary>
    /// Records what the pass achieved: every variant, some of them, or none.
    /// </summary>
    /// <remarks>
    /// A pass that staged something and then hit a retryable failure is still requeued, because the
    /// variants it did not reach are worth another attempt and the ones it staged cost nothing to keep.
    /// The attempt bound in <see cref="IGeneratedImageGenerationDataLayer.RequeueAsync"/> is what turns
    /// that into <see cref="GeneratedImageOperationStatus.PartiallySucceeded"/> rather than a loop.
    /// </remarks>
    private async Task<GeneratedImageRunOutcome> SettleAsync(
        Guid operationId,
        Guid leaseToken,
        GeneratedImageWork work,
        GeneratedImagePassState run,
        CancellationToken cancellationToken)
    {
        if (run.LeaseWasLost)
        {
            return GeneratedImageRunOutcome.Skipped;
        }

        if (run.StagedCount >= work.VariantCount)
        {
            return await SettleAsync(
                operationId,
                leaseToken,
                GeneratedImageOperationStatus.Succeeded,
                null,
                null,
                GeneratedImageRunOutcome.Generated,
                cancellationToken);
        }

        if (run.RetryableCategory is not null)
        {
            var requeued = await dataLayer.RequeueAsync(
                operationId,
                leaseToken,
                run.RetryableCategory,
                run.RetryableSummary!,
                Random.Shared.NextDouble(),
                cancellationToken);

            if (requeued.Outcome is not GeneratedImageWriteOutcome.Applied)
            {
                return GeneratedImageRunOutcome.Skipped;
            }

            return requeued.Abandoned
                ? run.StagedCount > 0
                    ? GeneratedImageRunOutcome.PartiallyGenerated
                    : GeneratedImageRunOutcome.Failed
                : GeneratedImageRunOutcome.Requeued;
        }

        // Nothing left to retry. Whatever is staged is what the creator gets, and the category says why
        // the rest is not there.
        var category = run.TerminalCategory ?? GeneratedImageFailureCategory.InvalidResponse;
        var summary = run.TerminalSummary ?? "The image provider returned fewer images than were asked for.";

        return run.StagedCount > 0
            ? await SettleAsync(
                operationId,
                leaseToken,
                GeneratedImageOperationStatus.PartiallySucceeded,
                category,
                summary,
                GeneratedImageRunOutcome.PartiallyGenerated,
                cancellationToken)
            : await SettleAsync(
                operationId,
                leaseToken,
                GeneratedImageOperationStatus.Failed,
                category,
                summary,
                GeneratedImageRunOutcome.Failed,
                cancellationToken);
    }

    /// <remarks>
    /// <strong>The one place a generation's allowance is reckoned</strong>, because it is the one place a
    /// generation ends — the data layer stages its ledger entries and settles its hold here.
    /// </remarks>
    private async Task<GeneratedImageRunOutcome> SettleAsync(
        Guid operationId,
        Guid leaseToken,
        GeneratedImageOperationStatus status,
        string? category,
        string? summary,
        GeneratedImageRunOutcome outcome,
        CancellationToken cancellationToken) =>
        await dataLayer.SettleAsync(operationId, leaseToken, status, category, summary, cancellationToken)
                is GeneratedImageWriteOutcome.Applied
            ? outcome
            : GeneratedImageRunOutcome.Skipped;

    /// <remarks>
    /// Every caller of this failed before a provider was reached — no deployment, no model identity, or the
    /// day's budget already spent — so nothing was generated, nothing is charged, and the hold is released
    /// whole by the settlement below.
    /// </remarks>
    private async Task<GeneratedImageRunOutcome> FailAsync(
        Guid operationId,
        Guid leaseToken,
        string category,
        string summary,
        CancellationToken cancellationToken) =>
        await SettleAsync(
            operationId,
            leaseToken,
            GeneratedImageOperationStatus.Failed,
            category,
            summary,
            GeneratedImageRunOutcome.Failed,
            cancellationToken);

    /// <summary>
    /// What one pass has learned so far: how many variants are staged, and the last reason one was not.
    /// </summary>
    /// <remarks>
    /// The last reason rather than every reason, because the operation records one category. A retryable
    /// reason outranks a terminal one when both happened: the terminal one is a fact about an answer
    /// already received, and the retryable one is the reason variants were never attempted at all.
    /// </remarks>
    private sealed class GeneratedImagePassState(int alreadyStaged)
    {
        public int StagedCount { get; private set; } = alreadyStaged;

        public string? RetryableCategory { get; private set; }

        public string? RetryableSummary { get; private set; }

        public string? TerminalCategory { get; private set; }

        public string? TerminalSummary { get; private set; }

        public bool LeaseWasLost { get; private set; }

        public void Staged() => StagedCount++;

        public void Retryable(string category, string summary)
        {
            RetryableCategory = category;
            RetryableSummary = summary;
        }

        public void Terminal(string category, string summary)
        {
            TerminalCategory = category;
            TerminalSummary = summary;
        }

        public void LeaseLost() => LeaseWasLost = true;
    }
}
