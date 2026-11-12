using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class RestoredAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssessmentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EngagementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Assessor = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsComplete = table.Column<bool>(type: "bit", nullable: false),
                    OverallScore = table.Column<double>(type: "float", nullable: false),
                    OverallPercentage = table.Column<double>(type: "float", nullable: false),
                    TotalControls = table.Column<int>(type: "int", nullable: false),
                    ImplementedControls = table.Column<int>(type: "int", nullable: false),
                    PartialControls = table.Column<int>(type: "int", nullable: false),
                    NotImplementedControls = table.Column<int>(type: "int", nullable: false),
                    NotAssessedControls = table.Column<int>(type: "int", nullable: false),
                    ExceptionControls = table.Column<int>(type: "int", nullable: false),
                    NotApplicableControls = table.Column<int>(type: "int", nullable: false),
                    ReadinessRating = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    CalculatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentRuns_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssessmentRuns_Engagements_EngagementId",
                        column: x => x.EngagementId,
                        principalTable: "Engagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssessmentRecommendations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssessmentRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentRecommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentRecommendations_AssessmentRuns_AssessmentRunId",
                        column: x => x.AssessmentRunId,
                        principalTable: "AssessmentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssessmentResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssessmentRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ControlId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Assessor = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ReviewedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExceptionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentResults_AssessmentRuns_AssessmentRunId",
                        column: x => x.AssessmentRunId,
                        principalTable: "AssessmentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssessmentScoreBreakdowns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssessmentRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BreakdownType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Percentage = table.Column<double>(type: "float", nullable: false),
                    TotalPoints = table.Column<int>(type: "int", nullable: false),
                    EarnedPoints = table.Column<int>(type: "int", nullable: false),
                    GapCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentScoreBreakdowns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentScoreBreakdowns_AssessmentRuns_AssessmentRunId",
                        column: x => x.AssessmentRunId,
                        principalTable: "AssessmentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentRecommendations_AssessmentRunId_Sequence",
                table: "AssessmentRecommendations",
                columns: new[] { "AssessmentRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentResults_AssessmentRunId_ControlId",
                table: "AssessmentResults",
                columns: new[] { "AssessmentRunId", "ControlId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentRuns_CustomerId_StartedUtc",
                table: "AssessmentRuns",
                columns: new[] { "CustomerId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentRuns_EngagementId",
                table: "AssessmentRuns",
                column: "EngagementId");

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentScoreBreakdowns_AssessmentRunId_BreakdownType_Label",
                table: "AssessmentScoreBreakdowns",
                columns: new[] { "AssessmentRunId", "BreakdownType", "Label" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssessmentRecommendations");

            migrationBuilder.DropTable(
                name: "AssessmentResults");

            migrationBuilder.DropTable(
                name: "AssessmentScoreBreakdowns");

            migrationBuilder.DropTable(
                name: "AssessmentRuns");
        }
    }
}
