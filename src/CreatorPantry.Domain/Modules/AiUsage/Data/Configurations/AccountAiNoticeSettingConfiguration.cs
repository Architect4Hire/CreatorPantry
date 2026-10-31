using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Configurations;

internal sealed class AccountAiNoticeSettingConfiguration : IEntityTypeConfiguration<AccountAiNoticeSetting>
{
    public void Configure(EntityTypeBuilder<AccountAiNoticeSetting> builder)
    {
        builder.ToTable("AccountAiNoticeSettings");

        builder.HasKey(setting => setting.Id);

        builder.Property(setting => setting.AccountId)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.AccountIdMaxLength);

        builder.Property(setting => setting.LowCreditNoticeEnabled).IsRequired();
        builder.Property(setting => setting.LastChangedAt).IsRequired();

        // One row per account, which is the whole invariant: two rows would be two answers to "is this notice
        // on", and the reader would have to pick. Two requests racing to create the first row lose here rather
        // than both succeeding.
        builder.HasIndex(setting => setting.AccountId)
            .IsUnique()
            .HasDatabaseName("UX_AccountAiNoticeSettings_Account");
    }
}
