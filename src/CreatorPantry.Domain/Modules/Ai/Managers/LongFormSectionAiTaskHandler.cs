using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.LongFormSection"/> (LF.3.1): writes the body of one section of a long-form piece
/// whose plan the creator has approved (baseline B-33).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The model writes one body; everything else is code.</strong> Which section, what it is for and
/// where it sits come from the approved plan, read through the content module under the workspace filter. What
/// the piece is about comes from the long-form grounding package (LF.1.4) and the voice from the approved brand
/// package. The length a body is aimed at is arithmetic done here. After the answer validates, the body is
/// measured against the section ceiling and read by the claim scanners, and what they find is written beside it
/// by the server — a model cannot remove a finding.
/// </para>
/// <para>
/// <strong>Every refusal happens before any provider is paid</strong>: a request that does not name a draft,
/// a revision and a section; a draft that is not there, is not approved, is accepted, or has moved on from the
/// revision named; a piece of work that can no longer be read; a recipe that has moved to a newer version than
/// the plan was made against; and an item whose recipe could not be shown.
/// </para>
/// <para>
/// <strong>An item is written from its own recipe and no other (LF.4.2).</strong> Whether a source has moved is
/// judged on the whole piece. What an item's writer is then shown is a second package, assembled for that one
/// reference: its recipe, the creator's words and the day — and none of the issue's other recipes, pictures,
/// ideas or saved prompts, nor how any other item opens. That package is the one the proposal records, so its
/// checksum names what the item was written from.
/// </para>
/// <para>
/// <strong>A body over the ceiling is not a failure.</strong> It is stored whole with a finding on it. Nothing
/// here shortens, trims or rewrites what was written.
/// </para>
/// <para>
/// <strong>It stops at the proposal.</strong> The rows say which section the body is for and which revision
/// it was written against; writing it into a new revision of the draft is LF.3.3's. Until then the body is a
/// proposal and the draft is unchanged.
/// </para>
/// </remarks>
internal sealed partial class LongFormSectionAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    ICreativeContextPackageFacade creativeContexts,
    IBrandContextAssembler brandContext,
    ILongFormSectionFacade sections,
    ILongFormPlanFacade plans,
    IClock clock) : IAiTaskHandler
{
    public const string KindInput = "kind";

    public const string RoleInput = "role";

    public const string RoleGuidanceInput = "roleGuidance";

    public const string PositionInput = "position";

    public const string LengthInput = "length";

    public const string CarriedCopyRuleInput = "carriedCopyRule";

    public const string RecipeRuleInput = "recipeRule";

    public const string ChangeRuleInput = "changeRule";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var draftId = LongFormSectionInputs.ReadDraftId(context.Inputs);
        var revisionNumber = LongFormSectionInputs.ReadRevisionNumber(context.Inputs);
        var sectionId = LongFormSectionInputs.ReadSectionId(context.Inputs);
        var instruction = LongFormSectionInputs.ReadInstruction(context.Inputs);

        if (draftId is null || revisionNumber is null || sectionId is null)
        {
            return Failure(
                AiFailureCategory.Validation,
                "This request does not say which draft, which revision of it and which section to write.");
        }

        if (instruction is { Length: > AiPolicy.LongFormSectionInstructionMaxLength })
        {
            return Failure(
                AiFailureCategory.Validation,
                $"What to change is described in at most {Number(AiPolicy.LongFormSectionInstructionMaxLength)} characters.");
        }

        // Through the content module's facade, under the workspace filter: another workspace's draft is not
        // found rather than found and rejected, and the two get one answer.
        var read = await sections.GetWritingContextAsync(
            draftId.Value, revisionNumber.Value, sectionId.Value, cancellationToken);

        if (!read.Succeeded)
        {
            // The content module's own sentence: it names no id and nothing a creator wrote.
            return Failure(AiFailureCategory.DomainInvalid, read.Error!.Message);
        }

        var writing = read.Value!;

        // The read is made under the workspace filter, so this cannot fail today. It is checked rather than
        // assumed: the plan and the body it carries are a creator's prose, and the one answer for a draft that
        // is not this workspace's is that it is not there.
        if (writing.WorkspaceId != context.WorkspaceId)
        {
            return Failure(AiFailureCategory.DomainInvalid, "That long-form draft does not exist.");
        }

        var target = writing.Sections[writing.TargetIndex];

        var assembled = await creativeContexts.AssembleAsync(
            writing.CreativeContextId, AiTaskType.LongFormSection, cancellationToken);

        if (!assembled.Succeeded)
        {
            return Failure(AiFailureCategory.DomainInvalid, "That piece of work is no longer available to write from.");
        }

        var package = assembled.Value!;

        // The same sentence as an unreadable one, and a refusal rather than the envelope's exception: a package
        // from another workspace, or for another piece of work than the draft's, is not this draft's material.
        if (package.WorkspaceId != context.WorkspaceId || package.ContextId != writing.CreativeContextId)
        {
            return Failure(AiFailureCategory.DomainInvalid, "That piece of work is no longer available to write from.");
        }

        if (SourceHasMoved(writing, package))
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "One of this piece's recipes has changed since its plan was made. Review the piece against the "
                    + "current recipe before any more of it is written.");
        }

        CreativeContextRecipeEntry? recipe = null;

        if (target.Role is LongFormSectionRole.Item)
        {
            var sliced = target.SourceReferenceId is { } own
                ? await creativeContexts.AssembleAsync(
                    writing.CreativeContextId,
                    AiTaskType.LongFormSection,
                    new CreativeContextPackageOptions(OnlyRecipeReferenceId: own),
                    cancellationToken)
                : null;

            recipe = sliced is { Succeeded: true }
                && sliced.Value!.WorkspaceId == context.WorkspaceId
                && sliced.Value.ContextId == writing.CreativeContextId
                    ? sliced.Value.Recipes.SingleOrDefault(candidate => candidate.ReferenceId == target.SourceReferenceId)
                    : null;

            // An item is one recipe and nothing else. Written without it, it could only be invented.
            if (recipe is null)
            {
                return Failure(
                    AiFailureCategory.DomainInvalid,
                    "The recipe this section covers could not be read, so the section cannot be written. Check the "
                        + "recipe is still part of this piece.");
            }

            // Two reads, a moment apart. The piece was checked whole a moment ago; this is the same check on the
            // one recipe the item is about to be written from.
            if ((recipe.LatestRecipeVersionId is { } latest && latest != recipe.RecipeVersionId)
                || writing.Pins.All(pin => pin.ReferenceId != recipe.ReferenceId)
                || writing.Pins.Any(pin =>
                    pin.ReferenceId == recipe.ReferenceId && pin.RecipeVersionId != recipe.RecipeVersionId))
            {
                return Failure(
                    AiFailureCategory.DomainInvalid,
                    "One of this piece's recipes has changed since its plan was made. Review the piece against the "
                        + "current recipe before any more of it is written.");
            }

            // From here on the item's own package is the package: what is sent, measured, scanned and recorded.
            package = sliced.Value!;
        }

        var template = templates.Get(AiTaskCatalog.LongFormSection);
        var brand = await AssembleBrandAsync(cancellationToken);
        var carried = CarriedParts(target.Role, recipe, package);

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiLongFormSectionOutputDocument>(
                Envelope(context, template, writing, package, brand, carried, instruction).Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiLongFormSectionOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var measure = plans.Measure(outcome.Document!.Body);
        var (changes, warnings) = Translate(outcome.Document, writing, package, measure, carried);

        var notShown = CreativeContextPackageSelection.NotShown(package.Dropped).Sum(item => item.Count);

        if (notShown > 0)
        {
            warnings.Insert(0, new AiOutputWarning
            {
                Kind = AiWarningKind.Limitation,
                Message = $"[{LongFormFindings.SourcesNotShown}] {Number(notShown)} of this piece's sources could "
                    + "not be shown to the writer, so this section does not draw on them.",
                ChangeIndex = null,
            });
        }

        if (brand is null)
        {
            warnings.Insert(0, new AiOutputWarning
            {
                Kind = AiWarningKind.Limitation,
                Message = $"[{LongFormFindings.BrandGuidanceUnavailable}] This section was written without your "
                    + "brand guidance: none could be read. Check it reads like you.",
                ChangeIndex = null,
            });
        }

        var attempt = outcome.Attempts[^1];
        var pinned = package.Recipes.Count > 0 ? package.Recipes[0].RecipeVersionId : (Guid?)null;

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: pinned,

            // Checked above against the recipes as they stood when the package was read, which is the same read.
            currentVersionId: pinned,
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
            brand,
            package);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, outcome.Attempts)
            : AiTaskHandlerOutcome.ForFailure(
                assembly.Failure!.Category, assembly.Failure.Message, outcome.Attempts);
    }

    /// <summary>
    /// Whether a recipe is no longer the version this piece was planned against.
    /// </summary>
    /// <remarks>
    /// Two ways, and either is enough: the piece of work now points at a different version than the plan
    /// pinned, or the version it points at has itself been superseded. A section written across that gap would
    /// sit beside sections written from another recipe, and the document could never be accepted as current.
    /// A pinned recipe the package could not show is not this: it is reported as a source not shown.
    /// A recipe whose latest version could not be established is not this either — unknown is not moved.
    /// </remarks>
    internal static bool SourceHasMoved(
        LongFormWritingContextServiceModel writing, CreativeContextPackage package)
    {
        if (package.Recipes.Any(recipe =>
                (recipe.LatestRecipeVersionId is { } latest && latest != recipe.RecipeVersionId)
                || writing.Pins.Any(pin =>
                    pin.ReferenceId == recipe.ReferenceId && pin.RecipeVersionId != recipe.RecipeVersionId)))
        {
            return true;
        }

        // The set of recipes itself. A recipe the plan never saw is as much a moved source as a new version of
        // one it did, and so is a pinned recipe that is no longer part of the piece. A pinned recipe the package
        // names as left out — for room, or because it could not be read — is still part of the piece: that is
        // reported as a source not shown.
        return package.Recipes.Any(recipe => writing.Pins.All(pin => pin.ReferenceId != recipe.ReferenceId))
            || writing.Pins.Any(pin =>
                package.Recipes.All(recipe => recipe.ReferenceId != pin.ReferenceId)
                && package.Dropped.All(drop => drop.ReferenceId != pin.ReferenceId));
    }

    private async Task<BrandContextPackage?> AssembleBrandAsync(CancellationToken cancellationToken)
    {
        var request = BrandContextRequestSelection.Default.ToRequest(AiTaskType.LongFormSection);

        if (request is null)
        {
            return null;
        }

        var assembled = await brandContext.AssembleAsync(request, cancellationToken);

        return assembled.Succeeded ? assembled.Value : null;
    }

    /// <summary>The envelope, assembled in one place so every segment's trust level is visible together.</summary>
    /// <remarks>
    /// Every template input is the server's own text: a kind, a role name, a rule and a number. Nothing a
    /// creator typed is rendered into the task. The plan they edited, the openings of what is already written,
    /// the body being replaced and what they asked to change all arrive fenced, as material.
    /// </remarks>
    private static PromptEnvelopeBuilder Envelope(
        AiTaskExecutionContext context,
        PromptTemplate template,
        LongFormWritingContextServiceModel writing,
        CreativeContextPackage package,
        BrandContextPackage? brand,
        IReadOnlyList<string> carried,
        string? instruction)
    {
        var target = writing.Sections[writing.TargetIndex];

        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KindInput] = LongFormOutlineAiTaskHandler.DescribeKind(writing.Kind),
                [RoleInput] = target.Role.ToString(),
                [RoleGuidanceInput] = LongFormSectionGuidance.For(target.Role),
                [PositionInput] = PositionBrief(writing),
                [LengthInput] = LengthBrief(writing),
                [CarriedCopyRuleInput] = CarriedCopyRule(carried),
                [RecipeRuleInput] = RecipeRule(
                    target, package, writing.Sections.Count(section => section.Role is LongFormSectionRole.Item)),
                [ChangeRuleInput] = ChangeRule(writing.CurrentBody is not null, instruction is not null),
            }))
            .WithOutputSchema(AiLongFormSectionOutputSchema.Json)
            .WithSource(writing.WorkspaceId, RenderPlan(writing, package));

        if (brand is not null)
        {
            if (BrandContextPromptRenderer.Guidance(brand) is { } guidance)
            {
                builder = builder.WithPreferences(context.WorkspaceId, guidance);
            }

            if (BrandContextPromptRenderer.Excerpts(brand) is { } excerpts)
            {
                builder = builder.AddReference(context.WorkspaceId, excerpts);
            }
        }

        if (RenderRewrite(writing.CurrentBody, instruction) is { } rewrite)
        {
            builder = builder.WithUntrustedText(writing.WorkspaceId, rewrite);
        }

        // Stamped with the workspace the context was read in, which the builder refuses if it is not its own.
        return CreativeContextPromptRenderer.AddTo(builder, package);
    }

    /// <summary>
    /// The approved plan as the writer sees it: every section's role, heading and intent in order, which one is
    /// being written, and how each one already written begins.
    /// </summary>
    /// <remarks>
    /// An item names its recipe by position in the package's own list, never by an identifier: no id reaches a
    /// model.
    /// </remarks>
    internal static string RenderPlan(LongFormWritingContextServiceModel writing, CreativeContextPackage package) =>
        JsonSerializer.Serialize(
            new
            {
                plan = writing.Sections.Select((section, index) => new
                {
                    position = index + 1,
                    role = section.Role.ToString(),
                    heading = section.Heading,
                    intent = section.Intent,
                    recipe = section.SourceReferenceId is { } referenceId
                        && package.Recipes.ToList().FindIndex(recipe => recipe.ReferenceId == referenceId) is >= 0 and var found
                            ? found + 1
                            : (int?)null,
                    writeThisOne = index == writing.TargetIndex ? true : (bool?)null,

                    alreadyWrittenBeginning = ShowsOpening(writing, index) ? section.FirstLine : null,
                }),
            },
            Json);

    /// <summary>
    /// Whether the writer of the target section is shown how another section opens.
    /// </summary>
    /// <remarks>
    /// An item is shown one recipe, and an opening is prose that may state any recipe's facts: another item's
    /// states its own recipe's, and a lead or an aside may state every recipe's. So an item is shown only the
    /// openings of the sections that carry no recipe facts at all — the greeting, the closing and the sign-off.
    /// Every other section is shown every other opening, as before.
    /// </remarks>
    internal static bool ShowsOpening(LongFormWritingContextServiceModel writing, int index) =>
        index != writing.TargetIndex
        && (writing.Sections[writing.TargetIndex].Role is not LongFormSectionRole.Item
            || writing.Sections[index].Role
                is LongFormSectionRole.Greeting or LongFormSectionRole.Closing or LongFormSectionRole.Signoff);

    /// <summary>The body being replaced and what the creator asked to change, or null on a first write.</summary>
    internal static string? RenderRewrite(string? currentBody, string? instruction) =>
        currentBody is null && instruction is null
            ? null
            : JsonSerializer.Serialize(new { currentBody, whatTheCreatorAskedToChange = instruction }, Json);

    internal static string PositionBrief(LongFormWritingContextServiceModel writing) =>
        $"section {Number(writing.TargetIndex + 1)} of {Number(writing.Sections.Count)}";

    /// <summary>
    /// The length to aim for: the kind's target range shared evenly across the plan's sections, and the ceiling.
    /// </summary>
    /// <remarks>
    /// Arithmetic done here and told to the model as guidance. It is not relied on: a body is measured after it
    /// is written. An even share is a starting point a creator's plan can make wrong — a greeting is not as long
    /// as a method — which is why it is a range and the prompt says the section's purpose outranks it.
    /// </remarks>
    internal static string LengthBrief(LongFormWritingContextServiceModel writing)
    {
        var count = Math.Max(1, writing.Sections.Count);

        // Never more words than the ceiling has room for: a guide that asked for more would be asking for a
        // body the server then flags as too long.
        var room = Math.Max(
            AiPolicy.LongFormSectionWordsFloor, writing.SectionMaxCharacters / AiPolicy.LongFormCharactersPerWord);
        var high = Math.Min(room, Math.Max(AiPolicy.LongFormSectionWordsFloor, writing.TargetWords.Max / count));
        var low = Math.Min(high, Math.Max(AiPolicy.LongFormSectionWordsFloor, writing.TargetWords.Min / count));

        // The target is the issue's (LF.4.2): an even share is where a section starts, never a length an item
        // is held to.
        return $"between {Number(low)} and {Number(high)} words, and never more than "
            + $"{Number(writing.SectionMaxCharacters)} characters"
            + (writing.Kind is LongFormKind.NewsletterIssue
                ? ". That is an even share of the issue's length, which is the one that matters: no item has a "
                    + "target of its own"
                : string.Empty);
    }

    /// <summary>
    /// The parts of accepted copy that already cover this section's material (baseline B-36), by name.
    /// </summary>
    /// <remarks>
    /// For an item, the copy accepted for its own recipe. For any other section, the copy accepted for the
    /// piece's only recipe — with several recipes there is no saying whose tips a Tips section means, so nothing
    /// is carried and the section is written from the recipes' own lines.
    /// </remarks>
    internal static IReadOnlyList<string> CarriedParts(
        LongFormSectionRole role, CreativeContextRecipeEntry? itemRecipe, CreativeContextPackage package)
    {
        var editorial = itemRecipe?.Editorial
            ?? (package.Recipes.Count == 1 ? package.Recipes[0].Editorial : null);

        return editorial is null
            ? []
            : [.. LongFormSectionGuidance.PartsFor(role).Where(part => editorial.SectionsCarried.Contains(part))];
    }

    internal static string CarriedCopyRule(IReadOnlyList<string> carried) =>
        carried.Count == 0
            ? "No accepted copy covers this section. Write it from the creator's material."
            : "The creator has already accepted copy that covers this section: the "
                + string.Join(" and the ", carried.Select(part => $"`{part}`"))
                + " under `acceptedEditorialCopy`. Work from that text. Keep what it says and how it says it, "
                + "fit it to this section and this piece, and do not write the same material again in other "
                + "words. Add only what the section's intent asks for and that text does not already say.";

    /// <param name="items">How many items the plan has: whether an item is told there are others.</param>
    internal static string RecipeRule(
        LongFormWritingSectionServiceModel target, CreativeContextPackage package, int items = 1) =>
        (target.Role, package.Recipes.Count) switch
        {
            (LongFormSectionRole.Item, _) when items > 1 =>
                "This section is about one recipe, and it is the only recipe you are shown. The issue covers "
                    + "others, each in an item of its own that somebody else writes. Take every recipe fact from "
                    + "this recipe's own lines. State no ingredient, quantity, time, temperature, yield or step of "
                    + "any other dish, and do not compare this recipe to another item's: you have not been shown them.",
            (LongFormSectionRole.Item, _) =>
                "This section is about one recipe, and it is the only recipe you are shown. Take every recipe "
                    + "fact from its own lines, and state no ingredient, quantity, time, temperature, yield or step "
                    + "of any other dish.",
            (_, 0) =>
                "No recipe is shown. You have no recipe facts at all: write no quantity, time, temperature, "
                    + "yield, ingredient list or step.",
            (_, 1) => "Take every recipe fact from the recipes' own lines, exactly as they state it.",
            _ => "Take every recipe fact from the recipes' own lines, exactly as they state it. A fact belongs to "
                + "the recipe whose lines state it: name the dish it comes from, and never give one recipe's "
                + "figure, ingredient or step as another's.",
        };

    internal static string ChangeRule(bool hasBody, bool hasInstruction) => (hasBody, hasInstruction) switch
    {
        (true, true) =>
            "This section has been written before, and the creator has asked for it to be changed. The "
                + "UNTRUSTED_TEXT segment holds the body as it stands and what they asked to change. Write the "
                + "section again with that change made, keeping what they did not ask to change.",
        (true, false) =>
            "This section has been written before, and the creator has asked for another version. The "
                + "UNTRUSTED_TEXT segment holds the body as it stands. Write the section again, differently.",
        (false, true) =>
            "The creator has said what they want from this section; it is in the UNTRUSTED_TEXT segment. Write "
                + "the section with that in mind.",
        _ => "This section has not been written before.",
    };

    /// <summary>
    /// The validated body as the rows a proposal is stored as, with what the server found about it.
    /// </summary>
    /// <remarks>
    /// One target — the section's own id — so whatever lands this proposal needs no lookup to know where the
    /// body goes. The body is stored exactly as written, in as many parts as it takes; the first part is the
    /// row every warning points at.
    /// </remarks>
    internal static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiLongFormSectionOutputDocument document,
        LongFormWritingContextServiceModel writing,
        CreativeContextPackage package,
        LongFormBodyMeasureServiceModel measure,
        IReadOnlyList<string> carried)
    {
        var target = writing.Sections[writing.TargetIndex];
        var changes = new List<AiResolvedChange>();
        var warnings = new List<AiOutputWarning>();

        void Set(string field, string value) => changes.Add(new AiResolvedChange(
            AiChangeKind.Set, AiChangeTargetKind.LongFormSectionBody, target.Id, field,
            BeforeValue: null, value, ProposedPosition: null, changes.Count));

        var parts = LongFormSectionFields.Split(document.Body);

        for (var index = 0; index < parts.Count; index++)
        {
            Set(LongFormSectionFields.BodyPart(index), parts[index]);
        }

        Set(LongFormSectionFields.BasedOnRevision, Number(writing.RevisionNumber));

        // The server's own record of what the writer was pointed at, from a closed list of part names. It is
        // what lets the piece later say whose words a section began as (B-36).
        if (carried.Count > 0)
        {
            Set(LongFormSectionFields.ReusedEditorialParts, string.Join(LongFormSectionFields.ReusedPartsSeparator, carried));
        }

        void Found(AiWarningKind kind, string code, string message) => warnings.Add(new AiOutputWarning
        {
            Kind = kind,
            Message = $"[{code}] {message}",
            ChangeIndex = 0,
        });

        // The ceiling first: it is the one finding that is about the whole body rather than a phrase in it.
        if (measure.OverCeiling)
        {
            Found(
                AiWarningKind.Limitation,
                LongFormFindingCodes.SectionOverCeiling,
                $"This section is {Number(measure.CharacterCount)} characters, over the {Number(measure.MaxCharacters)} "
                    + "a section may be. Nothing was cut. Shorten it, or ask for it again.");
        }

        // No claim is looked for here. Figures, safety and dietary claims, storage advice, search metrics and
        // promises are found by the content module's scanner when this body is written into the draft
        // (LF.3.2) — the same scanner a creator's own edit goes through, so a model's text and theirs are
        // held to one set of rules, and what it finds is stored with the section rather than on this proposal.
        if (RepeatsEarlierSection(document.Body, writing) is { } repeated)
        {
            Found(
                AiWarningKind.Limitation,
                LongFormFindings.RepeatsEarlierSection,
                $"This section opens the way section {Number(repeated + 1)} does. Check the two do not say the "
                    + "same thing twice.");
        }

        // Told to the creator so the assembled document can say whose words these were (B-36), and so a
        // section that reads like copy they have seen before is not a surprise.
        if (carried.Count > 0)
        {
            Found(
                AiWarningKind.Rationale,
                LongFormFindings.DrawsOnAcceptedCopy,
                "This section was written from copy you already accepted for the recipe: "
                    + string.Join(", ", carried) + ".");
        }

        // The model's own warnings last, so a server finding is never pushed out of view by one, and labelled
        // as the model's. The validator has already refused one that imitates a server finding.
        foreach (var warning in document.Warnings)
        {
            warnings.Add(new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = AiPolicy.ModelWarningLabel + warning.Message,
                ChangeIndex = 0,
            });
        }

        return (changes, warnings);
    }

    /// <summary>
    /// The position of an earlier-written section this body repeats, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrow: a body repeats another when a line of it is, letter for letter once case, spacing
    /// and punctuation are set aside, the line the other section opens with — and that line is long enough to
    /// be a sentence and not a word. It is the one repetition code can name without judging prose; whether two
    /// sections cover the same ground in different words is a question for the creator.
    /// </para>
    /// <para>
    /// Only openings are compared because openings are all this task is shown of the other sections.
    /// </para>
    /// </remarks>
    internal static int? RepeatsEarlierSection(string body, LongFormWritingContextServiceModel writing)
    {
        var lines = body.Split('\n').Select(Normalise).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);

        for (var index = 0; index < writing.Sections.Count; index++)
        {
            // Only against what the writer was shown: an opening it never saw is not one it repeated.
            if (!ShowsOpening(writing, index) || writing.Sections[index].FirstLine is not { } firstLine)
            {
                continue;
            }

            var opening = Normalise(firstLine);

            if (opening.Split(' ').Length >= AiPolicy.LongFormRepeatedOpeningMinWords
                && lines.Any(line => line.StartsWith(opening, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return null;
    }

    private static string Normalise(string line) =>
        NotLetterOrDigit().Replace(line.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotLetterOrDigit();

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string message) =>
        AiTaskHandlerOutcome.ForFailure(category, message, []);
}

/// <summary>
/// What each role's section is told to do, and which parts of accepted copy already cover it (LF.3.1, B-36).
/// </summary>
/// <remarks>
/// The server's own text, from a closed vocabulary: a role decides which of these lines the task carries, and
/// nothing a creator typed is in any of them. <see cref="LongFormRoleGuidance"/> says what a role is for when a
/// plan is being made; this says how to write it.
/// </remarks>
internal static class LongFormSectionGuidance
{
    public static string For(LongFormSectionRole role) => role switch
    {
        LongFormSectionRole.Hook =>
            "Open on one concrete reason to read on, drawn from the creator's material. No list of what is to "
                + "come and no recipe steps.",
        LongFormSectionRole.Context =>
            "Say where the dish or the idea comes from and why now, only as far as the creator's material tells "
                + "it. Invent no history, no memory and no testing.",
        LongFormSectionRole.WhyItWorks =>
            "Explain the choices the method makes, pointing at the recipe's own steps and ingredients. Explain "
                + "in a cook's terms; claim no science the material does not give.",
        LongFormSectionRole.IngredientNotes =>
            "Notes on the ingredients the recipe lists, each in the quantity and the words the recipe uses. "
                + "Suggest no substitute the material does not give.",
        LongFormSectionRole.MethodNarrative =>
            "Walk through the recipe's steps in prose, in the recipe's order, leaving none out and adding none. "
                + "Every quantity, time and temperature is written as the recipe states it.",
        LongFormSectionRole.Tips =>
            "Practical tips the creator's material supports, as a short list or a few short paragraphs.",
        LongFormSectionRole.Variations =>
            "Only the variations and substitutions the creator's material gives. If it gives none, say briefly "
                + "that the recipe is written one way and raise a warning; invent none.",
        LongFormSectionRole.Storage =>
            "Only what the creator's material says about keeping, freezing or reheating. Give no keeping time, "
                + "temperature or method it does not state. If it says nothing, write one or two sentences "
                + "saying the creator has not given storage notes, and raise a warning.",
        LongFormSectionRole.Faq =>
            "Questions a reader is likely to ask, each answered in a sentence or two from the creator's "
                + "material. Write each question in bold on its own line. Ask no question the material cannot answer.",
        LongFormSectionRole.Cta =>
            "What the creator asks the reader to do next, as their material or their guidance puts it. One "
                + "short paragraph. Give no address and promise nothing.",
        LongFormSectionRole.Closing =>
            "A short send-off in the creator's voice. No new facts and no summary of the whole piece.",
        LongFormSectionRole.Body =>
            "Cover what this section's heading and intent say, and nothing they do not.",
        LongFormSectionRole.Greeting =>
            "A short hello to the subscriber in the creator's voice: two or three sentences. No recipe facts.",
        LongFormSectionRole.Lead =>
            "Say what this issue is about and why it is worth reading, in a short paragraph or two.",
        LongFormSectionRole.Item =>
            "Present the one recipe this section covers: what it is and why it is in this issue. Mention a "
                + "quantity, a time or a step only as that recipe states it; this is not the recipe card.",
        LongFormSectionRole.Aside =>
            "A short note beside the main items, on what its heading and intent say.",
        LongFormSectionRole.Signoff =>
            "The creator signing off in their own way: a line or two. No new facts.",
        _ => "Cover what this section's heading and intent say.",
    };

    /// <summary>The parts of an accepted editorial package whose material a role's section covers.</summary>
    public static IReadOnlyList<string> PartsFor(LongFormSectionRole role) => role switch
    {
        LongFormSectionRole.Hook => [CreativeContextPackageSelection.EditorialSections.Headnote],
        LongFormSectionRole.Context => [CreativeContextPackageSelection.EditorialSections.Introduction],
        LongFormSectionRole.Tips => [CreativeContextPackageSelection.EditorialSections.Tips],
        LongFormSectionRole.Variations => [CreativeContextPackageSelection.EditorialSections.Substitutions],
        LongFormSectionRole.Storage => [CreativeContextPackageSelection.EditorialSections.StorageReheating],
        LongFormSectionRole.Faq => [CreativeContextPackageSelection.EditorialSections.Faq],
        LongFormSectionRole.Cta => [CreativeContextPackageSelection.EditorialSections.Cta],
        LongFormSectionRole.Item => [CreativeContextPackageSelection.EditorialSections.Headnote],
        _ => [],
    };
}
