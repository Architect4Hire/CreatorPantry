using System.Text.Encodings.Web;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// 11A.24: writes the same three samples twice — once with no brand context at all, once grounded in the guide
/// version the creator selected — so the difference a creator is shown is a difference the server actually
/// produced.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two provider calls, and that is the capability.</strong> One call holding the guide while writing
/// both halves would make the left-hand column a sample written <em>with</em> the guide and labelled
/// "without", which is the single claim this screen must not make. The calls differ in exactly one respect:
/// the second carries the package's <c>PREFERENCES</c> and <c>REFERENCES</c> segments and the first carries
/// neither. Same template, same output schema, same validator, same subject.
/// </para>
/// <para>
/// <strong>The lease is renewed between them.</strong> Two sequential provider calls is the case
/// <see cref="AiTaskExecutionContext.RenewLeaseAsync"/> exists for; racing the lease here would let a recovery
/// requeue an operation whose first call had already been paid for.
/// </para>
/// <para>
/// <strong>An empty guide is refused before anything is spent.</strong> The package is assembled first, and a
/// version with no guidance and no rules cannot demonstrate anything: both columns would come back identical
/// and the creator would have paid for two generations to see that. Assembly makes no provider call, so the
/// refusal is free.
/// </para>
/// <para>
/// <strong>Nothing here can become content.</strong> The scope is
/// <see cref="AiOperationScope.NotApplicable"/>, no recipe is named, and the two target kinds the samples are
/// stored under are absent from <see cref="AiChangeApplicability"/> and answer <c>null</c> in
/// <see cref="AiChangeTargetPolicy"/>. There is no acceptance route.
/// </para>
/// </remarks>
internal sealed class BrandStyleTestDriveAiTaskHandler(
    IAiCompletionGateway gateway,
    IBrandContextAssembler brandContext,
    IPromptTemplateStore templates,
    IClock clock) : IAiTaskHandler
{
    private static readonly JsonSerializerOptions SubjectJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.RecipeId is not null || context.RecipeVersionId is not null)
        {
            return Failure(
                AiFailureCategory.Validation,
                "A style test drive names no recipe; its subject is a brand guide version.");
        }

        if (BrandContextRequestInputs.Read(context.Inputs) is not { } selection)
        {
            return Failure(AiFailureCategory.Validation, "The brand context this test drive asked for cannot be read.");
        }

        if (!selection.UseBrandVoice || selection.Guide is null)
        {
            return Failure(
                AiFailureCategory.Validation,
                "A style test drive names the guide version it is testing; there is no default here.");
        }

        var package = await Assemble(selection, cancellationToken);

        if (package is null)
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "The guide version this test drive named is no longer available.");
        }

        if (package.Guidance.Count == 0 && package.Rules.Count == 0)
        {
            // Before the first call, so this costs nothing. Two identical columns are not a comparison, and a
            // creator should not pay to discover that the version they picked is still empty.
            return Failure(
                AiFailureCategory.DomainInvalid,
                "That guide version has nothing for a test drive to show yet, so nothing was generated.");
        }

        var template = templates.Get(AiTaskCatalog.BrandStyleTestDrive);
        var subject = Subject(context.Inputs);
        var attempts = new List<AiAttemptRecord>();

        var plain = await CallAsync(context, template, subject, brandContext: null, cancellationToken);
        attempts.AddRange(plain.Attempts);

        if (!plain.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(plain.Failure!.Category, plain.Failure.Message, attempts);
        }

        await context.RenewLeaseAsync(cancellationToken);

        var guided = await CallAsync(context, template, subject, package, cancellationToken);
        attempts.AddRange(guided.Attempts);

        if (!guided.Succeeded)
        {
            // Both halves or neither. A one-sided result would be shown as a comparison, and the creator would
            // be reading a column labelled "with your guide" that does not exist.
            return AiTaskHandlerOutcome.ForFailure(guided.Failure!.Category, guided.Failure.Message, attempts);
        }

        var (changes, warnings) = Translate(plain.Document!, guided.Document!, package);
        var attempt = attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,

            // Both null: a test drive pins no recipe version, so the assembler's staleness rule compares
            // nothing against nothing, which is the correct answer for a task that reads no recipe.
            pinnedVersionId: null,
            currentVersionId: null,
            new AiOutputDocument { SchemaVersion = template.OutputSchemaVersion, Warnings = warnings },
            changes,
            new AiProposalProvenance(
                template.OutputSchemaVersion,
                template.Id,
                template.Version.ToString(),
                template.BodyChecksum,
                attempt.ProviderName,
                attempt.ModelName,
                attempt.ModelDeployment),
            clock.UtcNow,
            package);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, attempts)
            : AiTaskHandlerOutcome.ForFailure(assembly.Failure!.Category, assembly.Failure.Message, attempts);
    }

    private async Task<BrandContextPackage?> Assemble(
        BrandContextRequestSelection selection, CancellationToken cancellationToken)
    {
        // ToRequest fixes the channel at null for every caller, which is what a test drive wants: one package
        // grounds the blog introduction and the caption together, and a channel key would pull a social variant
        // over the long-form guidance the introduction needs.
        var request = selection.ToRequest(AiTaskType.BrandStyleTestDrive);

        if (request is null)
        {
            return null;
        }

        var assembled = await brandContext.AssembleAsync(request, cancellationToken);

        return assembled.Succeeded ? assembled.Value : null;
    }

    /// <summary>
    /// One half of the comparison. <paramref name="brandContext"/> is null for the half that must not see the
    /// guide; everything else about the call is identical, which is what makes the two halves comparable.
    /// </summary>
    private async Task<AiCompletionOutcome<AiBrandStyleSamplesOutputDocument>> CallAsync(
        AiTaskExecutionContext context,
        PromptTemplate template,
        string subject,
        BrandContextPackage? brandContext,
        CancellationToken cancellationToken)
    {
        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)))
            .WithOutputSchema(AiBrandStyleSamplesOutputSchema.Json)

            // The creator's own words when they named a subject, so it travels as data rather than instruction
            // even though the platform's default is the server's own string.
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(new { subject }, SubjectJson));

        if (brandContext is not null)
        {
            if (BrandContextPromptRenderer.Guidance(brandContext) is { } guidance)
            {
                builder = builder.WithPreferences(context.WorkspaceId, guidance);
            }

            if (BrandContextPromptRenderer.Excerpts(brandContext) is { } excerpts)
            {
                builder = builder.AddReference(context.WorkspaceId, excerpts);
            }
        }

        return await gateway.CompleteAsync(
            new AiCompletionRequest<AiBrandStyleSamplesOutputDocument>(
                builder.Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiBrandStyleSamplesOutputValidator.AsDelegate()),
            cancellationToken);
    }

    /// <summary>
    /// The two documents as the rows a proposal is stored as: three pairs, each pair a sample written both ways.
    /// </summary>
    /// <remarks>
    /// Ordered by sample and then by variant, so the pair the screen shows side by side is adjacent in
    /// <c>SortOrder</c> as well. A warning that named a sample points at the row from its own half, which is why
    /// the index is computed from both the sample and the variant rather than from the sample alone.
    /// </remarks>
    /// <remarks>
    /// Internal rather than private so the pairing can be asserted directly. A stored warning names its change
    /// by database id, and ids do not exist until the proposal is written — so at the handler's own level the
    /// attachment is invisible, and a test that went through the assembler would be comparing two empty guids
    /// and passing for the wrong reason.
    /// </remarks>
    internal static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiBrandStyleSamplesOutputDocument plain,
        AiBrandStyleSamplesOutputDocument guided,
        BrandContextPackage package)
    {
        var changes = new List<AiResolvedChange>();
        var indexBySample = new Dictionary<(AiBrandStyleSample Sample, bool Guided), int>();

        foreach (var sample in AiBrandStyleSampleCatalog.All)
        {
            foreach (var isGuided in (bool[])[false, true])
            {
                indexBySample[(sample, isGuided)] = changes.Count;

                changes.Add(new AiResolvedChange(
                    AiChangeKind.Set,
                    isGuided
                        ? AiChangeTargetKind.BrandStyleSampleWithGuide
                        : AiChangeTargetKind.BrandStyleSampleWithoutGuide,

                    // No target row: a sample is not a change to anything that exists.
                    TargetId: null,
                    AiBrandStyleSampleCatalog.ToWire(sample),

                    // Never the other half. The column written without the guide is a sample in its own right,
                    // not the "before" of the column written with it — and BeforeValue is documented as a value
                    // the server read from a pinned source, which a test drive does not have.
                    BeforeValue: null,
                    AiBrandStyleSampleCatalog.TextOf(isGuided ? guided.Samples : plain.Samples, sample),
                    ProposedPosition: null,
                    changes.Count));
            }
        }

        var warnings = new List<AiOutputWarning>();

        foreach (var (document, isGuided) in (ValueTuple<AiBrandStyleSamplesOutputDocument, bool>[])
            [(plain, false), (guided, true)])
        {
            foreach (var warning in document.Warnings)
            {
                warnings.Add(new AiOutputWarning
                {
                    Kind = warning.Kind,

                    // Labelled, as every other capability labels a model's own caution: a creator reading this
                    // beside a server finding is entitled to know which of the two said it.
                    Message = AiPolicy.ModelWarningLabel + warning.Message,
                    ChangeIndex = warning.Sample is { } named && indexBySample.TryGetValue((named, isGuided), out var index)
                        ? index
                        : null,
                });
            }
        }

        // The server's own findings, after the model's, so they cannot be displaced by its warning limit and
        // cannot be left out by anything it says. The prompt forbids a safety, allergen, dietary or health
        // claim and tells the model that the creator's guidance does not relax that; this is what notices when
        // a guide rule talked it into one anyway.
        //
        // They warn rather than refuse. Nothing here can be accepted or published, so refusing the whole
        // comparison over one phrase would cost the creator the demonstration they paid for and tell them less
        // than a warning does — a sample that makes the claim their own guide asked for is precisely what they
        // need to see, with the server saying so beside it.
        foreach (var (document, isGuided) in (ValueTuple<AiBrandStyleSamplesOutputDocument, bool>[])
            [(plain, false), (guided, true)])
        {
            foreach (var sample in AiBrandStyleSampleCatalog.All)
            {
                var text = AiBrandStyleSampleCatalog.TextOf(isGuided ? guided.Samples : plain.Samples, sample);

                foreach (var finding in AiEditorialClaimScanner.ScanTextForSafetyClaims(text))
                {
                    warnings.Add(new AiOutputWarning
                    {
                        Kind = finding.Kind,
                        Message = $"[{finding.Code}] {finding.Message}",
                        ChangeIndex = indexBySample[(sample, isGuided)],
                    });
                }
            }
        }

        // What the package could not supply, said by the server rather than left to the model to mention.
        warnings.AddRange(BrandContextNotices.For(package));

        return (changes, warnings);
    }

    private static string Subject(IReadOnlyDictionary<string, string>? inputs) =>
        BrandStyleTestDriveInputs.ReadSubject(inputs);

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string summary) =>
        AiTaskHandlerOutcome.ForFailure(category, summary, []);
}
