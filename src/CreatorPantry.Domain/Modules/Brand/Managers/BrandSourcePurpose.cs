namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What the creator offers a source document as evidence of. It says which part of a style guide the document
/// may inform; it is not itself a statement of voice or style, which <c>BrandStyleGuideVersion</c> owns.
/// </summary>
public enum BrandSourcePurpose
{
    Voice = 1,

    WritingStyle = 2,

    VisualDirection = 3,

    /// <summary>Facts about the brand rather than an example of how it sounds or looks.</summary>
    Background = 4,
}
