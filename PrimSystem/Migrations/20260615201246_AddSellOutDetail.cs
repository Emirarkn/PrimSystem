using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrimSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddSellOutDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SellOutDetails",
                columns: table => new
                {
                    SellOutDetailId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BrickId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    PeriodId = table.Column<int>(type: "int", nullable: false),
                    GLN = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    KurumName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Il = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Ilce = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AmountTL = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MFTL = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SellOutDetails", x => x.SellOutDetailId);
                    table.ForeignKey(
                        name: "FK_SellOutDetails_Bricks_BrickId",
                        column: x => x.BrickId,
                        principalTable: "Bricks",
                        principalColumn: "BrickId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SellOutDetails_Periods_PeriodId",
                        column: x => x.PeriodId,
                        principalTable: "Periods",
                        principalColumn: "PeriodId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SellOutDetails_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ProductId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SellOutDetails_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SellOutDetails_BrickId",
                table: "SellOutDetails",
                column: "BrickId");

            migrationBuilder.CreateIndex(
                name: "IX_SellOutDetails_GLN",
                table: "SellOutDetails",
                column: "GLN");

            migrationBuilder.CreateIndex(
                name: "IX_SellOutDetails_ImportedAt",
                table: "SellOutDetails",
                column: "ImportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SellOutDetails_PeriodId_BrickId_ProductId",
                table: "SellOutDetails",
                columns: new[] { "PeriodId", "BrickId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_SellOutDetails_ProductId",
                table: "SellOutDetails",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SellOutDetails_UserId",
                table: "SellOutDetails",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SellOutDetails");
        }
    }
}
