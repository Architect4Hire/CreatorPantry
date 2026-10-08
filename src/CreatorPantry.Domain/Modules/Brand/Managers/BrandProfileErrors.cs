using CreatorPantry.Domain.Managers.Results;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Errors raised from more than one layer, built once so they cannot drift apart.</summary>
internal static class BrandProfileErrors
{
    /// <summary>
    /// One or more of the submitted logos is not an asset this workspace can link (12.10k).
    /// </summary>
    /// <remarks>
    /// <strong>One sentence for three cases.</strong> An id that names nothing, an asset in another workspace
    /// and one removed from this workspace's library all answer in these words, naming only the position the
    /// caller sent it at — so a save cannot be used to ask what another workspace owns, or whether something
    /// was ever here (tenancy.md).
    /// </remarks>
    /// <param name="positions">The zero-based positions, in the submitted list, of the assets refused.</param>
    public static OperationError AssetsUnprocessable(IEnumerable<int> positions) => OperationError.Validation(
        BrandErrorCodes.AssetsUnprocessable,
        "The brand profile could not be saved.",
        positions.Select(position => (
            $"{nameof(CreateBrandProfileViewModel.Assets)}[{position}].{nameof(BrandAssetInput.MediaAssetId)}",
            "That picture is not in this workspace's library.")));
}
