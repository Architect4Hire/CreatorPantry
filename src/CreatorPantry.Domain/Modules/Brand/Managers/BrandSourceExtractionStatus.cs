namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// How one extraction attempt ended. There is no pending value: a version with no extraction row has not been
/// extracted yet.
/// </summary>
public enum BrandSourceExtractionStatus
{
    /// <summary>Text was produced and stored; the only status that carries an object key.</summary>
    Succeeded = 1,

    /// <summary>The format cannot be read as text — a scan or an image. A review state, not an error.</summary>
    Unsupported = 2,

    Failed = 3,
}
