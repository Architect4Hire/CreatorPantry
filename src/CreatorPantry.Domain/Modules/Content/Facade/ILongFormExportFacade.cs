using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// An accepted long-form draft as one document (LF.5.1). Every call is made under the workspace filter.
/// </summary>
public interface ILongFormExportFacade
{
    /// <inheritdoc cref="ILongFormExportBusiness.ExportAsync" />
    Task<OperationResult<LongFormExportServiceModel>> ExportAsync(
        Guid draftId, LongFormExportFormat format, CancellationToken cancellationToken);
}

internal sealed class LongFormExportFacade(ILongFormExportBusiness business) : ILongFormExportFacade
{
    public Task<OperationResult<LongFormExportServiceModel>> ExportAsync(
        Guid draftId, LongFormExportFormat format, CancellationToken cancellationToken) =>
        Enum.IsDefined(format)
            ? business.ExportAsync(draftId, format, cancellationToken)
            : Task.FromResult(OperationResult<LongFormExportServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.LongFormExportInvalid,
                "The request is not valid.",
                [("format", "Name the format as Markdown or Html.")])));
}
