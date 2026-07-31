using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrimSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddNameAlias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NameAliases",
                columns: table => new
                {
                    NameAliasId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AliasType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AliasKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NameAliases", x => x.NameAliasId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NameAliases_AliasType_AliasKey",
                table: "NameAliases",
                columns: new[] { "AliasType", "AliasKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NameAliases");
        }
    }
}
