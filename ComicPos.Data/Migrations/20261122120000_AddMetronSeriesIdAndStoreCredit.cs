using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ComicPos.Data;

#nullable disable

namespace ComicPos.Data.Migrations
{
    // Hand-written like every migration in this project since the
    // AddComicVineFields one - there is no dotnet/dotnet-ef CLI available
    // in this environment to scaffold one, so the [DbContext]/[Migration]
    // attributes that Database.Migrate() needs to discover and order this
    // migration are placed directly on this class instead of in a
    // separate *.Designer.cs file, and ComicDbContextModelSnapshot.cs is
    // intentionally left as-is. Migrate() only ever calls Up()/Down() on
    // the migration classes it finds via these attributes - it never
    // reads the Designer.cs "target model" snapshot at runtime (that's
    // purely a `dotnet ef migrations add` design-time diffing aid, which
    // isn't available here to regenerate correctly anyway) - so this
    // doesn't change what actually happens when the app starts and
    // applies pending migrations.
    [DbContext(typeof(ComicDbContext))]
    [Migration("20261122120000_AddMetronSeriesIdAndStoreCredit")]
    public partial class AddMetronSeriesIdAndStoreCredit : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Metron's own numeric volume id - see Series.MetronSeriesId.
            migrationBuilder.AddColumn<int>(
                name: "MetronSeriesId",
                table: "AllSeries",
                type: "INTEGER",
                nullable: true);

            // See Sale.StoreCreditUsed.
            migrationBuilder.AddColumn<decimal>(
                name: "StoreCreditUsed",
                table: "Sales",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "StoreCreditTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    SaleId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreCreditTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreCreditTransactions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoreCreditTransactions_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoreCreditTransactions_CustomerId",
                table: "StoreCreditTransactions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreCreditTransactions_SaleId",
                table: "StoreCreditTransactions",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_AllSeries_MetronSeriesId",
                table: "AllSeries",
                column: "MetronSeriesId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "StoreCreditTransactions");
            migrationBuilder.DropColumn(name: "StoreCreditUsed", table: "Sales");
            migrationBuilder.DropIndex(name: "IX_AllSeries_MetronSeriesId", table: "AllSeries");
            migrationBuilder.DropColumn(name: "MetronSeriesId", table: "AllSeries");
        }
    }
}
