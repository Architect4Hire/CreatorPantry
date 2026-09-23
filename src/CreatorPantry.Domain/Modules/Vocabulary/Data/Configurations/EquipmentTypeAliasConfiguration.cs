using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

internal sealed class EquipmentTypeAliasConfiguration()
    : VocabularyAliasConfiguration<EquipmentTypeAlias, EquipmentType>("EquipmentTypeAliases", "EquipmentTypeId");
