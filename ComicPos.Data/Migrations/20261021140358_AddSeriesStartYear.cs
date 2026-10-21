using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicPos.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesStartYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StartYear",
                table: "AllSeries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartYear",
                table: "AllSeries");
        }
    }
}
