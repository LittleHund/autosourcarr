using FluentMigrator;

namespace AutoSourcarr.Core.Persistence.Migrations;

/// <summary>Phase 8D: results of the scaled mixed-entry check, one row per entry with two or more source records, so
/// the review list is a paged read instead of a whole-catalogue scan. Rebuilt by the check; holds no user decision
/// (reviews stay in mixed_entry_reviews, keyed by evidence fingerprint).</summary>
[Migration(2026110184, "catalog: mixed-entry check findings")]
[Tags(SchemaMigrator.CatalogTag)]
public sealed class MixedEntryFindingsTable : ForwardOnlyMigration
{
    public override void Up()
    {
        Create.Table("mixed_entry_findings")
            .WithColumn("entry_id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("fingerprint").AsString().NotNullable()
            .WithColumn("state").AsString().NotNullable()
            .WithColumn("evidence_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("record_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("partial").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("preferred_title").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("sort_title").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("search_text").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("evidence_json").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("checked_at").AsString().NotNullable();
        Create.Index("idx_mixed_entry_findings_state").OnTable("mixed_entry_findings")
            .OnColumn("state").Ascending().OnColumn("fingerprint").Ascending();
    }
}
