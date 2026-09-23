using CreatorPantry.Domain.Modules.Vocabulary.Managers;
namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A surface form resolving to one <see cref="CookingTechnique"/> — "sauté", "pan fry", "stir-fried".
/// </summary>
/// <remarks>
/// <para>
/// Aliasing carries a second weight here. An alias that points "water bath" at
/// <c>water-bath-canning</c> also decides whether that text reaches a technique carrying
/// <see cref="CookingTechnique.RequiresSafetyCaution"/>, so an alias attached to the wrong technique can
/// route text past a caution. Aliases whose plain reading spans a cautioned and an uncautioned technique
/// belong on neither; leave the text unresolved instead.
/// </para>
/// <para>
/// Unresolved is a safe outcome. Not recognising a technique loses a filter value; misrecognising one
/// attaches guidance written for a different process.
/// </para>
/// </remarks>
public class CookingTechniqueAlias : VocabularyAlias;
