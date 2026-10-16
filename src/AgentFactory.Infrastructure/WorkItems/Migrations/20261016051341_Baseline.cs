using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// The baseline. This is the schema the hand-written ADO.NET store defined, column for
// column and index for index, so that a fresh database differs from the old one only in
// how it was made. `created_utc` and `asked_utc` are INTEGER tick counts because SQLite's
// provider refuses ORDER BY over a DateTimeOffset; every other timestamp is still readable
// text, and nothing was migrated — the map decided a fresh database rebuilt from GitHub is
// accepted, so `data/agent-factory.db` is left alone and this file is what every future
// migration is a change against (#56, #62).

namespace AgentFactory.Infrastructure.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    work_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: false),
                    feedback = table.Column<string>(type: "TEXT", nullable: false),
                    decided_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    applied_to = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => new { x.work_item_id, x.sequence });
                });

            migrationBuilder.CreateTable(
                name: "pending_rounds",
                columns: table => new
                {
                    work_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    round_number = table.Column<int>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", nullable: false),
                    asked_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    retry_after_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_rounds", x => x.work_item_id);
                });

            migrationBuilder.CreateTable(
                name: "round_results",
                columns: table => new
                {
                    work_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    round_number = table.Column<int>(type: "INTEGER", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    result_payload = table.Column<string>(type: "TEXT", nullable: true),
                    agent_note = table.Column<string>(type: "TEXT", nullable: true),
                    started_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    completed_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    failure = table.Column<string>(type: "TEXT", nullable: true),
                    log = table.Column<string>(type: "TEXT", nullable: true),
                    diff = table.Column<string>(type: "TEXT", nullable: true),
                    diff_base = table.Column<string>(type: "TEXT", nullable: true),
                    diff_tree = table.Column<string>(type: "TEXT", nullable: true),
                    diff_container_bounded = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    diff_unavailable = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_round_results", x => new { x.work_item_id, x.round_number });
                });

            migrationBuilder.CreateTable(
                name: "work_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    project = table.Column<string>(type: "TEXT", nullable: false),
                    repo_url = table.Column<string>(type: "TEXT", nullable: false),
                    issue_number = table.Column<int>(type: "INTEGER", nullable: false),
                    issue_title = table.Column<string>(type: "TEXT", nullable: false),
                    issue_body = table.Column<string>(type: "TEXT", nullable: false),
                    base_branch = table.Column<string>(type: "TEXT", nullable: false),
                    swimlane = table.Column<string>(type: "TEXT", nullable: false),
                    round_count = table.Column<int>(type: "INTEGER", nullable: false),
                    created_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    review_started_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    merge_attempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    merge_retry_after_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Unrouted"),
                    route_reason = table.Column<string>(type: "TEXT", nullable: true),
                    routing_issue = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_work_item",
                table: "decisions",
                columns: new[] { "work_item_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_source_issue",
                table: "work_items",
                columns: new[] { "repo_url", "issue_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropTable(
                name: "pending_rounds");

            migrationBuilder.DropTable(
                name: "round_results");

            migrationBuilder.DropTable(
                name: "work_items");
        }
    }
}
