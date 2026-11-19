using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lab.Modules.Shared.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chl_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeedId = table.Column<string>(type: "TEXT", nullable: false),
                    SeedVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Difficulty = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    VariationJson = table.Column<string>(type: "TEXT", nullable: false),
                    PersonalizationJson = table.Column<string>(type: "TEXT", nullable: true),
                    SpecJson = table.Column<string>(type: "TEXT", nullable: false),
                    PublicJson = table.Column<string>(type: "TEXT", nullable: false),
                    MasterSeed = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferenceCostUsd = table.Column<double>(type: "REAL", nullable: true),
                    ReferenceSceneJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chl_challenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dsg_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DraftSceneJson = table.Column<string>(type: "TEXT", nullable: false),
                    DraftVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    FlowsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HintsUsedJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeadlineAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmissionEvaluationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubmitIdempotencyKey = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dsg_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dsg_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SceneJson = table.Column<string>(type: "TEXT", nullable: false),
                    FlowsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IrJson = table.Column<string>(type: "TEXT", nullable: false),
                    IrHash = table.Column<string>(type: "TEXT", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dsg_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "eva_evaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    Percent = table.Column<double>(type: "REAL", nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    FinalScore = table.Column<int>(type: "INTEGER", nullable: true),
                    Rank = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eva_evaluations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "eva_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Dimension = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    ElementRefsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Confirmed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eva_findings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "eva_scenario_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScenarioId = table.Column<string>(type: "TEXT", nullable: false),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: false),
                    Series = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eva_scenario_results", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gam_challenge_stats",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeedId = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    BestScore = table.Column<int>(type: "INTEGER", nullable: false),
                    BestRank = table.Column<string>(type: "TEXT", nullable: false),
                    BestAttemptXp = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstScore = table.Column<int>(type: "INTEGER", nullable: false),
                    Evaluations = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gam_challenge_stats", x => new { x.ProfileId, x.ChallengeId });
                });

            migrationBuilder.CreateTable(
                name: "gam_competency_ratings",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Competency = table.Column<string>(type: "TEXT", nullable: false),
                    Rating = table.Column<double>(type: "REAL", nullable: false),
                    Samples = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gam_competency_ratings", x => new { x.ProfileId, x.Competency });
                });

            migrationBuilder.CreateTable(
                name: "gam_user_achievements",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    UnlockedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceEvaluationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gam_user_achievements", x => new { x.ProfileId, x.Code });
                });

            migrationBuilder.CreateTable(
                name: "gam_user_progress",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TotalXp = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    StreakDays = table.Column<int>(type: "INTEGER", nullable: false),
                    StreakFreezes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastActivityDate = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gam_user_progress", x => x.ProfileId);
                });

            migrationBuilder.CreateTable(
                name: "gam_xp_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gam_xp_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "profile_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Locale = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultProvider = table.Column<string>(type: "TEXT", nullable: false),
                    PreferencesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sys_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    RunAfter = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sys_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sys_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sys_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chl_challenges_ProfileId_CreatedAt",
                table: "chl_challenges",
                columns: new[] { "ProfileId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_dsg_attempts_ChallengeId_Number",
                table: "dsg_attempts",
                columns: new[] { "ChallengeId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dsg_snapshots_AttemptId_CreatedAt",
                table: "dsg_snapshots",
                columns: new[] { "AttemptId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_eva_evaluations_AttemptId_CreatedAt",
                table: "eva_evaluations",
                columns: new[] { "AttemptId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_eva_evaluations_ChallengeId",
                table: "eva_evaluations",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_eva_findings_EvaluationId",
                table: "eva_findings",
                column: "EvaluationId");

            migrationBuilder.CreateIndex(
                name: "IX_eva_scenario_results_EvaluationId_ScenarioId",
                table: "eva_scenario_results",
                columns: new[] { "EvaluationId", "ScenarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gam_xp_entries_ProfileId_Reason_SourceId",
                table: "gam_xp_entries",
                columns: new[] { "ProfileId", "Reason", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sys_jobs_Status_RunAfter",
                table: "sys_jobs",
                columns: new[] { "Status", "RunAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_sys_outbox_ProcessedAt",
                table: "sys_outbox",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chl_challenges");

            migrationBuilder.DropTable(
                name: "dsg_attempts");

            migrationBuilder.DropTable(
                name: "dsg_snapshots");

            migrationBuilder.DropTable(
                name: "eva_evaluations");

            migrationBuilder.DropTable(
                name: "eva_findings");

            migrationBuilder.DropTable(
                name: "eva_scenario_results");

            migrationBuilder.DropTable(
                name: "gam_challenge_stats");

            migrationBuilder.DropTable(
                name: "gam_competency_ratings");

            migrationBuilder.DropTable(
                name: "gam_user_achievements");

            migrationBuilder.DropTable(
                name: "gam_user_progress");

            migrationBuilder.DropTable(
                name: "gam_xp_entries");

            migrationBuilder.DropTable(
                name: "profile_profiles");

            migrationBuilder.DropTable(
                name: "sys_jobs");

            migrationBuilder.DropTable(
                name: "sys_outbox");
        }
    }
}
