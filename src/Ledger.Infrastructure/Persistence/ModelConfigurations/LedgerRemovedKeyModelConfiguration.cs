using Ledger.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ledger.Infrastructure.Persistence.ModelConfigurations;

public sealed class LedgerRemovedKeyModelConfiguration : BaseModelConfiguration<LedgerRemovedKey>
{
    public override void Configure(EntityTypeBuilder<LedgerRemovedKey> builder)
    {
        builder.ToTable("ledger_removed_key");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.StreamId)
            .HasColumnName("stream_id")
            .IsRequired();

        builder.Property(x => x.PublicKey)
            .HasColumnName("public_key")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.RemovedAtVersion)
            .HasColumnName("removed_at_version")
            .IsRequired();

        builder.HasIndex(x => new { x.StreamId, x.PublicKey })
            .IsUnique();

        base.Configure(builder);
    }
}
