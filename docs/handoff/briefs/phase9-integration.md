PHASE 9 INTEGRATION BRIEF (three areas built in parallel, uncommitted, never compiled). Wire them in src/AutoSourcarr.Api/ApplicationRuntime.cs, ApplicationRuntime.Matching.cs (OpenMatching), Endpoints/ApiV1Endpoints.cs, AutoSourcarr.Core.csproj and tests/AutoSourcarr.Tests/Program.cs.

9B SHARING
- AutoSourcarr.Core.csproj: `<EmbeddedResource Include="Compatibility/*.json" />` (required).
- ApiV1Services: `public CatalogueSharingV1Services? CatalogueSharing { get; init; }`; MapApiV1 after ReviewV1Endpoints: `CatalogueSharingV1Endpoints.Map(group, json, services.CatalogueSharing);`
- ApplicationRuntime: field `private CatalogueReviewTasks? _catalogueReviews;`; after OpenMatching:
  var sharingGallery = new GalleryEvidenceService(owner.Database, owner._catalogueWriteGate);
  var sharingExchange = new CatalogueExchange(owner.Database, owner._catalogueWriteGate, owner.Privacy, galleryEvidence: sharingGallery.CatalogueAdapter, structure: owner.Catalogue.Structure, invalidate: () => owner._duplicates?.Invalidate()) { Log = <the ONE DecisionLog of OpenMatching (use the scoped one if review needs it)> };
  var identities = new PortableIdentityResolver(owner.Database);
  owner._catalogueReviews = new CatalogueReviewTasks(sharingExchange, owner.Tasks, owner.Lanes, owner.Io);
  CatalogueSharing = new CatalogueSharingV1Services { Exchange = sharingExchange, Identities = identities, Reviews = owner._catalogueReviews, IdentityReview = new IdentityReview(owner.Database, owner._catalogueWriteGate, identities) { Log = <same log> }, AdminLock = owner._admin, Io = owner.Io };
  CloseAsync before "Task lanes": dispose _catalogueReviews.
- No Program.cs line needed (CatalogueSharingTests.Register calls the new tests). CatalogueSharingEndpoints.cs was deleted (was test-only) — make sure no test still references MapCatalogueSharing / the old class.
- Version constant: the agent notes the runtime version constant still says "0.4.3-migration" while compatibility.json says 0.5.0 — find the app identity/version source (Identity.Version, VERSION file 0.4.5?) and make CompatibilityList read the real app version or keep them consistent; don't bump VERSION (release gate) — use the conversion's target "0.5.0" only where it's descriptive.

9C METADATA + DISCOVERY
- ApiV1Services: `public MetadataV1Services? Metadata { get; init; }`, `public DiscoveryV1Services? Discovery { get; init; }`; MapApiV1: `MetadataV1Endpoints.Map(group, json, services.Metadata); DiscoveryV1Endpoints.Map(group, services, json, services.Discovery);`
- OpenMatching (usings AutoSourcarr.Core.Catalogue.Metadata, AutoSourcarr.Core.Sources.Discovery, AutoSourcarr.Core.Persistence, System.Globalization): after `titles`, move `evidence` above `ingest`:
  void Choose(long id) { lock (writeGate) database.Write(DatabaseFile.Catalog, s => { catalogue.NativeMetadata.ChooseMetadata(s, id, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)); return 0; }); }
  var providers = new MetadataProviderSet(type => owner._connections?.Enabled(type) ?? [], () => owner.Settings);
  var identity = new MetadataIdentityResolver(database, writeGate, () => owner.Settings, providers.Current, evidence.Snapshot) { AddTitle = (id, t, lang, kind, src) => titles.AddTitle(id, t, lang, kind, src), IdentitiesChanged = id => { Choose(id); owner._duplicates?.Invalidate(); }, Failed = (id, e) => owner.Logs.Record("Metadata", "Identity lookup failed (" + e.GetType().Name + ").") };
  var chooser = new MetadataChooser(database, writeGate, decisions, titles) { Changed = id => { owner._duplicates?.Invalidate(); _ = catalogue.Covers.ClearKeys([FormattableString.Invariant($"entry-{id}")]); } };
  catalogue.NativeMetadata.FieldPolicy = chooser;
  ingest construction adds: followUps: new MetadataIngestFollowUps(identity) { Choose = Choose, Hydrate = (s, m) => catalogue.Hydration.Schedule(s, m) }, identityOnIngest: () => owner.Settings.MetadataIdentityOnIngest,
  duplicates gate: new MetadataIdentityGate(new ScoredAutomaticMatchGate(...existing...) { OptionsFor = ...existing... }); after construction `duplicates.Hydration = identity;`
  var discovery = new CatalogueDiscovery(database, writeGate, ingest, new SuwayomiCatalogueClient(owner.SourceRuntime.Client), () => owner.Settings) { Identity = identity, ChooseMetadata = Choose, RecordAttached = (_, s, m) => catalogue.Hydration.Schedule(s, m) };
  services: Metadata = new MetadataV1Services { Chooser = chooser }, Discovery = new DiscoveryV1Services { Discovery = discovery, SubbedEntryIds = (force, token) => library.SubbedEntryIdsAsync(force, token) }
  Dispose `identity` (IAsyncDisposable) and `providers` (IDisposable) at shutdown (keep fields).
- Program.cs: AutoSourcarr.Tests.Catalogue.Metadata.MetadataChooserTests.Register(suite); AutoSourcarr.Tests.Catalogue.Metadata.MetadataIdentityTests.Register(suite); AutoSourcarr.Tests.Sources.Discovery.CatalogueDiscoveryTests.Register(suite);
- Optional hooks not wired: CatalogueDiscovery.SourceResult (source health: wire to SourceHealthStore if a simple record method exists), identity.Fingerprints (Imaging, phase 10).

9A IMPORTS
- csproj already has `<Protobuf Include="Imports/Backup/MihonBackup.proto" GrpcServices="None" Access="Internal" />`.
- ApiV1Services: `public ImportsV1Services? Imports { get; init; }`; MapApiV1: `ImportsV1Endpoints.Map(group, json, services.Imports);`
- OpenMatching (using AutoSourcarr.Core.Imports): after ingest/manual exist:
  var imports = new ImportRuns(database, writeGate, decisions, ingest) { Manual = manual, History = decisionLog, Jobs = owner.Jobs, DataDirectory = owner._dataDirectory, Options = () => ImportOptions.From(owner.Settings), Library = new SuwayomiImportLibrary(library, () => owner._main), Sources = new DelegateImportSourceSearch((s, mode, page, q, t) => owner.SourceRuntime.Client.FetchSourceMangaAsync(s, mode, page, q, t), (id, m, ch, t) => owner.SourceRuntime.Client.FetchMangaAndChaptersAsync(id, m, ch, t)), Metadata = new MetadataClientsImportSearch(() => <enabled IMetadataProviderClient list: from 9C MetadataProviderSet.Current or the Connections clients>), Identities = <IImportIdentitySink over 9C MetadataIdentityResolver's identity_links upsert, if a method fits; otherwise leave null and say so> };
  _ = imports.ScheduleStartupRecovery();
  Imports = new ImportsV1Services { Runs = imports }
  Also: after a database reset's rebuild finishes (CatalogueMaintenance ResetAsync, Catalogue/Maintenance), call imports.RecheckAfterReset(actor) through a CatalogueMaintenanceHooks member (add one if needed).
- Program.cs: AutoSourcarr.Tests.Imports.ImportsTests.Register(suite);

GENERAL
- One DecisionLog / ScopedDecisionLog instance is shared (see OpenMatching). Keep construction order valid (everything used is constructed before).
- AccessPolicy: check new paths: /api/v1/imports*, /api/v1/catalog-sharing*, /api/v1/identity-review*, /api/v1/portable-identities*, /api/v1/compatibility, /api/v1/entries/{id}/metadata (GET guest ok per 9C; PUT/DELETE admin), /api/v1/maintenance/refresh-*, /api/v1/subbed/* — admin unless stated. RequestPolicy: uploads (imports/mihon-backup, catalog-sharing/preview) need the BoundedUpload/NonInteractive treatment the /api/admin versions had; check http-policy.json fixture parity tests.
- Extend tests/AutoSourcarr.Tests/Matching/MatchingWiringTests.cs (or a new Phase9WiringTests.cs) to assert all new v1 routes are mapped with the right access level and list GETs answer 200 on an empty catalogue.
- Run check-project.sh (AUTOSOURCARR_SKIP_PYTEST=1, env from config/autosourcarr.env.example; must end "Project checks completed.") and ci-pytest.sh ("No new Python failures (baseline 27 known)").
