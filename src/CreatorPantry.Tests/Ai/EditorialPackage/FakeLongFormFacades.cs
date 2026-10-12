using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// The content module's long-form reads as the SEO package sees them (LF.5.3), scripted. What those facades
/// really read, and that they read only the resolved workspace's, are their own tests'; these stand in so the
/// AI module's tests can say what a draft is without composing the content module.
/// </summary>
internal sealed class FakeLongFormDocumentFacade : ILongFormDocumentFacade
{
    private readonly Dictionary<Guid, LongFormAssembledDocumentServiceModel> _documents = [];

    public int Reads { get; private set; }

    /// <summary>Runs before each read is answered, with that read's number, so a test can move a draft mid-run.</summary>
    public Action<int>? BeforeRead { get; set; }

    public void Hold(
        Guid draftId,
        string markdown,
        LongFormDraftStatus status = LongFormDraftStatus.Accepted,
        int? acceptedRevisionNumber = 4,
        LongFormKind kind = LongFormKind.BlogArticle,
        LongFormFramingServiceModel? framing = null) =>
        _documents[draftId] = new LongFormAssembledDocumentServiceModel(
            draftId,
            Guid.NewGuid(),
            kind,
            status,
            acceptedRevisionNumber ?? 4,
            "AAAAAAAAB9E=",
            markdown,
            WordCount: 0,
            CharacterCount: 0,
            LongFormPolicy.TargetWords(kind),
            LongFormWordTargetStatus.NotAssessed,
            LongFormPolicy.SectionMaxCharacters,
            LongFormPolicy.DocumentMaxCharacters,
            DocumentOverCeiling: false,
            Sections: [],
            NeedsConfirmation: [],
            acceptedRevisionNumber,
            AcceptedAt: null,
            framing,
            UnresolvedItems: []);

    public void Forget(Guid draftId) => _documents.Remove(draftId);

    public Task<OperationResult<LongFormAssembledDocumentServiceModel>> GetDocumentAsync(Guid draftId, CancellationToken cancellationToken)
    {
        Reads++;
        BeforeRead?.Invoke(Reads);

        return Task.FromResult(_documents.TryGetValue(draftId, out var document)
            ? OperationResult<LongFormAssembledDocumentServiceModel>.Success(document)
            : OperationResult<LongFormAssembledDocumentServiceModel>.Failure(new OperationError(
                ContentErrorCodes.LongFormDraftNotFound, "That long-form draft does not exist.", new Dictionary<string, string[]>())));
    }

    public Task<OperationResult<LongFormAssembledDocumentServiceModel>> AcceptAsync(
        string actorUserId, Guid draftId, AcceptLongFormDocumentViewModel model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("An SEO package never accepts a piece.");
}

/// <inheritdoc cref="FakeLongFormDocumentFacade"/>
internal sealed class FakeLongFormExportFacade : ILongFormExportFacade
{
    private readonly Dictionary<Guid, string?> _titles = [];

    public void Title(Guid draftId, string? title) => _titles[draftId] = title;

    public Task<OperationResult<LongFormExportServiceModel>> ExportAsync(
        Guid draftId, LongFormExportFormat format, CancellationToken cancellationToken) =>
        Task.FromResult(_titles.TryGetValue(draftId, out var title)
            ? OperationResult<LongFormExportServiceModel>.Success(new LongFormExportServiceModel(
                draftId, LongFormKind.BlogArticle, format, 4, title, "(not read by the package)", "article-r4.md", []))
            : OperationResult<LongFormExportServiceModel>.Failure(new OperationError(
                ContentErrorCodes.LongFormDraftNotFound, "That long-form draft does not exist.", new Dictionary<string, string[]>())));
}
