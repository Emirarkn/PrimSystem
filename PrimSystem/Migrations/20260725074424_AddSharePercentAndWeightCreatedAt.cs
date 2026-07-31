using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrimSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddSharePercentAndWeightCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SharePercent",
                table: "UserBricks",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "ScorecardWeights",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharePercent",
                table: "UserBricks");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ScorecardWeights");
        }
    }
}
