using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Shape checks for creating a guide, and the one place blanks are dropped — so what is validated and what is
/// written cannot disagree about what counted as an answer.
/// </summary>
internal static class BrandStyleGuideInput
{
    /// <summary>Whitespace is not an answer. The text of one that is stays exactly as typed.</summary>
    public static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private static readonly (BrandStyleGuideSectionKey Key, Func<BrandStyleGuideQuestionnaire, string?> Answer, string Field)[] Questions =
    [
        (BrandStyleGuideSectionKey.Voice, q => q.Voice, nameof(BrandStyleGuideQuestionnaire.Voice)),
        (BrandStyleGuideSectionKey.Tone, q => q.Tone, nameof(BrandStyleGuideQuestionnaire.Tone)),
        (BrandStyleGuideSectionKey.Audience, q => q.Audience, nameof(BrandStyleGuideQuestionnaire.Audience)),
        (BrandStyleGuideSectionKey.Vocabulary, q => q.Vocabulary, nameof(BrandStyleGuideQuestionnaire.Vocabulary)),
        (BrandStyleGuideSectionKey.UserNotes, q => q.Notes, nameof(BrandStyleGuideQuestionnaire.Notes)),
    ];

    public static IEnumerable<(string Field, string Message)> Failures(CreateBrandStyleGuideViewModel model)
    {
        var name = BrandProfileInputChecks.Normalize(model.DisplayName);

        if (name is null)
        {
            yield return (nameof(model.DisplayName), "A guide needs a name.");
        }
        else if (name.Length > BrandPolicy.StyleGuideDisplayNameMaxLength)
        {
            yield return (nameof(model.DisplayName),
                $"A guide name can be at most {BrandPolicy.StyleGuideDisplayNameMaxLength} characters.");
        }

        if (BrandProfileInputChecks.Normalize(model.Purpose) is { } purpose
            && purpose.Length > BrandPolicy.StyleGuidePurposeMaxLength)
        {
            yield return (nameof(model.Purpose),
                $"A purpose can be at most {BrandPolicy.StyleGuidePurposeMaxLength} characters.");
        }

        var taken = new HashSet<(BrandStyleGuideSectionKey, string)>();
        var variants = 0;

        if (model.Questionnaire is { } questionnaire)
        {
            foreach (var (key, answer, field) in Questions)
            {
                if (answer(questionnaire) is { } text && Has(text))
                {
                    taken.Add((key, string.Empty));

                    if (text.Length > BrandPolicy.StyleGuideSectionBodyMaxLength)
                    {
                        yield return ($"Questionnaire.{field}", BodyTooLong);
                    }
                }
            }

            foreach (var failure in Rules(nameof(questionnaire.AlwaysDo), questionnaire.AlwaysDo))
            {
                yield return failure;
            }

            foreach (var failure in Rules(nameof(questionnaire.NeverDo), questionnaire.NeverDo))
            {
                yield return failure;
            }
        }

        var sections = model.Sections ?? [];

        for (var index = 0; index < sections.Count; index++)
        {
            if (sections[index] is not { } section || !Has(section.Body))
            {
                // Blank stays blank: an entry with no body is no entry, and says nothing about its key.
                continue;
            }

            var path = $"Sections[{index}]";

            if (section.SectionKey is not { } key || !Enum.IsDefined(key))
            {
                yield return ($"{path}.SectionKey", "Choose which section this is.");
                continue;
            }

            if (section.Body!.Length > BrandPolicy.StyleGuideSectionBodyMaxLength)
            {
                yield return ($"{path}.Body", BodyTooLong);
            }

            var channel = BrandProfileInputChecks.Normalize(section.ChannelKey) ?? string.Empty;

            if (key is BrandStyleGuideSectionKey.ChannelVariant)
            {
                if (channel.Length == 0 || !BrandProfileInputChecks.IsChannelKey(channel))
                {
                    yield return ($"{path}.ChannelKey", "A channel variant needs a channel key, such as instagram.");
                    continue;
                }

                if (++variants > BrandPolicy.MaxStyleGuideChannelVariants)
                {
                    yield return ($"{path}.ChannelKey",
                        $"A guide can have at most {BrandPolicy.MaxStyleGuideChannelVariants} channel variants.");
                    continue;
                }
            }
            else if (channel.Length > 0)
            {
                yield return ($"{path}.ChannelKey", "Only a channel variant names a channel.");
                continue;
            }

            if (!taken.Add((key, channel)))
            {
                yield return ($"{path}.SectionKey", "This section is already given once in the request.");
            }
        }

        var rules = Compose(model).Rules;

        if (rules.Count > BrandPolicy.MaxStyleGuideRules)
        {
            yield return (nameof(model.Questionnaire),
                $"A guide can have at most {BrandPolicy.MaxStyleGuideRules} do and don't rules.");
        }

        var seenRules = new HashSet<(BrandStyleGuideRuleKind, string)>();

        foreach (var rule in rules)
        {
            if (!seenRules.Add((rule.Kind, rule.Text.Trim().ToUpperInvariant())))
            {
                yield return (nameof(model.Questionnaire), "The same rule is listed more than once.");
                break;
            }
        }

        var sources = model.SourceDocuments ?? [];

        if (sources.Count > BrandPolicy.MaxStyleGuideSourceLinks)
        {
            yield return (nameof(model.SourceDocuments),
                $"A guide can cite at most {BrandPolicy.MaxStyleGuideSourceLinks} source documents.");
        }

        var seenSources = new HashSet<(Guid, int)>();

        for (var index = 0; index < sources.Count; index++)
        {
            var path = $"SourceDocuments[{index}]";

            if (sources[index] is not { DocumentId: { } documentId, VersionNumber: { } version }
                || documentId == Guid.Empty
                || version < 1)
            {
                yield return (path, "Name a source document and one of its version numbers.");
            }
            else if (!seenSources.Add((documentId, version)))
            {
                yield return (path, "This version is already cited once in the request.");
            }
        }
    }

    /// <summary>What will be written, with blanks dropped. Assumes <see cref="Failures"/> found nothing.</summary>
    public static BrandStyleGuideDraft Compose(CreateBrandStyleGuideViewModel model)
    {
        var sections = new List<BrandStyleGuideSectionServiceModel>();

        if (model.Questionnaire is { } questionnaire)
        {
            foreach (var (key, answer, _) in Questions)
            {
                if (answer(questionnaire) is { } text && Has(text))
                {
                    sections.Add(new BrandStyleGuideSectionServiceModel(key, null, text));
                }
            }
        }

        foreach (var section in model.Sections ?? [])
        {
            if (section is { SectionKey: { } key } && Has(section.Body))
            {
                sections.Add(new BrandStyleGuideSectionServiceModel(
                    key,
                    key is BrandStyleGuideSectionKey.ChannelVariant
                        ? BrandProfileInputChecks.Normalize(section.ChannelKey)
                        : null,
                    section.Body!));
            }
        }

        var rules = new List<BrandStyleGuideRuleServiceModel>();
        rules.AddRange(Items(model.Questionnaire?.AlwaysDo)
            .Select(text => new BrandStyleGuideRuleServiceModel(BrandStyleGuideRuleKind.Do, text)));
        rules.AddRange(Items(model.Questionnaire?.NeverDo)
            .Select(text => new BrandStyleGuideRuleServiceModel(BrandStyleGuideRuleKind.Dont, text)));

        return new BrandStyleGuideDraft(
            BrandProfileInputChecks.Normalize(model.DisplayName)!,
            BrandProfileInputChecks.Normalize(model.Purpose),
            [.. sections.OrderBy(section => section.SectionKey).ThenBy(section => section.ChannelKey, StringComparer.Ordinal)],
            rules,
            [.. (model.SourceDocuments ?? []).Where(source => source is not null).Select(source => source!)]);
    }

    private static string BodyTooLong =>
        $"A section can be at most {BrandPolicy.StyleGuideSectionBodyMaxLength} characters.";

    private static IEnumerable<string> Items(IReadOnlyList<string?>? items) =>
        (items ?? []).Where(Has).Select(item => item!);

    private static IEnumerable<(string Field, string Message)> Rules(string field, IReadOnlyList<string?>? items)
    {
        for (var index = 0; index < (items?.Count ?? 0); index++)
        {
            if (Has(items![index]) && items[index]!.Length > BrandPolicy.StyleGuideRuleTextMaxLength)
            {
                yield return ($"Questionnaire.{field}[{index}]",
                    $"A rule can be at most {BrandPolicy.StyleGuideRuleTextMaxLength} characters.");
            }
        }
    }

}

public sealed class CreateBrandStyleGuideViewModelValidator : AbstractValidator<CreateBrandStyleGuideViewModel>
{
    public CreateBrandStyleGuideViewModelValidator()
    {
        RuleFor(model => model).Custom((model, context) =>
        {
            foreach (var (field, message) in BrandStyleGuideInput.Failures(model))
            {
                context.AddFailure(field, message);
            }
        });
    }
}
