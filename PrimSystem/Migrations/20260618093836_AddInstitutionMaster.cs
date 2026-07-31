using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrimSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddInstitutionMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "GLN",
                table: "Institutions",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BrickOverride",
                table: "Institutions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Ilce",
                table: "Institutions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "Institutions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Institutions_GLN",
                table: "Institutions",
                column: "GLN",
                unique: true,
                filter: "[GLN] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Institutions_GLN",
                table: "Institutions");

            migrationBuilder.DropColumn(
                name: "BrickOverride",
                table: "Institutions");

            migrationBuilder.DropColumn(
                name: "Ilce",
                table: "Institutions");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Institutions");

            migrationBuilder.AlterColumn<string>(
                name: "GLN",
                table: "Institutions",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);
        }
    }
}
