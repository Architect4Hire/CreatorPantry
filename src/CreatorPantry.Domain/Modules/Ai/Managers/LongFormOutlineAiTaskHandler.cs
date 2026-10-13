using System.Globalization;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.LongFormOutline"/> (LF.2.1): proposes a section plan for one blog article or
/// newsletter issue, for the creator to edit and approve before any prose is written (baseline B-33).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The model proposes a plan; everything else is code.</strong> Which roles a piece may use and how
/// often come from the content module's shape rules. What the piece is about comes from the long-form
/// grounding package (LF.1.4), assembled under the workspace filter, and the voice from the approved brand
/// package beside it. After the answer validates, each heading and intent is read by the claim scanners and
/// what they find is written beside the section by the server — a model cannot remove a finding.
/// </para>
/// <para>
/// <strong>Three refusals happen before any provider is paid.</strong> A request that does not say what to
/// plan; a piece of work with nothing to plan from; and one that pins a recipe version that is no longer the
/// latest, because an article planned against a superseded recipe could never be accepted as current.
/// </para>
/// <para>
/// <strong>A recipe that could not be shown is not a refusal.</strong> The plan goes ahead on what is there,
/// the prompt says how many sources are not shown, and the creator is told in a warning.
/// </para>
/// <para>
/// <strong>It stops at the proposal.</strong> Turning the stored rows into a draft revision needs the
/// proposal's id, which exists only once the worker has stored it, so that step is
/// <see cref="LongFormOutlineProposalLanding"/>'s (LF.2.2). What it needs is all on the proposal: the plan rows,
/// one pin row for every recipe the plan was written against, and the brand and creative-context provenance.
/// </para>
/// </remarks>
internal sealed partial class LongFormOutlineAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    ICreativeContextPackageFacade creativeContexts,
    IBrandContextAssembler brandContext,
    ILongFormPlanFacade plans,
    IClock clock) : IAiTaskHandler
{
    public const string KindInput = "kind";

    public const string RolesInput = "roles";

    public const string TargetLengthInput = "targetLength";

    public const string WantedSectionsInput = "wantedSections";

    public const string ItemRuleInput = "itemRule";

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var contextId = LongFormOutlineInputs.ReadContextId(context.Inputs);
        var kind = LongFormOutlineInputs.ReadKind(context.Inputs);
        var targetWords = LongFormOutlineInputs.ReadTargetWords(context.Inputs);
        var wanted = LongFormOutlineInputs.ReadWantedRoles(context.Inputs);

        if (contextId is null || kind is null || wanted is null)
        {
            return Failure(
                AiFailureCategory.Validation,
                "This request does not say which piece of work to plan, what kind of piece it is, or names a "
                    + "section that is not one a plan can have.");
        }

        if (targetWords is < AiPolicy.LongFormTargetWordsMin or > AiPolicy.LongFormTargetWordsMax)
        {
            return Failure(
                AiFailureCategory.Validation,
                $"A piece is planned at between {Number(AiPolicy.LongFormTargetWordsMin)} and "
                    + $"{Number(AiPolicy.LongFormTargetWordsMax)} words.");
        }

        var rules = plans.Describe(kind.Value);

        // Asked of the same rules the answer is held to, so a request for a section this kind cannot have, or
        // for one twice, is refused here rather than paid for and refused afterwards.
        if (wanted.Any(role => rules.Roles.All(rule => rule.Role != role))
            || wanted.GroupBy(role => role).Any(group =>
                rules.Roles.Single(rule => rule.Role == group.Key).MaxOccurrences is { } most && group.Count() > most))
        {
            return Failure(
                AiFailureCategory.Validation,
                "One of the sections asked for is not one this kind of piece has, or is asked for more often "
                    + "than it may appear.");
        }

        // Through the content module's facade, under the workspace filter: another workspace's context is not
        // found rather than found and rejected, and the two get one answer.
        var assembled = await creativeContexts.AssembleAsync(
            contextId.Value, AiTaskType.LongFormOutline, cancellationToken);

        if (!assembled.Succeeded)
        {
            return Failure(AiFailureCategory.DomainInvalid, "That piece of work is no longer available to plan.");
        }

        var package = assembled.Value!;

        if (!HasSomethingToPlanFrom(package))
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "There is nothing to plan a piece from yet. Give this piece of work a title, a brief, a recipe "
                    + "or a concept first.");
        }

        if (package.Recipes.Any(recipe =>
                recipe.LatestRecipeVersionId is { } latest && latest != recipe.RecipeVersionId))
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "This piece of work is pinned to an older version of one of its recipes. Point it at the "
                    + "current version before planning, so the piece is not planned around a recipe that has moved on.");
        }

        // An item covers a recipe the task is shown. Asking for more items than there are recipes asks for a
        // plan no answer could be: refused here rather than paid for and then refused, every time.
        if (wanted.Count(role => role is LongFormSectionRole.Item) > package.Recipes.Count)
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "An item section covers one of this piece's recipes, and it does not name enough recipes for the "
                    + "item sections asked for. Add the recipe to the piece first, or leave the item out.");
        }

        var template = templates.Get(AiTaskCatalog.LongFormOutline);
        var brand = await AssembleBrandAsync(cancellationToken);
        var expectation = new AiLongFormOutlineExpectation(kind.Value, package.Recipes.Count, wanted, plans);

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiLongFormOutlineOutputDocument>(
                Envelope(context, template, rules, targetWords, wanted, package, brand).Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiLongFormOutlineOutputValidator.For(expectation)),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!, package);

        // Server-written, because only the server knows what it could not show. A plan made around three
        // recipes when the piece names five would otherwise read as a plan for the whole piece.
        var notShown = CreativeContextPackageSelection.NotShown(package.Dropped).Sum(item => item.Count);

        if (notShown > 0)
        {
            warnings.Insert(0, new AiOutputWarning
            {
                Kind = AiWarningKind.Limitation,
                Message = $"[{LongFormFindings.SourcesNotShown}] {Number(notShown)} of this piece's sources could "
                    + "not be shown to the writer, so this plan does not cover them. Check each is still "
                    + "available, or that the piece does not name more than can be read at once.",
                ChangeIndex = null,
            });
        }

        // Server-written for the same reason: a plan is the brand deciding how a piece is built, and a creator
        // who silently got none of their guidance would be approving a structure they believe is theirs.
        if (brand is null)
        {
            warnings.Insert(0, new AiOutputWarning
            {
                Kind = AiWarningKind.Limitation,
                Message = $"[{LongFormFindings.BrandGuidanceUnavailable}] This plan was made without your brand "
                    + "guidance: none could be read. Check it reads like you before approving it.",
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
    /// Whether the package says anything a piece could be about.
    /// </summary>
    /// <remarks>
    /// A day, a theme or an undescribed picture alone is not a subject. The working brief is one, and so is a
    /// saved prompt: "from the creator's own brief" and "from a saved prompt" are two of the three ways a
    /// long-form piece starts (baseline B-32).
    /// </remarks>
    private static bool HasSomethingToPlanFrom(CreativeContextPackage package) =>
        package.Words?.WorkingTitle is not null
        || package.Words?.WorkingBrief is not null
        || package.Words?.PictureBrief is not null
        || package.Recipes.Count > 0
        || package.Concepts.Count > 0
        || package.Prompts.Count > 0
        || package.Pictures.Any(picture => picture.Description is not null || picture.Reading is not null);

    private async Task<BrandContextPackage?> AssembleBrandAsync(CancellationToken cancellationToken)
    {
        var request = BrandContextRequestSelection.Default.ToRequest(AiTaskType.LongFormOutline);

        if (request is null)
        {
            return null;
        }

        var assembled = await brandContext.AssembleAsync(request, cancellationToken);

        return assembled.Succeeded ? assembled.Value : null;
    }

    /// <summary>The envelope, assembled in one place so every segment's trust level is visible together.</summary>
    /// <remarks>
    /// Every template input is the server's own text: a kind, a role list, a number and a rule. Nothing a
    /// creator typed is rendered into the task. Their material arrives in the package, fenced as untrusted.
    /// </remarks>
    private static PromptEnvelopeBuilder Envelope(
        AiTaskExecutionContext context,
        PromptTemplate template,
        LongFormPlanRulesServiceModel rules,
        int? targetWords,
        IReadOnlyList<LongFormSectionRole> wanted,
        CreativeContextPackage package,
        BrandContextPackage? brand)
    {
        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KindInput] = DescribeKind(rules.Kind),
                [RolesInput] = RoleBrief(rules),
                [TargetLengthInput] = TargetBrief(rules, targetWords),
                [WantedSectionsInput] = WantedBrief(wanted),
                [ItemRuleInput] = ItemRule(rules.Kind, package.Recipes.Count),
            }))
            .WithOutputSchema(AiLongFormOutlineOutputSchema.Json, template.OutputSchemaVersion);

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

        // Stamped with the workspace the context was read in, which the builder refuses if it is not its own.
        return CreativeContextPromptRenderer.AddTo(builder, package);
    }

    internal static string DescribeKind(LongFormKind kind) => kind switch
    {
        LongFormKind.NewsletterIssue => "a newsletter issue",
        _ => "a blog article",
    };

    /// <summary>One line per role this kind allows: its name, how often it may appear, and what it is for.</summary>
    internal static string RoleBrief(LongFormPlanRulesServiceModel rules) =>
        string.Join('\n', rules.Roles.Select(rule =>
            $"- {rule.Role} ({Often(rule.MaxOccurrences)}): {LongFormRoleGuidance.Purpose(rule.Role)}"));

    private static string Often(int? most) => most switch
    {
        null => "as many as the rule below says",
        1 => "at most once",
        var count => $"at most {Number(count.Value)} times",
    };

    /// <summary>
    /// The length to plan for. Told to the model so it plans a sensible number of sections, and not relied on:
    /// a document is measured after it is written.
    /// </summary>
    internal static string TargetBrief(LongFormPlanRulesServiceModel rules, int? targetWords) =>
        targetWords is { } words
            ? $"about {Number(words)} words"
            : $"between {Number(rules.TargetWords.Min)} and {Number(rules.TargetWords.Max)} words";

    internal static string WantedBrief(IReadOnlyList<LongFormSectionRole> wanted) =>
        wanted.Count == 0
            ? "The creator asked for no particular section."
            : "The creator asked for these sections, and each must be in the plan: "
                + string.Join(", ", wanted.Select(role => role.ToString())) + ".";

    /// <summary>What a plan must do about items, stated from the number of recipes the task is shown.</summary>
    internal static string ItemRule(LongFormKind kind, int recipeCount) => (kind, recipeCount) switch
    {
        (LongFormKind.NewsletterIssue, 0) =>
            "No recipe is listed, so the plan has no Item section. Leave `source` null on every section.",
        (LongFormKind.NewsletterIssue, 1) =>
            "One recipe is listed, so the plan has exactly one Item section, with `source` set to 1. Leave "
                + "`source` null on every other section.",
        (LongFormKind.NewsletterIssue, var count) =>
            $"{Number(count)} recipes are listed, so the plan has exactly {Number(count)} Item sections: one for "
                + "each recipe and no more. Set each item's `source` to the position of its recipe in the "
                + $"`recipes` list, counting from 1 to {Number(count)}, and use each position once. Leave "
                + "`source` null on every other section. An item's heading and intent are about its own recipe "
                + "and no other: name no ingredient, figure or step of a different recipe in them.",
        _ => "This piece has no Item sections. Leave `source` null on every section.",
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The validated plan as the rows a proposal is stored as, with what the server found about each section.
    /// </summary>
    /// <remarks>
    /// One target per section, in plan order. The heading is the Add row; the role, the intent and — for an
    /// item — the reference it covers are Set rows on the same target. Every warning about a section points at
    /// that section's heading row.
    /// </remarks>
    internal static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiLongFormOutlineOutputDocument document, CreativeContextPackage package)
    {
        var changes = new List<AiResolvedChange>();
        var warnings = new List<AiOutputWarning>();
        var headingRows = new List<int>(document.Sections.Count);
        var source = CreativeContextClaimSource.Facts(package);

        for (var index = 0; index < document.Sections.Count; index++)
        {
            var section = document.Sections[index];
            var role = Enum.Parse<LongFormSectionRole>(section.Role);
            var targetId = Guid.NewGuid();
            var headingRow = changes.Count;

            headingRows.Add(headingRow);

            // Stored exactly as written. Not trimmed, not re-cased, not normalised.
            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.LongFormSectionPlan, targetId, FieldName: null,
                BeforeValue: null, section.Heading, ProposedPosition: index, headingRow));

            void Set(string field, string value) => changes.Add(new AiResolvedChange(
                AiChangeKind.Set, AiChangeTargetKind.LongFormSectionPlan, targetId, field,
                BeforeValue: null, value, ProposedPosition: null, changes.Count));

            Set(LongFormPlanFields.Role, Number((int)role));
            Set(LongFormPlanFields.Intent, section.Intent);

            // The position the model gave, turned into the reference it stands for. The validator has already
            // held it inside the list, so this cannot name a source the task was not shown.
            if (section.Source is { } position)
            {
                Set(LongFormPlanFields.SourceReferenceId, package.Recipes[position - 1].ReferenceId.ToString("D"));
            }

            // An item answers to its own recipe alone (LF.4.2): another recipe's baking time in its heading or
            // intent is a figure this recipe does not state, however true it is of the other.
            var answersTo = section.Source is { } own
                ? CreativeContextClaimSource.Facts(package with { Recipes = [package.Recipes[own - 1]] })
                : source;

            // Every server finding carries its code in brackets, as the other writing capabilities' do, so a
            // reader can tell what the server found from what the model said about its own work.
            foreach (var text in new[] { section.Heading, section.Intent })
            {
                foreach (var (kind, code, message) in CreativeContextClaimSource.Claims(text, answersTo))
                {
                    if (role is LongFormSectionRole.Storage
                        && code == AiEditorialClaimScanner.UnsupportedStorageGuidance
                        && OnlyNamesStorage(text))
                    {
                        continue;
                    }

                    warnings.Add(new AiOutputWarning { Kind = kind, Message = $"[{code}] {message}", ChangeIndex = headingRow });
                }
            }
        }

        // The model's own warnings last, so a server finding is never pushed out of view by one, and labelled
        // as the model's. The validator has already refused one that imitates a server finding.
        foreach (var warning in document.Warnings)
        {
            warnings.Add(new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = AiPolicy.ModelWarningLabel + warning.Message,
                ChangeIndex = warning.SectionIndex is { } sectionIndex ? headingRows[sectionIndex] : null,
            });
        }

        // After every section, so a section's position in the rows is never shifted by them: which version of
        // each recipe this plan was written against, in the order the piece names them (LF.2.2). The server's
        // own record of what it read, with nothing in it the model said.
        foreach (var recipe in package.Recipes)
        {
            void Pin(string field, Guid value) => changes.Add(new AiResolvedChange(
                AiChangeKind.Set, AiChangeTargetKind.LongFormRecipePin, recipe.ReferenceId, field,
                BeforeValue: null, value.ToString("D"), ProposedPosition: null, changes.Count));

            Pin(LongFormPinFields.RecipeId, recipe.RecipeId);
            Pin(LongFormPinFields.RecipeVersionId, recipe.RecipeVersionId);
        }

        return (changes, warnings);
    }

    /// <summary>
    /// Whether a storage section's line merely says what the section is, rather than giving storage advice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The storage check answers to a word, not a claim, and "storage" and "reheating" are among its words. In
    /// the plan line of the section whose whole job is storage those are the section's name, and flagging them
    /// would mark every such section a creator is offered.
    /// </para>
    /// <para>
    /// <strong>Only those words are forgiven, and only by asking the scanner again.</strong> The naming words
    /// are taken out and the same check is run on what is left, so "keeps well for a week", "freezes for
    /// months" and "fine left out overnight" are still found — by the scanner's own list, not a second copy
    /// of it that could fall behind.
    /// </para>
    /// </remarks>
    internal static bool OnlyNamesStorage(string text) =>
        AiEditorialClaimScanner.ScanTextWithoutSource(StorageName().Replace(text, " "))
            .All(finding => finding.Code != AiEditorialClaimScanner.UnsupportedStorageGuidance);

    [GeneratedRegex(@"\b(?:stor(?:e|es|ed|ing|age)|reheat\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StorageName();

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string message) =>
        AiTaskHandlerOutcome.ForFailure(category, message, []);
}

/// <summary>The codes the long-form capabilities' own server findings are stored under, in brackets.</summary>
/// <remarks>
/// Claim findings keep the code of the scanner that raised them (<c>editorial.*</c>, <c>seo.*</c>). These are
/// the ones only long-form raises.
/// </remarks>
public static class LongFormFindings
{
    public const string BrandGuidanceUnavailable = "long_form.brand_guidance_unavailable";

    /// <summary>The piece names sources the writer was not shown: gone, over the cap, or over the budget.</summary>
    public const string SourcesNotShown = "long_form.sources_not_shown";

    /// <summary>A section's body opens the way a section already written does (LF.3.1).</summary>
    public const string RepeatsEarlierSection = "long_form.repeats_earlier_section";

    /// <summary>A section was written from copy the creator had already accepted for the recipe (B-36).</summary>
    public const string DrawsOnAcceptedCopy = "long_form.draws_on_accepted_copy";

    /// <summary>A section promises the recipe will work: foolproof, never fails, works every time (LF.3.1).</summary>
    public const string UnsupportedGuarantee = "long_form.unsupported_guarantee";
}

/// <summary>
/// What each long-form role is for, as a model is told it. Prompt guidance, not a rule: which roles a piece may
/// use and how often is the content module's to say.
/// </summary>
internal static class LongFormRoleGuidance
{
    public static string Purpose(LongFormSectionRole role) => role switch
    {
        LongFormSectionRole.Hook => "the opening that gives a reader a reason to keep reading",
        LongFormSectionRole.Context => "where the dish or the idea comes from, and why now",
        LongFormSectionRole.WhyItWorks => "what makes this version work, in terms a cook can use",
        LongFormSectionRole.IngredientNotes => "what to know about the ingredients the recipe lists",
        LongFormSectionRole.MethodNarrative => "a walk through the method in prose, following the recipe's own steps",
        LongFormSectionRole.Tips => "practical tips for making it go well",
        LongFormSectionRole.Variations => "ways to vary it that the creator's material supports",
        LongFormSectionRole.Storage => "keeping and reheating, only as far as the creator's material says",
        LongFormSectionRole.Faq => "questions a reader is likely to ask, with short answers",
        LongFormSectionRole.Cta => "what the creator asks the reader to do next",
        LongFormSectionRole.Closing => "the last word, which sends the reader off",
        LongFormSectionRole.Body => "a section of the creator's own, under a heading that says what it covers",
        LongFormSectionRole.Greeting => "a short hello to the subscriber",
        LongFormSectionRole.Lead => "what this issue is about, and why it is worth opening",
        LongFormSectionRole.Item => "one recipe the issue covers",
        LongFormSectionRole.Aside => "a short note beside the main items",
        LongFormSectionRole.Signoff => "the creator signing off in their own way",
        _ => "a section of the piece",
    };
}
