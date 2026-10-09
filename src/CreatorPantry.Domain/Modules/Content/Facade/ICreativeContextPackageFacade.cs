using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The boundary an AI task handler crosses to learn what a piece of creative work is about (AF.1.5).
/// </summary>
/// <remarks>
/// <para>
/// A facade of its own rather than a method on <see cref="ICreativeContextFacade"/>: that one is the creator's
/// editing seam, with validators and an idempotent executor behind it, and a handler wanting a read-only
/// package would resolve all of it to get one.
/// </para>
/// <para>
/// <strong>There is no workspace parameter and no role gate.</strong> The workspace is the resolved context's —
/// in the worker, the one the operation was validated against before the handler ran — and Viewer, the lowest
/// role, may read a context. What a caller receives is bounded by the task type it names, which is its own
/// and never a client's to choose.
/// </para>
/// </remarks>
public interface ICreativeContextPackageFacade
{
    /// <inheritdoc cref="ICreativeContextPackageBusiness.AssembleAsync"/>
    Task<OperationResult<CreativeContextPackage>> AssembleAsync(
        Guid contextId, AiTaskType taskType, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextPackageFacade"/>
internal sealed class CreativeContextPackageFacade(ICreativeContextPackageBusiness business)
    : ICreativeContextPackageFacade
{
    public Task<OperationResult<CreativeContextPackage>> AssembleAsync(
        Guid contextId, AiTaskType taskType, CancellationToken cancellationToken) =>
        business.AssembleAsync(contextId, taskType, cancellationToken);
}
