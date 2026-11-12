using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArchiTech.Api.Data.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class EngagementOwnershipAndSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeploymentSyncMessage",
                table: "Engagements",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeploymentSyncStatus",
                table: "Engagements",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeploymentSyncedUtc",
                table: "Engagements",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExperienceJson",
                table: "Engagements",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerEmail",
                table: "Engagements",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Engagements",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Engagements_OwnerId",
                table: "Engagements",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Engagements_OwnerId",
                table: "Engagements");

            migrationBuilder.DropColumn(
                name: "DeploymentSyncMessage",
                table: "Engagements");

            migrationBuilder.DropColumn(
                name: "DeploymentSyncStatus",
                table: "Engagements");

            migrationBuilder.DropColumn(
                name: "DeploymentSyncedUtc",
                table: "Engagements");

            migrationBuilder.DropColumn(
                name: "ExperienceJson",
                table: "Engagements");

            migrationBuilder.DropColumn(
                name: "OwnerEmail",
                table: "Engagements");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Engagements");
        }
    }
}
