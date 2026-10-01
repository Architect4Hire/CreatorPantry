namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Who produced an extracted-text artifact.</summary>
public enum BrandSourceExtractionOrigin
{
    /// <summary>A format parser, run by the worker.</summary>
    Extracted = 1,

    /// <summary>The creator, correcting an earlier artifact. Always names its author.</summary>
    Corrected = 2,
}
