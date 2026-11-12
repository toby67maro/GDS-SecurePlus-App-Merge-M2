using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class InitialSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WbsTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Workstream = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Deliverable = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RuleKey = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    MinHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    MaxHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    DurationMin = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DurationMax = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SupportHandoff = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WbsTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Engagements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Salesperson = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    MeetingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Users = table.Column<int>(type: "int", nullable: true),
                    Endpoints = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OneTimeHoursMin = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    OneTimeHoursMax = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    MonthlyHoursMin = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    MonthlyHoursMax = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    PlannerHandoffStatus = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    PlannerPlanId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PlannerHandoffMessage = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlannerHandedOffUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Engagements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Engagements_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EngagementAnswers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EngagementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngagementAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngagementAnswers_Engagements_EngagementId",
                        column: x => x.EngagementId,
                        principalTable: "Engagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EngagementTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EngagementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WbsTaskId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MinHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    MaxHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngagementTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngagementTasks_Engagements_EngagementId",
                        column: x => x.EngagementId,
                        principalTable: "Engagements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EngagementTasks_WbsTasks_WbsTaskId",
                        column: x => x.WbsTaskId,
                        principalTable: "WbsTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_NormalizedName",
                table: "Customers",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngagementAnswers_EngagementId_FieldKey_Value",
                table: "EngagementAnswers",
                columns: new[] { "EngagementId", "FieldKey", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Engagements_CreatedUtc",
                table: "Engagements",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Engagements_CustomerId",
                table: "Engagements",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_EngagementTasks_EngagementId_WbsTaskId",
                table: "EngagementTasks",
                columns: new[] { "EngagementId", "WbsTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngagementTasks_WbsTaskId",
                table: "EngagementTasks",
                column: "WbsTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EngagementAnswers");

            migrationBuilder.DropTable(
                name: "EngagementTasks");

            migrationBuilder.DropTable(
                name: "Engagements");

            migrationBuilder.DropTable(
                name: "WbsTasks");

            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
