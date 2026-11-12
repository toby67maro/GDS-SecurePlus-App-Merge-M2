using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class ProfileDiscoveryResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileDecisionResponses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeploymentProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DecisionId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Answer = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileDecisionResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileDecisionResponses_DeploymentProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "DeploymentProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileDecisionResponses_SecurePlusDecisions_CatalogVersion_DecisionId",
                        columns: x => new { x.CatalogVersion, x.DecisionId },
                        principalTable: "SecurePlusDecisions",
                        principalColumns: new[] { "CatalogVersion", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileDecisionResponses_CatalogVersion_DecisionId",
                table: "ProfileDecisionResponses",
                columns: new[] { "CatalogVersion", "DecisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileDecisionResponses_DeploymentProfileId_CatalogVersion_DecisionId",
                table: "ProfileDecisionResponses",
                columns: new[] { "DeploymentProfileId", "CatalogVersion", "DecisionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileDecisionResponses");
        }
    }
}
