using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class PersistDeploymentSetupChecklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeploymentProfileSetupSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeploymentProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepKey = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    IsComplete = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentProfileSetupSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentProfileSetupSteps_DeploymentProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "DeploymentProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentProfileSetupSteps_DeploymentProfileId_StepKey",
                table: "DeploymentProfileSetupSteps",
                columns: new[] { "DeploymentProfileId", "StepKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentProfileSetupSteps");
        }
    }
}
