using AutoSourcarr.Core.Persistence;
using FluentMigrator;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// Phase 10 CBZ library (CHAP-7). New tables: 0.4.x had no local library (membership and downloads lived in main
/// Suwayomi), so nothing existing fits. library_tracking (monitoring, observations) and due_work (monitoring clocks)
/// are reused as they are.
/// <list type="bullet">
/// <item>library_series: membership of a work in AutoSourcarr's own library and its fixed series folder.</item>
/// <item>download_queue: chapters waiting to download, in queue order, with the serving source (one lane per source).</item>
/// <item>library_downloads: chapters on disk (relative file, page count, size, source).</item>
/// <item>download_history: downloaded / failed / removed events for the activity history.</item>
/// <item>library_state: small switches (downloader paused).</item>
/// </list>
/// </summary>
[Migration(2026110192, "catalog: CBZ library, download queue and history")]
[Tags(SchemaMigrator.CatalogTag)]
public sealed class CatalogLibraryDownloads : ForwardOnlyMigration
{
    public override void Up()
    {
        Create.Table("library_series")
            .WithColumn("entry_id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("in_library").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("series_name").AsString().NotNullable()
            .WithColumn("folder").AsString().NotNullable()
            .WithColumn("folder_key").AsString().NotNullable()
            .WithColumn("added_at").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("updated_at").AsString().NotNullable();
        Create.Index("idx_library_series_folder").OnTable("library_series").OnColumn("folder_key").Unique();

        Create.Table("download_queue")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("entry_id").AsInt64().NotNullable()
            .WithColumn("chapter_id").AsInt64().NotNullable()
            .WithColumn("source_id").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("source_name").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("position").AsInt64().NotNullable()
            .WithColumn("state").AsString().NotNullable().WithDefaultValue("queued")
            .WithColumn("tries").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("progress").AsDouble().NotNullable().WithDefaultValue(0)
            .WithColumn("error").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("chapter_json").AsString().NotNullable().WithDefaultValue("{}")
            .WithColumn("queued_at").AsString().NotNullable()
            .WithColumn("updated_at").AsString().NotNullable();
        Create.Index("idx_download_queue_chapter").OnTable("download_queue").OnColumn("chapter_id").Unique();
        Create.Index("idx_download_queue_source").OnTable("download_queue")
            .OnColumn("source_id").Ascending().OnColumn("state").Ascending().OnColumn("position").Ascending();

        Create.Table("library_downloads")
            .WithColumn("chapter_id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("entry_id").AsInt64().NotNullable()
            .WithColumn("file").AsString().NotNullable()
            .WithColumn("pages").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("candidate_id").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("source_id").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("downloaded_at").AsString().NotNullable();
        Create.Index("idx_library_downloads_entry").OnTable("library_downloads").OnColumn("entry_id").Ascending();

        Create.Table("download_history")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("entry_id").AsInt64().NotNullable()
            .WithColumn("chapter_id").AsInt64().NotNullable()
            .WithColumn("event").AsString().NotNullable()
            .WithColumn("detail").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("created_at").AsString().NotNullable();
        Create.Index("idx_download_history_created").OnTable("download_history").OnColumn("created_at").Ascending();

        Create.Table("library_state")
            .WithColumn("key").AsString().NotNullable().PrimaryKey()
            .WithColumn("value").AsString().NotNullable();
    }
}
