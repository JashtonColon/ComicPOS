using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicPos.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AllSeries_MetronSeriesId",
                table: "AllSeries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AllSeries_MetronSeriesId",
                table: "AllSeries",
                column: "MetronSeriesId");
        }
    }
}
