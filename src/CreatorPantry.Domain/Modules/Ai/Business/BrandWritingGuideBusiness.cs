using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

public interface IBrandWritingGuideBusiness
{
    Task<OperationResult<BrandWritingGuideServiceModel>> GetAsync(
        BrandWritingGuideViewModel model, CancellationToken cancellationToken);
}

/// <summary>
/// Answers "which brand guide would this writing screen use, and what would it ask for?" — a read, with no
/// provider call, no retrieval and no write.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same selection rules as generation, not a copy of them.</strong> What a task is grounded in is
/// decided by <see cref="BrandContextAssembler.SelectGuidance"/>; calling it here is what keeps this preview
/// from promising guidance a generation would not send. It deliberately does <em>not</em> call the whole
/// assembler: that also retrieves source passages, which is a semantic search a page load must not pay for.
/// </para>
/// <para>
/// <strong>Reads the active guide only.</strong> This is the "use my brand voice" default. It names no guide
/// by id, and the workspace is resolved by the facade it calls, so there is nothing here a client could point at
/// another workspace.
/// </para>
/// </remarks>
internal sealed class BrandWritingGuideBusiness(
    IBrandStyleGuideFacade guides,
    IContentChannelCatalog channels) : IBrandWritingGuideBusiness
{
    /// <summary>Guide Do/Don't lines shown. They are guide-wide, so the preview cannot rank them; it stops early.</summary>
    private const int MaxRules = 5;

    public async Task<OperationResult<BrandWritingGuideServiceModel>> GetAsync(
        BrandWritingGuideViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var taskType = BrandWritingGuideTasks.Parse(model.Task)
            ?? throw new InvalidOperationException("The facade validates the task before this is called.");

        var channelKey = string.IsNullOrWhiteSpace(model.Channel) ? null : model.Channel.Trim();
        var channel = channelKey is null ? null : channels.Find(channelKey);

        if (channelKey is not null && channel is null)
        {
            return OperationResult<BrandWritingGuideServiceModel>.Failure(OperationError.Validation(
                BrandWritingGuideErrors.RequestInvalid,
                "The request is not valid.",
                [("channel", "That is not a channel this product knows how to write for.")]));
        }

        var active = await guides.GetActiveAsync(cancellationToken);

        // Unreadable and absent read the same: nothing to show, and the writing screen carries on without one.
        if (!active.Succeeded || active.Value is not { } guide)
        {
            return OperationResult<BrandWritingGuideServiceModel>.Success(new BrandWritingGuideServiceModel(null, []));
        }

        var guidance = BrandContextAssembler.SelectGuidance(
            taskType, channelKey, guide.Version, new SortedSet<BrandContextConflict>(), new SortedSet<BrandContextOmission>());

        var rules = new List<BrandWritingGuideRuleServiceModel>();
        var applied = new List<string>();

        foreach (var item in guidance)
        {
            var label = item.Origin == BrandContextOrigin.GuideChannelVariant && channel is not null
                ? channel.DisplayName
                : BrandWritingGuideText.Label(item.SectionKey);

            applied.Add(label);
            rules.Add(new BrandWritingGuideRuleServiceModel(label, BrandWritingGuideText.Summarise(item.Body)));
        }

        rules.AddRange(guide.Version.Rules
            .Take(MaxRules)
            .Select(rule => new BrandWritingGuideRuleServiceModel(
                rule.Kind == BrandStyleGuideRuleKind.Do ? "Do" : "Don't",
                BrandWritingGuideText.Summarise(rule.Text))));

        return OperationResult<BrandWritingGuideServiceModel>.Success(new BrandWritingGuideServiceModel(
            new BrandWritingGuideActiveServiceModel(
                guide.GuideId,
                guide.DisplayName,
                guide.Version.VersionNumber,
                guide.Version.Approval?.ApprovedAt,
                guide.StaleSourceCount > 0,
                guide.StaleSourceCount,
                applied),
            rules));
    }
}
