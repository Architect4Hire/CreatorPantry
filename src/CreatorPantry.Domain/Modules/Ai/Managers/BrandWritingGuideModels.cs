using System.ComponentModel;
using System.Text;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which writing screen is asking what brand guidance would apply (11A.21a).
/// </summary>
/// <param name="Task">
/// <c>editorial-package</c> or <c>seo-package</c>. A discriminator the API recognises and maps to a task
/// itself — the client never names a task type, and a task that is grounded in no brand context is refused
/// rather than answered with an empty preview that would imply it was.
/// </param>
/// <param name="Channel">
/// Optional channel key from the content-channel catalogue. Names the channel whose own guidance is wanted
/// alongside the guide's general voice.
/// </param>
public sealed record BrandWritingGuideViewModel(
    [property: FromQuery(Name = "task")]
    [property: Description("editorial-package or seo-package.")]
    string? Task = null,
    [property: FromQuery(Name = "channel")]
    [property: Description("Optional content-channel key.")]
    string? Channel = null);

/// <summary>One line of the plain-language preview: what the guide asks for, in the creator's own words.</summary>
/// <param name="Label">A creator-facing name — "Tone", "Instagram", "Do" — never a section key.</param>
/// <param name="Summary">The creator's own text from the guide, shortened. Never a prompt fragment.</param>
public sealed record BrandWritingGuideRuleServiceModel(string Label, string Summary);

/// <summary>The workspace's active guide as a writing screen needs to show it.</summary>
/// <param name="IsStale">True when any source the active version cites has since been replaced.</param>
/// <param name="StaleSourceCount">How many. The version history's own count.</param>
/// <param name="AppliedSections">Plain labels of the guide parts this task actually uses.</param>
public sealed record BrandWritingGuideActiveServiceModel(
    Guid GuideId,
    string Name,
    int VersionNumber,
    DateTimeOffset? ApprovedAt,
    bool IsStale,
    int StaleSourceCount,
    IReadOnlyList<string> AppliedSections);

/// <summary>
/// What a writing screen shows about brand guidance before the creator asks for anything.
/// </summary>
/// <param name="ActiveGuide">Null when the workspace has activated no guide. That is an answer, not an error.</param>
/// <param name="Rules">
/// What the active guide would contribute to this task and channel, in the same order and by the same selection
/// rules generation uses. Empty when there is no guide or it has nothing for this task.
/// </param>
public sealed record BrandWritingGuideServiceModel(
    BrandWritingGuideActiveServiceModel? ActiveGuide,
    IReadOnlyList<BrandWritingGuideRuleServiceModel> Rules);

public static class BrandWritingGuideErrors
{
    public const string RequestInvalid = "ai.brandWritingGuide.invalid_request";
}

/// <summary>The writing tasks this preview answers for, as the strings a client sends.</summary>
public static class BrandWritingGuideTasks
{
    public const string EditorialPackage = "editorial-package";

    public const string SeoPackage = "seo-package";

    public static AiTaskType? Parse(string? value) => value?.Trim() switch
    {
        EditorialPackage => AiTaskType.EditorialPackage,
        SeoPackage => AiTaskType.SeoPackage,
        _ => null,
    };
}

public sealed class BrandWritingGuideViewModelValidator : AbstractValidator<BrandWritingGuideViewModel>
{
    public BrandWritingGuideViewModelValidator()
    {
        RuleFor(model => model.Task)
            .Must(task => BrandWritingGuideTasks.Parse(task) is not null)
            .WithMessage($"Task must be {BrandWritingGuideTasks.EditorialPackage} or {BrandWritingGuideTasks.SeoPackage}.");

        RuleFor(model => model.Channel).MaximumLength(64);
    }
}

/// <summary>Creator-facing wording for a guide section, and the shortening of the creator's own text.</summary>
public static class BrandWritingGuideText
{
    /// <summary>How much of a creator's own text one preview line carries before it is shortened.</summary>
    public const int SummaryLimit = 160;

    /// <summary>
    /// A readable name for a section. A key added later still gets a sensible one by splitting its name, so the
    /// preview never shows a bare enum value; <c>BrandWritingGuideTextTests</c> walks the enum regardless.
    /// </summary>
    public static string Label(BrandStyleGuideSectionKey key) => key switch
    {
        BrandStyleGuideSectionKey.WritingStyle => "Writing style",
        BrandStyleGuideSectionKey.PointOfView => "Point of view",
        BrandStyleGuideSectionKey.SentenceRhythm => "Sentence rhythm",
        BrandStyleGuideSectionKey.CallsToAction => "Calls to action",
        BrandStyleGuideSectionKey.BlogGuidance => "Blog",
        BrandStyleGuideSectionKey.SocialGuidance => "Social",
        BrandStyleGuideSectionKey.ChannelVariant => "Channel",
        _ => Split(key.ToString()),
    };

    /// <summary>
    /// The creator's text on one line, cut at a word boundary with an ellipsis when it is longer than
    /// <see cref="SummaryLimit"/>. Whitespace is collapsed, nothing is paraphrased.
    /// </summary>
    public static string Summarise(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (text.Length <= SummaryLimit)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', SummaryLimit);

        return text[..(cut > 0 ? cut : SummaryLimit)].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    private static string Split(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(name[index]));
            }
            else
            {
                builder.Append(name[index]);
            }
        }

        return builder.ToString();
    }
}
