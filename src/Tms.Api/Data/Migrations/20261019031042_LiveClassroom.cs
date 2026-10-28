using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tms.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LiveClassroom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "JoinedAt",
                table: "SessionAttendance",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeftAt",
                table: "SessionAttendance",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "ClassSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndedAt",
                table: "ClassSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingRoom",
                table: "ClassSessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "ClassSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "ClassSessions",
                type: "text",
                nullable: false,
                defaultValue: "Scheduled"); // existing sessions have not been run in the classroom
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinedAt",
                table: "SessionAttendance");

            migrationBuilder.DropColumn(
                name: "LeftAt",
                table: "SessionAttendance");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "MeetingRoom",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "ClassSessions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ClassSessions");
        }
    }
}
