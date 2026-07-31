using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrimSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddSellOutDetailPerfIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SellOutDetails_Period_Incl",
                table: "SellOutDetails",
                column: "PeriodId")
                .Annotation("SqlServer:Include", new[] { "GLN", "BrickId", "Quantity", "AmountTL", "KurumName", "Il", "Ilce" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SellOutDetails_Period_Incl",
                table: "SellOutDetails");
        }
    }
}
