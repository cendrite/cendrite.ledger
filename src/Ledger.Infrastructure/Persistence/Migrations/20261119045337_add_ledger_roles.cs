using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_ledger_roles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "invite_keys_added",
                table: "ledger_event");

            migrationBuilder.AddColumn<bool>(
                name: "is_invite",
                table: "ledger_write_key",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "invites_added",
                table: "ledger_event",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "roles_changed",
                table: "ledger_event",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_invite",
                table: "ledger_write_key");

            migrationBuilder.DropColumn(
                name: "invites_added",
                table: "ledger_event");

            migrationBuilder.DropColumn(
                name: "roles_changed",
                table: "ledger_event");

            migrationBuilder.AddColumn<List<byte[]>>(
                name: "invite_keys_added",
                table: "ledger_event",
                type: "bytea[]",
                nullable: false,
                defaultValueSql: "'{}'::bytea[]");
        }
    }
}
