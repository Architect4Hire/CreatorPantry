using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// A content module with no pieces of work in it, for an AI-module fixture whose requests never name one.
/// </summary>
/// <remarks>
/// The AI request seams and handlers that can follow a themed day (AF.7.4) depend on the content module's
/// facades. A fixture that registers those seams without the content module registers this instead, so every
/// lookup answers the way the real facades do for an id the workspace does not have — and a test that did
/// name a piece of work here would be told so, rather than quietly given one.
/// </remarks>
internal sealed class NoCreativeContexts : ICreativeContextFacade, ICreativeContextPackageFacade
{
    private static OperationError NotFound() => new(
        ContentErrorCodes.CreativeContextNotFound,
        "That creative context could not be found.",
        new Dictionary<string, string[]>());

    public Task<OperationResult<CreativeContextServiceModel>> GetAsync(Guid contextId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<CreativeContextServiceModel>.Failure(NotFound()));

    public Task<OperationResult<CreativeContextPackage>> AssembleAsync(
        Guid contextId, AiTaskType taskType, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<CreativeContextPackage>.Failure(NotFound()));

    public Task<OperationResult<CreativeContextPackage>> AssembleAsync(
        Guid contextId, AiTaskType taskType, CreativeContextPackageOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<CreativeContextPackage>.Failure(NotFound()));

    public Task<IdempotentOutcome<CreativeContextServiceModel>> CreateAsync(
        string userId, CreateCreativeContextViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fixture has no content module.");

    public Task<OperationResult<IReadOnlyList<CreativeContextPictureServiceModel>>> ListPicturesAsync(
        Guid contextId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fixture has no content module.");

    public Task<OperationResult<CursorPageServiceModel<CreativeContextSummaryServiceModel>>> ListRecentAsync(
        CreativeContextListViewModel model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fixture has no content module.");

    public Task<OperationResult<CreativeContextServiceModel>> PatchAsync(
        string userId, Guid contextId, PatchCreativeContextViewModel model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fixture has no content module.");

    public Task<OperationResult<CreativeContextServiceModel>> AddReferenceAsync(
        Guid contextId, AddCreativeContextReferenceViewModel model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fixture has no content module.");

    public Task<OperationResult<CreativeContextServiceModel>> RemoveReferenceAsync(
        Guid contextId, Guid referenceId, string? expectedConcurrencyToken, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fixture has no content module.");
}
