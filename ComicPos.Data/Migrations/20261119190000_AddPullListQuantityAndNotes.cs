using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicPos.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPullListQuantityAndNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "PullListEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "PullListEntries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "PullListEntries");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "PullListEntries");
        }
    }
}
