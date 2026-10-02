using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ai.Business;

public interface IBrandVisualGuideBusiness
{
    Task<OperationResult<BrandVisualGuideServiceModel>> GetAsync(
        BrandVisualGuideViewModel model, CancellationToken cancellationToken);
}

/// <summary>
/// Answers "which visual style would this image screen use, and which references could it name?" — a read, with no
/// provider call, no embedding and no write (11A.21b).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same rules as generation.</strong> The look comes from <see cref="BrandContextAssembler.SelectGuidance"/>
/// and "usable" from the same indexed-passage read the assembler grounds a named reference on, so the preview cannot
/// promise a reference a generation would drop. It does not call the whole assembler, which would also run a
/// relevance match this page does not need.
/// </para>
/// <para>
/// <strong>Nothing about an image leaves.</strong> A reference is a title, an id and a boolean. No file name, storage
/// location, byte or extracted passage is returned — usability is read from the passages and only their existence is
/// kept.
/// </para>
/// </remarks>
internal sealed class BrandVisualGuideBusiness(
    IBrandStyleGuideFacade guides,
    IBrandSourceDocumentFacade documents,
    IBrandSourcePassageFacade passages,
    ILogger<BrandVisualGuideBusiness> logger) : IBrandVisualGuideBusiness
{
    /// <summary>References listed. Past this the answer says it stopped.</summary>
    private const int MaxReferences = 25;

    public async Task<OperationResult<BrandVisualGuideServiceModel>> GetAsync(
        BrandVisualGuideViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var taskType = BrandVisualGuideTasks.Parse(model.Task)
            ?? throw new InvalidOperationException("The facade validates the task before this is called.");

        var active = await guides.GetActiveAsync(cancellationToken);

        // Unreadable and absent read the same, and with no guide there are no references to offer: the server
        // names no documents when brand context is off, so listing them would invite a choice that does nothing.
        if (!active.Succeeded || active.Value is not { } guide)
        {
            return OperationResult<BrandVisualGuideServiceModel>.Success(
                new BrandVisualGuideServiceModel(null, false, [], null, [], false, ReferencesAvailable: true));
        }

        var guidance = BrandContextAssembler.SelectGuidance(
            taskType, null, guide.Version, new SortedSet<BrandContextConflict>(), new SortedSet<BrandContextOmission>());

        var negative = guidance.FirstOrDefault(item => item.SectionKey == BrandStyleGuideSectionKey.NegativeVisualGuidance);

        var lines = guidance
            .Where(item => item.SectionKey != BrandStyleGuideSectionKey.NegativeVisualGuidance)
            .Select(item => new BrandWritingGuideRuleServiceModel(
                BrandWritingGuideText.Label(item.SectionKey), BrandWritingGuideText.Summarise(item.Body)))
            .ToList();

        var (references, truncated, available) = await ReadReferencesAsync(cancellationToken);

        return OperationResult<BrandVisualGuideServiceModel>.Success(new BrandVisualGuideServiceModel(
            new BrandWritingGuideActiveServiceModel(
                guide.GuideId,
                guide.DisplayName,
                guide.Version.VersionNumber,
                guide.Version.Approval?.ApprovedAt,
                guide.StaleSourceCount > 0,
                guide.StaleSourceCount,
                [.. guidance.Select(item => BrandWritingGuideText.Label(item.SectionKey))]),
            guidance.Count > 0,
            lines,
            negative is null ? null : BrandWritingGuideText.Summarise(negative.Body),
            references,
            truncated,
            available));
    }

    private async Task<(IReadOnlyList<BrandVisualReferenceServiceModel> References, bool Truncated, bool Available)> ReadReferencesAsync(
        CancellationToken cancellationToken)
    {
        // Either read can fail like any I/O. The look is still worth showing, so the answer says the references
        // could not load — rather than letting an empty list read as "none" — and the failure is logged.
        BrandSourceVisualReferenceListServiceModel listed;
        HashSet<Guid> withText;

        try
        {
            listed = await documents.ListVisualReferencesAsync(MaxReferences, cancellationToken);

            var readable = listed.Items
                .Where(item => item.ExtractionState == BrandSourceExtractionState.Succeeded)
                .Select(item => new BrandSourcePassageSelector(item.DocumentId, item.VersionNumber))
                .ToList();

            withText = [.. await passages.ListDocumentsWithTextAsync(readable, cancellationToken)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Visual references could not be read; the style preview is returned without them.");

            return ([], false, false);
        }

        return (
            [.. listed.Items.Select(item => withText.Contains(item.DocumentId)
                ? new BrandVisualReferenceServiceModel(item.DocumentId, item.Title, true, null)
                : new BrandVisualReferenceServiceModel(
                    item.DocumentId, item.Title, false, BrandVisualReferenceReasons.For(item.ExtractionState)))],
            listed.Truncated,
            true);
    }
}
