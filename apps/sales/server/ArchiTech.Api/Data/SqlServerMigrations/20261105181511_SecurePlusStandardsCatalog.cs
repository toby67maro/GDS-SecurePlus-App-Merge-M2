using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class SecurePlusStandardsCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurePlusBaselineSettings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BaselineReference = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Workstream = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TaskNumber = table.Column<int>(type: "int", nullable: false),
                    TaskName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    GroupAbbreviation = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    GroupItemNumber = table.Column<int>(type: "int", nullable: false),
                    Control = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    SettingCategory = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SettingName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    TargetAction = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ReviewPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DeploymentMethod = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ValidationMethod = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    EvidenceRequired = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AppliesWhen = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LicenseRequirement = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ImplementationMode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PilotGroup = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Exclusions = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ExceptionCriteria = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RollbackMethod = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ReviewCadence = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    SupportOwner = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    EngineeringNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurePlusBaselineSettings", x => new { x.CatalogVersion, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "SecurePlusDecisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BaselineReferences = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Question = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AnswerType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SuggestedAnswers = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ConfigurationImpact = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    DecisionOwner = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    RequiredParticipants = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RequiredBy = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    EvidenceRequired = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DefaultIfUndecided = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    EngineeringNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurePlusDecisions", x => new { x.CatalogVersion, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "SecurePlusWorkItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PhaseReference = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Workstream = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TaskNumber = table.Column<int>(type: "int", nullable: false),
                    TaskName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    SubTaskNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SubTaskName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    GroupAbbreviation = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    GroupItemNumber = table.Column<int>(type: "int", nullable: false),
                    Control = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ControlDescription = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Deliverable = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Dependency = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AppliesWhen = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LicenseRequirement = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    EstimatedHoursMin = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    EstimatedHoursMax = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    DurationDaysMin = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    DurationDaysMax = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    Parallelizable = table.Column<bool>(type: "bit", nullable: false),
                    SupportHandoff = table.Column<bool>(type: "bit", nullable: false),
                    ComplexityDriver = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LeadershipNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurePlusWorkItems", x => new { x.CatalogVersion, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "TargetModelTables",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Schema = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EstimatedRows = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PrimarySources = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    TemporalHistory = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetModelTables", x => new { x.CatalogVersion, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "EngagementDecisionResponses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EngagementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_EngagementDecisionResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngagementDecisionResponses_Engagements_EngagementId",
                        column: x => x.EngagementId,
                        principalTable: "Engagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EngagementDecisionResponses_SecurePlusDecisions_CatalogVersion_DecisionId",
                        columns: x => new { x.CatalogVersion, x.DecisionId },
                        principalTable: "SecurePlusDecisions",
                        principalColumns: new[] { "CatalogVersion", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TargetModelColumns",
                columns: table => new
                {
                    TableId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CatalogVersion = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ColumnOrdinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsPrimaryKey = table.Column<bool>(type: "bit", nullable: false),
                    IsForeignKey = table.Column<bool>(type: "bit", nullable: false),
                    ForeignKeyTarget = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsUniqueKeyPart = table.Column<bool>(type: "bit", nullable: false),
                    IsIndexed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetModelColumns", x => new { x.CatalogVersion, x.TableId, x.ColumnOrdinal });
                    table.ForeignKey(
                        name: "FK_TargetModelColumns_TargetModelTables_CatalogVersion_TableId",
                        columns: x => new { x.CatalogVersion, x.TableId },
                        principalTable: "TargetModelTables",
                        principalColumns: new[] { "CatalogVersion", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EngagementDecisionResponses_CatalogVersion_DecisionId",
                table: "EngagementDecisionResponses",
                columns: new[] { "CatalogVersion", "DecisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EngagementDecisionResponses_EngagementId_CatalogVersion_DecisionId",
                table: "EngagementDecisionResponses",
                columns: new[] { "EngagementId", "CatalogVersion", "DecisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurePlusBaselineSettings_BaselineReference_CatalogVersion_Control_SettingCategory_SettingName",
                table: "SecurePlusBaselineSettings",
                columns: new[] { "BaselineReference", "CatalogVersion", "Control", "SettingCategory", "SettingName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurePlusBaselineSettings_CatalogVersion_Phase_ImplementationMode",
                table: "SecurePlusBaselineSettings",
                columns: new[] { "CatalogVersion", "Phase", "ImplementationMode" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurePlusDecisions_CatalogVersion_Phase_Category",
                table: "SecurePlusDecisions",
                columns: new[] { "CatalogVersion", "Phase", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurePlusWorkItems_CatalogVersion_Phase_Workstream",
                table: "SecurePlusWorkItems",
                columns: new[] { "CatalogVersion", "Phase", "Workstream" });

            migrationBuilder.CreateIndex(
                name: "IX_TargetModelColumns_CatalogVersion_TableId_Name",
                table: "TargetModelColumns",
                columns: new[] { "CatalogVersion", "TableId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetModelTables_CatalogVersion_Schema_Name",
                table: "TargetModelTables",
                columns: new[] { "CatalogVersion", "Schema", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EngagementDecisionResponses");

            migrationBuilder.DropTable(
                name: "SecurePlusBaselineSettings");

            migrationBuilder.DropTable(
                name: "SecurePlusWorkItems");

            migrationBuilder.DropTable(
                name: "TargetModelColumns");

            migrationBuilder.DropTable(
                name: "SecurePlusDecisions");

            migrationBuilder.DropTable(
                name: "TargetModelTables");
        }
    }
}
