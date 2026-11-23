using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicPos.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueMetronId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MetronIssueId",
                table: "Issues",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetronIssueId",
                table: "Issues");
        }
    }
}
