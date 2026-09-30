using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Errors raised from more than one layer, built once so they cannot drift apart.</summary>
internal static class BrandProfileErrors
{
    /// <summary>
    /// Logo links are refused until the media seam can verify an asset belongs to this workspace. Raised by the
    /// facade before anything is recorded, and by Business as the backstop for any caller that reaches it
    /// without the facade.
    /// </summary>
    public static OperationError AssetsUnprocessable() => OperationError.Validation(
        BrandErrorCodes.AssetsUnprocessable,
        "The brand profile could not be saved.",
        [(nameof(CreateBrandProfileViewModel.Assets), "Logos cannot be linked yet: the media library is not available to verify them.")]);
}
