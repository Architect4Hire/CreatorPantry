using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class CreativeContextChannelConfiguration : IEntityTypeConfiguration<CreativeContextChannel>
{
    public void Configure(EntityTypeBuilder<CreativeContextChannel> builder)
    {
        builder.ToTable("CreativeContextChannels", table =>
        {
            table.HasCheckConstraint("CK_CreativeContextChannels_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");
            table.HasCheckConstraint("CK_CreativeContextChannels_SortOrder_NonNegative", "SortOrder >= 0");
        });

        builder.HasKey(channel => channel.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(channel => channel.Id).ValueGeneratedNever();

        builder.Property(channel => channel.ChannelKey).IsRequired().HasMaxLength(ContentPolicy.ChannelKeyMaxLength);
        builder.Property(channel => channel.SortOrder).IsRequired();

        // Reaches the workspace only through its context, so there is one cascade path into this table.
        builder.HasOne<CreativeContext>()
            .WithMany(context => context.Channels)
            .HasForeignKey(channel => new { channel.WorkspaceId, channel.CreativeContextId })
            .HasPrincipalKey(context => new { context.WorkspaceId, context.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(channel => new { channel.WorkspaceId, channel.CreativeContextId, channel.ChannelKey })
            .IsUnique()
            .HasDatabaseName("UX_CreativeContextChannels_Workspace_Context_Channel");

        builder.HasIndex(channel => new { channel.WorkspaceId, channel.CreativeContextId, channel.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_CreativeContextChannels_Workspace_Context_Order");
    }
}
