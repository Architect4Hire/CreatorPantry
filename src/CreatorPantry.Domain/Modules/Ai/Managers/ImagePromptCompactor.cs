using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs the shortening half of <see cref="AiTaskType.ImagePrompt"/> (AP.3.2): the creator's prompt, said in
/// fewer characters so that it and their avoid list fit the model of the picture quality they chose.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same task as composing a prompt, and the same proposal.</strong> What comes back is an image
/// prompt — one <see cref="AiChangeTargetKind.ImagePrompt"/> row for the text and one for the avoid list — so it is
/// read, polled, edited, saved and sent for pictures exactly as a composed one is, and the picture request names it
/// as its proposal. Only the template differs: <c>image.prompt.compact</c>, beside <c>image.prompt</c> rather than
/// a version of it, so composition keeps its own.
/// </para>
/// <para>
/// <strong>The avoid list never reaches the model.</strong> It is the creator's negative guidance, and the one part
/// of what is sent that a shorter version must not lose, so it is carried from the request into the proposal word
/// for word and the model is never asked to keep it. The budget is what is left of the limit once it is set aside,
/// worked out by Media from the class's own model when the request was made.
/// </para>
/// <para>
/// <strong>Nothing is cut, and nothing is shortened silently.</strong> An answer over the budget is refused by the
/// validator and gets the gateway's one corrective re-ask; a second miss fails the operation. Every proposal says
/// that it was shortened, that the creator's own prompt is unchanged, and each piece of the picture the model says
/// it left out — the model's own account, which the live check measures for honesty.
/// </para>
/// </remarks>
internal sealed class ImagePromptCompactor(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    IClock clock)
{
    /// <summary>The template a shorter prompt is written from.</summary>
    public const string TemplateId = "image.prompt.compact";

    private static readonly JsonSerializerOptions SegmentJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = ImagePromptCompactionInputs.ReadVerbatim(context.Inputs, ImagePromptCompactionInputs.SourcePrompt);
        var avoid = ImagePromptCompactionInputs.ReadVerbatim(context.Inputs, ImagePromptCompactionInputs.Avoid);
        var limit = ImagePromptCompactionInputs.ReadPositive(context.Inputs, ImagePromptCompactionInputs.CharacterLimit);
        var budget = ImagePromptCompactionInputs.ReadPositive(context.Inputs, ImagePromptCompactionInputs.PromptBudget);

        if (source is null || limit is null || budget is not { } promptBudget
            || promptBudget < AiPolicy.ImagePromptMinLength)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "This request does not say what to shorten, or to what length.",
                []);
        }

        var template = templates.Get(TemplateId);

        // Asked for less than the budget and held to all of it: see AiPolicy.ImagePromptCompactionAimPercent.
        var aim = Math.Max(
            AiPolicy.ImagePromptMinLength, promptBudget * AiPolicy.ImagePromptCompactionAimPercent / 100);

        // A model counts words far better than characters, and is more willing to cut when told how much: the first
        // live run, given a character count alone, came back at four fifths of its prompt when asked for half.
        var words = Math.Max(1, aim / AiPolicy.ImagePromptCompactionCharactersPerWord);
        var cut = Math.Clamp(100 - (aim * 100 / source.Length), 1, 99);

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sourceLength"] = source.Length.ToString(CultureInfo.InvariantCulture),
                ["characterBudget"] = aim.ToString(CultureInfo.InvariantCulture),
                ["wordBudget"] = words.ToString(CultureInfo.InvariantCulture),
                ["cutPercent"] = cut.ToString(CultureInfo.InvariantCulture),
            }))

            // The version pinned in the schema, so the one corrective re-ask is not spent on a guessed "1.0" — which
            // is what the first live run's every first answer was.
            .WithOutputSchema(AiImagePromptCompactionOutputSchema.Json, template.OutputSchemaVersion)

            // The creator's prompt is untrusted for the reason a brief is: it is text they typed or pasted, and an
            // instruction inside it is material to shorten, never a command. The same fence IMG-002 puts it in.
            .WithUntrustedText(context.WorkspaceId, JsonSerializer.Serialize(new { prompt = source }, SegmentJson))
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiImagePromptCompactionOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiImagePromptCompactionOutputValidator.For(promptBudget))
            {
                Task = AiTaskType.ImagePrompt,
            },
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!, source, avoid, limit.Value);
        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
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
            clock.UtcNow);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, outcome.Attempts)
            : AiTaskHandlerOutcome.ForFailure(
                assembly.Failure!.Category, assembly.Failure.Message, outcome.Attempts);
    }

    /// <summary>
    /// The shorter prompt as IMG-002's rows — the text, and the creator's avoid list as they wrote it — and the
    /// warnings that say what happened to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's own warning comes first and is on every proposal, because "this was shortened by a model" is
    /// the server's fact and not one to leave to the model to mention. Each left-out phrase is its own warning, so a
    /// creator reads them as a list.
    /// </para>
    /// <para>
    /// <strong>Words the creator did not write are named too</strong>, by the server and not the model. The template
    /// says to add nothing, and no validator can tell a reworded "jug" from an added "copper pan" — but code can say
    /// which words are new, and leave the judgement to the creator reading it. A warning, never a refusal: most new
    /// words are a shorter way of saying what was there.
    /// </para>
    /// </remarks>
    internal static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiImagePromptCompactionOutputDocument document, string source, string? avoid, int limit)
    {
        var targetId = Guid.NewGuid();

        var changes = new List<AiResolvedChange>
        {
            new(
                AiChangeKind.Add,
                AiChangeTargetKind.ImagePrompt,
                targetId,
                FieldName: null,
                BeforeValue: null,
                document.Prompt.Trim(),
                ProposedPosition: 0,
                0),
        };

        if (avoid is not null)
        {
            // Verbatim: the request's own text, never the model's — which was never shown it.
            changes.Add(new AiResolvedChange(
                AiChangeKind.Set,
                AiChangeTargetKind.ImagePrompt,
                targetId,
                ImagePromptFields.Avoid,
                BeforeValue: null,
                avoid,
                ProposedPosition: null,
                1));
        }

        var warnings = new List<AiOutputWarning>
        {
            Limitation(string.Format(
                CultureInfo.InvariantCulture,
                "Shortened by AI to fit the {0:N0} characters the picture quality you chose can take. Your own "
                    + "prompt is unchanged{1}. Check that the shorter one still describes the picture you want.",
                limit,
                avoid is null ? string.Empty : ", and your avoid list is carried over word for word")),
        };

        if (NewWords(source, document.Prompt) is { Count: > 0 } added)
        {
            warnings.Add(Limitation(
                $"Words in the shorter prompt that are not in yours: {string.Join(", ", added)}. Check that nothing "
                    + "was added to the picture."));
        }

        warnings.AddRange(document.LeftOut.Select(phrase => Limitation($"Left out to fit: {phrase.Trim()}")));

        warnings.AddRange(document.Warnings.Select(warning => new AiOutputWarning
        {
            Kind = warning.Kind,
            Message = warning.Message,
            ChangeIndex = 0,
        }));

        return (changes, warnings);
    }

    /// <summary>
    /// The content words of <paramref name="shorter"/> that <paramref name="source"/> does not use, in order, at most
    /// <see cref="AiPolicy.MaxImagePromptNewWords"/>.
    /// </summary>
    /// <remarks>
    /// Crude by design, and erring towards naming too much: words of four letters or more, compared without case,
    /// with a plural "s" or "es" taken as the same word. Short words are left out because they are the joins a
    /// shorter sentence needs, and so are the longer joining words listed below.
    /// </remarks>
    internal static IReadOnlyList<string> NewWords(string source, string shorter)
    {
        var known = Words(source).Select(Stem).ToHashSet(StringComparer.Ordinal);

        return [.. Words(shorter)
            .Where(word => !known.Contains(Stem(word)) && !JoiningWords.Contains(word))
            .Distinct(StringComparer.Ordinal)
            .Take(AiPolicy.MaxImagePromptNewWords)];
    }

    private static IEnumerable<string> Words(string text) =>
        WordPattern.Matches(text)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(word => word.Length >= 4);

    private static string Stem(string word) =>
        word.EndsWith("es", StringComparison.Ordinal) && word.Length > 4 ? word[..^2]
        : word.EndsWith('s') ? word[..^1]
        : word;

    private static readonly Regex WordPattern = new(
        @"\p{L}+(?:'\p{L}+)?",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Words a shorter sentence needs to join what the creator wrote, and which add nothing to a picture.</summary>
    private static readonly HashSet<string> JoiningWords = new(StringComparer.Ordinal)
    {
        "with", "from", "into", "onto", "over", "under", "beside", "behind", "above", "below", "near", "against",
        "while", "where", "which", "that", "this", "their", "there", "then", "than", "both", "each", "some",
    };

    private static AiOutputWarning Limitation(string message) =>
        new() { Kind = AiWarningKind.Limitation, Message = message, ChangeIndex = 0 };
}
