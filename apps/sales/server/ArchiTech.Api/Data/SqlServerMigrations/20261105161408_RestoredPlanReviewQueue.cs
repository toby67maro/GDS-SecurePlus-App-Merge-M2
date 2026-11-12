using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class RestoredPlanReviewQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeploymentChangePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    RequestedById = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedById = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReviewedBy = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewDecision = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentChangePlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentChangePlans_AssessmentRuns_AssessmentRunId",
                        column: x => x.AssessmentRunId,
                        principalTable: "AssessmentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentChangePlans_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentChangePlanItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeploymentChangePlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ControlId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ControlName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AssessmentStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentChangePlanItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentChangePlanItems_DeploymentChangePlans_DeploymentChangePlanId",
                        column: x => x.DeploymentChangePlanId,
                        principalTable: "DeploymentChangePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentChangePlanItems_DeploymentChangePlanId_ControlId",
                table: "DeploymentChangePlanItems",
                columns: new[] { "DeploymentChangePlanId", "ControlId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentChangePlanItems_DeploymentChangePlanId_Sequence",
                table: "DeploymentChangePlanItems",
                columns: new[] { "DeploymentChangePlanId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentChangePlans_AssessmentRunId",
                table: "DeploymentChangePlans",
                column: "AssessmentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentChangePlans_CustomerId_CreatedUtc",
                table: "DeploymentChangePlans",
                columns: new[] { "CustomerId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentChangePlans_Status",
                table: "DeploymentChangePlans",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentChangePlanItems");

            migrationBuilder.DropTable(
                name: "DeploymentChangePlans");
        }
    }
}
