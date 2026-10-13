using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Aggregates.Agents;
using SupportDesk.Domain.Aggregates.Tickets;

namespace SupportDesk.Infrastructure.Data.Configurations;

public sealed class TicketEscalationConfiguration : IEntityTypeConfiguration<TicketEscalation>
{
    public void Configure(EntityTypeBuilder<TicketEscalation> builder)
    {
        builder.ToTable("TicketEscalations");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Reason).HasMaxLength(500).IsRequired();
        builder.Property(row => row.EscalatedBy).HasMaxLength(100).IsRequired();
        builder.Property(row => row.FromDueAtUtc).HasColumnType("datetime2(3)");
        builder.Property(row => row.ToDueAtUtc).HasColumnType("datetime2(3)");
        builder.Property(row => row.EscalatedAtUtc).HasColumnType("datetime2(3)");
        builder.HasOne<Ticket>().WithMany().HasForeignKey(row => row.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Agent>().WithMany().HasForeignKey(row => row.FromAgentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Agent>().WithMany().HasForeignKey(row => row.ToAgentId).OnDelete(DeleteBehavior.Restrict);
        // Ticket lookup followed by newest-first history; ID breaks timestamp ties.
        builder.HasIndex(row => new { row.TicketId, row.EscalatedAtUtc, row.Id }).IsDescending(false, true, true);
    }
}
