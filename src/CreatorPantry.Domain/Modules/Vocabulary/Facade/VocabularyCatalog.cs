namespace CreatorPantry.Domain.Modules.Vocabulary.Facade;

/// <summary>
/// Names one controlled vocabulary, so a caller can ask whether an id in it is usable without needing a
/// method per catalogue.
/// </summary>
/// <remarks>
/// Declared in the <c>Facade</c> namespace deliberately: it is part of this module's cross-module contract,
/// and a type in <c>Managers</c> could not be named by another module without reaching past the facade.
/// </remarks>
public enum VocabularyCatalog
{
    Cuisine = 0,
    Course = 1,
    CookingTechnique = 2,

    /// <summary>The allergen vocabulary a dietary and allergen analysis is asked about (14.4).</summary>
    Allergen = 3,

    /// <summary>The dietary profiles a dietary and allergen analysis is asked about (14.4).</summary>
    DietaryProfile = 4,
}
