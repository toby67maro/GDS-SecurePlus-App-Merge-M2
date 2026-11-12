using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class LibrarySourceInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StandardsReferenceSources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Area = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    UsageBoundary = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ImportedRecordCount = table.Column<int>(type: "int", nullable: true),
                    RecordUnit = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StandardsReferenceSources", x => new { x.CatalogVersion, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_StandardsReferenceSources_CatalogVersion_Area_Status",
                table: "StandardsReferenceSources",
                columns: new[] { "CatalogVersion", "Area", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StandardsReferenceSources_CatalogVersion_Name",
                table: "StandardsReferenceSources",
                columns: new[] { "CatalogVersion", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StandardsReferenceSources");
        }
    }
}
