# .NET conversion development

Conversion phase 0 (stage 3) restructured the former Core / Infrastructure / Repair / Bootstrap projects into the feature-folder layout below; `docs/port-map.md` maps every Python function and route of 0.4.5 to its C# target. `Repair/Runner` stays inside Core for now because the runner code and the repair-artifact depot reference each other; the separate low-rights `Repair.Runner` process is split out in phase 6.

These projects sit beside the current 0.4.x application. The production entrypoint
and backend are unchanged. This is an unfinished migration, without a published
.NET image or a complete replacement host. The executable core host now exists.

| Project | Responsibilities |
| --- | --- |
| Core | One feature folder per area, each holding its models, logic and data access (Sonarr's `NzbDrone.Core` layout): Catalogue (Chapters, Records, Structure, Tags, Gallery, Sharing), Matching, Reader, Sources (Runtime, Inventory, Repositories, Configuration, Controls), Repair (Artifacts, Runner), Jobs, Settings; plus the shared Persistence, Diagnostics, Privacy, Imaging, Concurrency, Serialization, Collections and Unicode folders |
| Api | Executable core host today, owned startup/shutdown, authorization, request ownership and one endpoint file per area under `Endpoints/` |
| Host | Finite unprivileged startup resolver (the former Bootstrap); becomes the real host entry point in conversion phase 1 |
| `../native` | Finite container bootstrap, source ownership/observation and constrained repair launchers; image integration remains open |

The API DLL now has an entrypoint and emits the dependency/runtime files expected
by native startup. Its cohesive application owner composes the current SQLite pair,
settings, privacy/diagnostics, source controller, native snapshots and HTTP lifetime.
It verifies Main privacy and identity before reading configuration, then accepts
the actual inherited channel before source work. Root/static, liveness, preferences,
errors and compatibility routes are registered. One catalogue owner now binds local
Sources/Entry, chapter/page/image, serving-profile and display-metadata routes to
the same database and write gate. The remaining domain and repair
routes still need their real owners; no successful stand-ins are registered.

Held locks protect instance data/configuration/database roots. Shared raw storage
is excluded. Separate liveness capacity checks both databases; shutdown cancels
admission and joins actual requests, workers and source/native cleanup before
releasing storage. Saved timezone settings update the process and source-launch
environment. Reader page and cache work is cancelled and physically joined before
source transports or database closure, including background fills and a cancelled
read waiting for the shared catalogue gate. Infrastructure composes the Repair protocol with private per-job
artifact storage; it does not promote received code or binaries.

The catalogue owner also composes actual native metadata, structural mutation,
chapter visibility, saved gallery replay and cover invalidation dependencies.
Feature schemas initialize before structural consumers. Native manga/webtoon
separation preserves the original primary/count selection and manual refusal rules,
then uses the existing durable split and cross-file recovery. It rejects ambiguous
native ownership and nested database sessions. An admitted mutation completes its
physical work; failed cleanup retains the journal. Owner creation performs no
automatic decisions. Offline gallery reconciliation now cleans current native
records, synchronizes tags/titles and rechecks physical/manual/policy ownership
before separating animation. Cached replay preserves rejected chapters, pages,
served history and scan evidence through disk-backed complete source batches.
The host prepares its private chapter workspace internally; no host setup step is
added. A native hydration owner now fetches actual details and runs the metadata,
title/tag and media/gallery phases. Requests deduplicate and promote in place;
idle waiting, shared source capacity and whole-task lanes stay independent.
Exact fetched identity and current physical/manual ownership guard publication.
An admitted operation retains its source session and physical database work through
shutdown, including late failures. Authoritative detail follows only a verified
local split's new owner/role. Pending jobs and source cover hints are bounded.
Cover recovery/delivery, startup scheduling and remaining domain workflows still
need integration. Duplicate snapshot caching is not yet composed into this host;
current native evidence reads query the actual database.

Use the SDK selected by `../global.json`, from the project root:

```bash
dotnet restore AutoSourcarr.slnx            # first restore after stage 3 regenerates packages.lock.json; use --locked-mode afterwards
dotnet build AutoSourcarr.slnx -c Release --no-restore
dotnet run --project tests/AutoSourcarr.Tests -c Release --no-build
```

The compiled checks use synthetic databases, files and controlled HTTP/process
boundaries. Set `AUTOSOURCARR_TEST_ROOT` to choose the temporary parent. They cover
populated schema compatibility, exact large IDs, manual decisions, source/reader
ownership, cancellation, recovery and the released pure-function/SQLite fixtures.
SQLite optimizer statistics are excluded from cross-build value equality;
application rows and sequences remain checked.

Native metadata is independent of display locks and imported cached text. Reviewed
merge/split recovery retains pending journals until real metadata, reader visibility
and cache invalidation succeed. Source preferences use current engine generations
and screen revisions; repository edits use the canonical configuration gate and
raw file revisions, without implicit restart. Full layout and domain composition
remain unfinished; the core source runtime is now wired into the executable host.

Saved gallery replay streams complete intents through private owned files and
preserves chapter/candidate/page/served history and scan evidence. Native tag storage
retains actual source provenance and committed reindex cursors. Source HTTP sessions
retain physical open/read/close ownership through pause/drain/resume, including
cancelled response delivery and concurrent reader shutdown. HTTP drain does not
establish a stopped or ready JVM. The source controller separately requires a fresh
HTTP observation and the same native process generation before admitting work.
It keeps configured background/foreground capacity, physical cleanup and API-shape
observations separate from source health. A discarded shape check can retry after
real readiness recovery without duplicating an active check.

The concrete catalogue reader combines current SQLite queries, display metadata
and real cover-cache presence for Sources/Entry reads, now composed into the host.
Aliases use the canonical cache identity; admin profile writes retain the original
canonical-ID contract. Full Browse/Refresh still
requires its domain owners; the read-only contract cannot register those routes.

Startup settings now retain exact requested integer IDs and original CWD.env
interpolation/canonical recovery. The resolver and bounded native request preserve
legacy owner inference without granting credentials. Finite native bootstrap now
prepares a fixed private observation exchange and transfers its checked descriptor
only to the source owner, closing the parent's copies before Main's fork. The JVM
does not inherit that capability. A fork-only capture component now checks inherited
privacy, fixed roots, full bounded bytes and final source stability through output
publication. Its 176-byte result has strict C# decoding against 95 actual C results,
with 384 modeled syscall assertions. Failed output retains only known created inode
identity for reconciliation. Native parent fork/FD closure, separate child tracking,
result EOF, exact reap and bounded ticket cleanup are now implemented. The actor
checks the 280-byte completion against its accepted owner and pending request;
one lease survives until native cleanup succeeds. Status has an independent lane,
generation changes wait for release, and shutdown owns pending capture and cleanup.
The Main receiver now opens the fixed private exchange itself, checks the real
accepted Main identity including its optional KVM group, and matches held/named
file identities, metadata, bounded bytes and full SHA. One transaction owns native
capture, off-actor file reading and uncancelled native Release on every path.
Pause/shutdown drain the transaction before changing the native process lifetime.
The installed inventory uses this typed reader with its existing generation,
GraphQL epoch and two-read fences. Full domain composition and Linux qualification
remain required. Fresh native startup now reconciles abandoned private snapshot
tickets before either Main or the source owner is forked. A Main568 child uses
metadata handles, validates the whole bounded directory before deleting anything,
and refuses unknown or changed files. The parent requires its exact report,
stream EOF, actual exit and an empty child set. This cleanup never reads live
source files, application configuration, library data or shared raw sources.
A normal CLR exec is unsuitable for inheriting snapshot descriptors because it resets
dumpability before a managed guard can restore it. The installed inventory's typed
reader seam keeps generation, HTTP epoch, identity and two independent byte reads,
including backend-buffer reuse and physically joined cancellation. The default
controlled disk path remains compatible; the new core host selects the actual native
snapshot owner in integrated mode. Full domain wiring and Linux execution remain unfinished.
The fixed native source launcher now
compiles and links; immutable pinned Suwayomi assets and actual image composition
remain required. Post-exec CLR privacy guards run before resolver configuration;
Main now calls its early guard before loading private configuration too.
The full replacement host, image and actual Linux handoff remain unfinished.

Raw storage retains immutable public/private versions, provenance and exact rollback
bytes. ZIP64, BZIP2/LZMA, XML/AXML and every payload fingerprint are checked separately
from cold import. Fixed, Retry and Revert history does not itself prove a working
source. Diagnostic exports redact before persistence and omit private tool payloads.

The native owner now sends bounded source-output frames into a separate owned
redacting consumer. Log floods and slow redaction cannot block native control;
gaps suppress their process generation so a missing private-key marker cannot
expose later fragments. Diagnostic completion never implies process readiness or
clean native retirement. The new core host passes the actual log store and shares
its privacy registry with source environment/settings owners.

Actual Windows checks exercise SQLite/files, native hard-link rejection, private
file locks and six-format raster decoding with path/delegate/resource policy.
Raster configuration stays in its private scratch directory and verifies an
actual outside-path denial before accepting input. Its short private directory name
reserves native blob/cache filename space as well as policy filenames. Three fresh
Windows processes retain 192-character temporary parents, six formats and outside
denial; unsupported longer parents fail before native setup and clean the empty
directory. Resource, delegate, coder and path restrictions remain unchanged.
Hardware tests use controlled processes and streams and start no VM or native
launcher. The existing launchers and a private source-owner probe cross-compile
and link as Linux x86_64 executables.426pure C startup tests run under a trusted
local WASI harness. Neither check is Linux execution, repair isolation, KVM
enforcement or abrupt-parent-death proof.

The intended repair boundary uses KVM, Landlock ABI6, syscall restrictions and a
fixed trusted guest, without host directory shares or direct guest networking.
Parent-death setup is paired with a dedicated creating thread and a V2 proof.
Transfers bind exact bytes to the qualified session and quarantine outputs; they
do not certify packages or source behavior. The new guest artifact library verifies
complete canonical raw trees over controlled streams/files, but has no boot/self-test,
worker/tool or project-commit wire capability. Missing protection fails
closed. There is no software-emulation isolation fallback.

Remaining work includes main catalogue/browse/matching/import/provider workflows,
automatic gallery layout, complete source/tool/account supervision, remaining host
routes, real
guest/build/cold-import/promotion/egress, image packaging and Linux/NAS qualification.
TestServer does not establish Kestrel, rendered Web UI or live reader behavior.
The catalogue host checks additionally use a confined real loopback Kestrel source
fixture for HTTP page/image transfers and cancellation. It is controlled upstream
evidence, not a live Suwayomi, full UI or Linux-isolation qualification.
The single project changelog records current checks and material limits.
