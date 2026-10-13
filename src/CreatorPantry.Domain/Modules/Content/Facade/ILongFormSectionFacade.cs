using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// What may be asked of, and done to, the sections of a long-form draft (LF.3.1, LF.3.3).
/// </summary>
/// <remarks>
/// <para>
/// The AI module writes a section and may not read or write a draft's rows to do it: cross-module traffic is
/// facade to facade. This is that door, and the controller's for a creator's own edit. Measuring a body is a
/// rule and not a read, so it is <see cref="ILongFormPlanFacade"/>'s.
/// </para>
/// <para>
/// Every call is made under the workspace filter. None takes a workspace.
/// </para>
/// </remarks>
public interface ILongFormSectionFacade
{
    /// <inheritdoc cref="ILongFormSectionBusiness.GetWritingContextAsync" />
    Task<OperationResult<LongFormWritingContextServiceModel>> GetWritingContextAsync(
        Guid draftId, int revisionNumber, Guid sectionId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormSectionBusiness.GetSectionsAsync" />
    Task<OperationResult<LongFormSectionsServiceModel>> GetSectionsAsync(Guid draftId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormSectionBusiness.GetWritableSectionsAsync" />
    Task<OperationResult<LongFormSectionsServiceModel>> GetWritableSectionsAsync(Guid draftId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormSectionBusiness.BeginWritingAsync" />
    Task<OperationResult<LongFormSectionsServiceModel>> BeginWritingAsync(Guid draftId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormSectionBusiness.RecordGeneratedSectionAsync" />
    Task<OperationResult<LongFormSectionsServiceModel>> RecordGeneratedSectionAsync(
        LongFormGeneratedSectionBodyInput input, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormSectionBusiness.EditSectionAsync" />
    Task<OperationResult<LongFormSectionsServiceModel>> EditSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, EditLongFormSectionViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormSectionBusiness.SettleSectionAsync" />
    Task<OperationResult<LongFormSectionsServiceModel>> SettleSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, SettleLongFormSectionViewModel model, CancellationToken cancellationToken);
}

internal sealed class LongFormSectionFacade(
    ILongFormSectionBusiness business,
    IValidator<EditLongFormSectionViewModel> editValidator,
    IValidator<SettleLongFormSectionViewModel> settleValidator) : ILongFormSectionFacade
{
    public Task<OperationResult<LongFormWritingContextServiceModel>> GetWritingContextAsync(
        Guid draftId, int revisionNumber, Guid sectionId, CancellationToken cancellationToken) =>
        business.GetWritingContextAsync(draftId, revisionNumber, sectionId, cancellationToken);

    public Task<OperationResult<LongFormSectionsServiceModel>> GetSectionsAsync(Guid draftId, CancellationToken cancellationToken) =>
        business.GetSectionsAsync(draftId, cancellationToken);

    public Task<OperationResult<LongFormSectionsServiceModel>> GetWritableSectionsAsync(Guid draftId, CancellationToken cancellationToken) =>
        business.GetWritableSectionsAsync(draftId, cancellationToken);

    public Task<OperationResult<LongFormSectionsServiceModel>> BeginWritingAsync(Guid draftId, CancellationToken cancellationToken) =>
        business.BeginWritingAsync(draftId, cancellationToken);

    public Task<OperationResult<LongFormSectionsServiceModel>> RecordGeneratedSectionAsync(
        LongFormGeneratedSectionBodyInput input, CancellationToken cancellationToken) =>
        business.RecordGeneratedSectionAsync(input, cancellationToken);

    public async Task<OperationResult<LongFormSectionsServiceModel>> EditSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, EditLongFormSectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await editValidator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? await business.EditSectionAsync(actorUserId, draftId, sectionId, model, cancellationToken)
            : Invalid(validation);
    }

    public async Task<OperationResult<LongFormSectionsServiceModel>> SettleSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, SettleLongFormSectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await settleValidator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? await business.SettleSectionAsync(actorUserId, draftId, sectionId, model, cancellationToken)
            : Invalid(validation);
    }

    private static OperationResult<LongFormSectionsServiceModel> Invalid(FluentValidation.Results.ValidationResult validation) =>
        OperationResult<LongFormSectionsServiceModel>.Failure(OperationError.Validation(
            ContentErrorCodes.LongFormSectionInvalid,
            "The request is not valid.",
            validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
}
