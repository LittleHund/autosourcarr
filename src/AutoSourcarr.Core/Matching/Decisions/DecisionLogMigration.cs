using AutoSourcarr.Core.Persistence;
using FluentMigrator;

namespace AutoSourcarr.Core.Matching.Decisions;

/// <summary>
/// Phase 8 "one decision log with actor": the single history of matching decisions (merges, splits, separates, manual
/// matches and groups, titles, policies, reviews, resets), automatic or manual, with who made them. A new table because
/// none of the 0.4.4 history tables fits: match_history, duplicate_review_history, automatic_merge_events and
/// duplicate_split_history each have a fixed shape for one kind of change and no actor; matching_review_actions and
/// policy_preview_history belong to the review workbench and the policy simulator (their own undo and preview state).
/// Those tables keep being written by their owners; this one is the common, filterable log on top.
/// </summary>
[Migration(2026110180, "catalog: decision log")]
[Tags(SchemaMigrator.CatalogTag)]
public sealed class CatalogDecisionLog : ForwardOnlyMigration
{
    public override void Up()
    {
        Create.Table(DecisionLog.Table)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("at").AsString().NotNullable()
            .WithColumn("kind").AsString().NotNullable()
            .WithColumn("actor_kind").AsString().NotNullable()
            .WithColumn("actor_name").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("entries_json").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("records_json").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("reason").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("before_json").AsString().Nullable()
            .WithColumn("after_json").AsString().Nullable()
            .WithColumn("undo_of").AsString().Nullable();
        Create.Index("idx_decision_log_kind").OnTable(DecisionLog.Table).OnColumn("kind").Ascending();
        Create.Index("idx_decision_log_actor").OnTable(DecisionLog.Table).OnColumn("actor_kind").Ascending();
    }
}
