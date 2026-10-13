using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportDesk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketEscalationsAndSlaWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An earlier local trial used this same schema. Preserve it when upgrading that DB.
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'Tickets', N'SlaWindowMilliseconds') IS NULL
                BEGIN
                    ALTER TABLE [Tickets] ADD [SlaWindowMilliseconds] float NOT NULL DEFAULT 0;
                    EXEC(N'UPDATE [Tickets]
                        SET [SlaWindowMilliseconds] = DATEDIFF_BIG(MILLISECOND, [CreatedAtUtc], [DueAtUtc])
                        WHERE [DueAtUtc] IS NOT NULL;');
                END;

                IF OBJECT_ID(N'TicketEscalations', N'U') IS NULL
                BEGIN
                    CREATE TABLE [TicketEscalations] (
                        [Id] int IDENTITY NOT NULL,
                        [TicketId] int NOT NULL,
                        [FromPriority] int NOT NULL, [ToPriority] int NOT NULL,
                        [FromAgentId] int NULL, [ToAgentId] int NULL,
                        [FromDueAtUtc] datetime2(3) NULL, [ToDueAtUtc] datetime2(3) NULL,
                        [Reason] nvarchar(500) NOT NULL,
                        [EscalatedBy] nvarchar(100) NOT NULL,
                        [EscalatedAtUtc] datetime2(3) NOT NULL,
                        CONSTRAINT [PK_TicketEscalations] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_TicketEscalations_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([Id]),
                        CONSTRAINT [FK_TicketEscalations_Agents_FromAgentId] FOREIGN KEY ([FromAgentId]) REFERENCES [Agents] ([Id]),
                        CONSTRAINT [FK_TicketEscalations_Agents_ToAgentId] FOREIGN KEY ([ToAgentId]) REFERENCES [Agents] ([Id])
                    );
                    CREATE INDEX [IX_TicketEscalations_FromAgentId] ON [TicketEscalations] ([FromAgentId]);
                    CREATE INDEX [IX_TicketEscalations_ToAgentId] ON [TicketEscalations] ([ToAgentId]);
                    CREATE INDEX [IX_TicketEscalations_TicketId_EscalatedAtUtc_Id]
                        ON [TicketEscalations] ([TicketId], [EscalatedAtUtc] DESC, [Id] DESC);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketEscalations");

            migrationBuilder.DropColumn(
                name: "SlaWindowMilliseconds",
                table: "Tickets");
        }
    }
}
