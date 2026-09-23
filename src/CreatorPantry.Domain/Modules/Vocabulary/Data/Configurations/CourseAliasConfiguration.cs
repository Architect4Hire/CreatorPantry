using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

internal sealed class CourseAliasConfiguration()
    : VocabularyAliasConfiguration<CourseAlias, Course>("CourseAliases", "CourseId");
