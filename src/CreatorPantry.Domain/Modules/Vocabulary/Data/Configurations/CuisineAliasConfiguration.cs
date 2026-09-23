using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

internal sealed class CuisineAliasConfiguration()
    : VocabularyAliasConfiguration<CuisineAlias, Cuisine>("CuisineAliases", "CuisineId");
