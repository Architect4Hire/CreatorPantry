using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

internal sealed class CookingTechniqueConfiguration() : ControlledVocabularyConfiguration<CookingTechnique>("CookingTechniques")
{
    protected override void ConfigureVocabulary(EntityTypeBuilder<CookingTechnique> builder)
    {
        // Not nullable: a technique nobody has considered reads false, which means "no caution attached",
        // never "safe". Three-valued storage would invite a caller to treat null and false as different
        // kinds of reassurance, and neither is any.
        //
        // No index on it either. The cautioned techniques are a handful of rows in a vocabulary of dozens,
        // read from an already-cached list; a low-cardinality index here would cost a write path and earn
        // nothing.
        builder.Property(technique => technique.RequiresSafetyCaution)
            .IsRequired();
    }
}
