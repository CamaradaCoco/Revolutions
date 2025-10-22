using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Demo.Migrations
{
    /// <inheritdoc />
    public partial class AddWikidataFiels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "EndDate",
                table: "Revolutions",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AddColumn<int>(
                name: "EstimatedDeaths",
                table: "Revolutions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sources",
                table: "Revolutions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Revolutions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WikidataId",
                table: "Revolutions",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EstimatedDeaths",
                table: "Revolutions");

            migrationBuilder.DropColumn(
                name: "Sources",
                table: "Revolutions");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Revolutions");

            migrationBuilder.DropColumn(
                name: "WikidataId",
                table: "Revolutions");

            migrationBuilder.AlterColumn<DateTime>(
                name: "EndDate",
                table: "Revolutions",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);
        }
    }
}
