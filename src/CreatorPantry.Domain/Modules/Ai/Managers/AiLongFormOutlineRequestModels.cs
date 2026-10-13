using System.ComponentModel;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What an outline may be asked to do, beyond which piece it is for (LF.2.2).</summary>
/// <remarks>
/// It cannot carry a heading, a body, a prompt, a template, a model, a provider parameter, a schema version or
/// a workspace. The sections a creator wants are named by role — a word from a closed vocabulary — so nothing
/// they type here reaches a model as an instruction.
/// </remarks>
public class RequestLongFormOutlineViewModel
{
    [Description("How long the finished piece should be, in words. Omit it to use the usual length for this kind of piece.")]
    public int? TargetWords { get; set; }

    [Description("Sections the plan must include, by role. Omit it to leave the plan to the outline.")]
    public IReadOnlyList<LongFormSectionRole>? WantedRoles { get; set; }
}

/// <summary>Starts a long-form draft for one piece of creative work and asks for its outline (LF.2.2).</summary>
public sealed class StartLongFormDraftViewModel : RequestLongFormOutlineViewModel
{
    [Description("The creative context the piece is written for.")]
    public Guid CreativeContextId { get; set; }

    [Description("What kind of piece it is.")]
    public LongFormKind? Kind { get; set; }
}

public sealed class RequestLongFormOutlineViewModelValidator : AbstractValidator<RequestLongFormOutlineViewModel>
{
    public RequestLongFormOutlineViewModelValidator() => Rules(this);

    /// <summary>The rules both requests share, so a draft's first outline and a later one are held to one set.</summary>
    internal static void Rules<TModel>(AbstractValidator<TModel> validator)
        where TModel : RequestLongFormOutlineViewModel
    {
        validator.RuleFor(model => model.TargetWords)
            .Must(words => words is null or (>= AiPolicy.LongFormTargetWordsMin and <= AiPolicy.LongFormTargetWordsMax))
            .WithMessage(
                $"Ask for between {AiPolicy.LongFormTargetWordsMin} and {AiPolicy.LongFormTargetWordsMax} words.")
            .OverridePropertyName("targetWords");

        validator.RuleFor(model => model.WantedRoles)
            .Must(roles => roles is null || roles.Count <= AiPolicy.MaxLongFormOutlineSections)
            .WithMessage($"Ask for at most {AiPolicy.MaxLongFormOutlineSections} sections.")
            .Must(roles => roles is null || roles.All(role => Enum.IsDefined(role)))
            .WithMessage("Name each section by one of the roles a plan can have.")
            .OverridePropertyName("wantedRoles");
    }
}

public sealed class StartLongFormDraftViewModelValidator : AbstractValidator<StartLongFormDraftViewModel>
{
    public StartLongFormDraftViewModelValidator()
    {
        RuleFor(model => model.CreativeContextId)
            .NotEmpty()
            .WithMessage("Name the piece of work this draft is for.")
            .OverridePropertyName("creativeContextId");

        RuleFor(model => model.Kind)
            .Must(kind => kind is { } value && Enum.IsDefined(value))
            .WithMessage("Say whether this is a blog article or a newsletter issue.")
            .OverridePropertyName("kind");

        RequestLongFormOutlineViewModelValidator.Rules(this);
    }
}

/// <summary>The codes LF.2.2's outline requests answer with, beyond the ones the content module's draft has.</summary>
public static class AiLongFormOutlineRequestErrors
{
    public const string RequestInvalid = "ai.longFormOutline.invalid_request";

    public const string TaskNotEnabled = "ai.longFormOutline.not_enabled";

    /// <summary>
    /// No such outline request for that draft: an unknown id, another workspace's request, a request that ran
    /// a different task, and a request for a different draft are one absence.
    /// </summary>
    public const string RequestNotFound = "content.long_form.outline_request.not_found";
}

/// <summary>A draft, and the outline request just made for it.</summary>
/// <param name="Request">Poll it at the draft's outline-requests route by its id.</param>
public sealed record StartedLongFormDraftServiceModel(
    LongFormDraftServiceModel Draft, AiProposalStatusServiceModel Request);

/// <summary>Where an outline request has got to, and the draft as it stands now.</summary>
/// <param name="Draft">Its <c>latest</c> is the generated plan once the request has finished and landed.</param>
public sealed record LongFormOutlineRequestStatusServiceModel(
    AiProposalStatusServiceModel Request, LongFormDraftServiceModel Draft);
