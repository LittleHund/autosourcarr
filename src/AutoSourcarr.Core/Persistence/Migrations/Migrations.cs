using FluentMigrator;

namespace AutoSourcarr.Core.Persistence.Migrations;

// Migration numbers are dates (yyyyMMddNN) so branches cannot collide. One set per database file, chosen by tag.
// New migrations after the baseline use the fluent API (Create.Table, Alter.Table, ...) so they run on SQLite and
// on Postgres (phase 16) unchanged; engine-specific SQL goes behind IfDatabase(...). The baselines run the
// released SQLite script (it sets journal_mode=WAL, which SQLite refuses inside a transaction, so the
// baselines run without FluentMigrator's transaction and wrap their data steps in their own); phase 16 puts them behind IfDatabase("SQLite") and adds the Postgres form.

/// <summary>Catalogue database at the 0.4.4 schema: new files are created, 0.3.x and 0.4.x files are upgraded.</summary>
[Migration(2026103101, TransactionBehavior.None, "catalog: 0.4.4 baseline (released SQL + 0.3.x upgrades)")]
[Tags(SchemaMigrator.CatalogTag)]
public sealed class CatalogBaseline : ForwardOnlyMigration
{
    public override void Up() => Execute.WithConnection((connection, transaction) =>
        SchemaMigrator.ApplyBaseline(connection, transaction, DatabaseFile.Catalog));
}

/// <summary>Feature tables 0.4.x created on first use (catalogue identity, gallery policy, identity review).</summary>
[Migration(2026103102, TransactionBehavior.None, "catalog: feature tables created up front")]
[Tags(SchemaMigrator.CatalogTag)]
public sealed class CatalogFeatureTables : ForwardOnlyMigration
{
    public override void Up() => Execute.WithConnection(SchemaMigrator.ApplyFeatureTables);
}

/// <summary>Chapter-origins database at the 0.4.4 schema.</summary>
[Migration(2026103111, TransactionBehavior.None, "origins: 0.4.4 baseline (released SQL + 0.3.x upgrades)")]
[Tags(SchemaMigrator.OriginsTag)]
public sealed class OriginsBaseline : ForwardOnlyMigration
{
    public override void Up() => Execute.WithConnection((connection, transaction) =>
        SchemaMigrator.ApplyBaseline(connection, transaction, DatabaseFile.Origins));
}
