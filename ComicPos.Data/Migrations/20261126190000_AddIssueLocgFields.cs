using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicPos.Data.Migrations
{
    /// <inheritdoc />
    // Abandoned same-session, before ever being applied anywhere - the
    // user redirected the whole-catalog variant-backfill approach away
    // from LOCG (see AutoFetchService.BackfillCoverVariantsFromPrhBookPages
    // instead) before this ever got wired up or run. Left as a genuine
    // no-op, rather than deleted, because this session's remote shell
    // can't delete files in a connected folder without an extra
    // permission prompt - not worth spending for a migration that never
    // touched a single real database.
    public partial class AddIssueLocgFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
