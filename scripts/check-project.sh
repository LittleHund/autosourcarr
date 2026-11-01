#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

step(){ printf '\n[%s/10] %s\n' "$1" "$2"; }

step 1 'Python syntax'
python scripts/check-linux-text.py .
python -m compileall -q autosourcarr/app autosourcarr/tests source_tools repair-worker extension/signing_state.py scripts/configure-repair-worker.py scripts/knowledge-db.py

step 2 'Backend regression tests'
if [[ ${AUTOSOURCARR_SKIP_PYTEST:-0} == 1 ]]; then
  echo 'SKIP: backend suite not run by this invocation (AUTOSOURCARR_SKIP_PYTEST=1)'
else
  PYTHONPATH=autosourcarr pytest -q autosourcarr/tests
fi

python scripts/build-ui-styles.py --check
step 3 'Compose YAML + env_file wiring'
python - <<'PY'
from pathlib import Path
import yaml

files = ('docker-compose.yml','docker-compose.truenas.yml','examples/manga-cluster-autosourcarr-services.yml','compose-env-file.yml')
for name in files:
    data = yaml.safe_load(Path(name).read_text())
    assert isinstance(data, dict) and 'services' in data, name
    assert 'autosourcarr' in data['services'] and 'autosourcarr-source-host' not in data['services'], name
    cfg = data['services']['autosourcarr']
    assert cfg['environment']['SOURCE_ENGINE_MODE'] == 'integrated', name
    assert cfg['image'] == 'autosourcarr:local' and cfg['pull_policy'] == 'build', name
    assert cfg['build']['context'] == ('./autosourcarr' if name == 'docker-compose.yml' else '/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/autosourcarr'), name
    assert any(v.endswith(':/home/suwayomi/.local/share/Tachidesk') for v in cfg['volumes']), name
    assert not cfg.get('depends_on'), name
    assert cfg['environment']['APP_SERVICES_MODE'] == 'integrated', name
    assert set(data['services']) <= {'autosourcarr','suwayomi'}, name
    assert '/var/run/docker.sock' not in str(cfg), name
    for other in ('suwayomi','private-suwayomi'):
        assert 'ports' not in data['services'].get(other, {}), f'{name}: 0.4.5 removed the Suwayomi host port; use Open in WebView'
    print(name, 'YAML OK')

for name in ('docker-compose.truenas.yml','examples/manga-cluster-autosourcarr-services.yml'):
    data = yaml.safe_load(Path(name).read_text())
    for service in ('autosourcarr',):
        cfg = data['services'][service]
        assert cfg.get('env_file'), f'{name}: {service} must use env_file'
        assert set(cfg.get('environment', {})) <= {'SOURCE_ENGINE_MODE','SOURCE_ENGINE_UID','SOURCE_ENGINE_GID','APP_SERVICES_MODE'}, name
        assert cfg['image'] == 'autosourcarr:local', name
        assert 'command' not in cfg, 'Use the image entrypoint, never the old install-on-start wrapper'
    print(name, 'env_file wiring OK')
PY

step 4 'Environment templates'
python - <<'PY'
from pathlib import Path

def parse_env(path):
    vals = {}
    for raw in Path(path).read_text().splitlines():
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        assert '=' in line, f'{path}: invalid env line: {raw!r}'
        key, value = line.split('=', 1)
        assert key and key.replace('_','').isalnum(), f'{path}: invalid key {key!r}'
        vals[key] = value
    return vals

for suffix in ('.example',):
    auto = parse_env(f'config/autosourcarr.env{suffix}')
    source = parse_env(f'config/source-host.env{suffix}')
    for key in ('AUTOSOURCARR_INTERNAL_URL','AUTOSOURCARR_PUBLIC_URL','AUTOSOURCARR_READER_API_TOKEN','AUTOSOURCARR_AUTHELIA_BASIC_USERNAME','AUTOSOURCARR_AUTHELIA_BASIC_PASSWORD','MAIN_SUWAYOMI_URL','MAIN_SUWAYOMI_DOWNLOADS_PATH','SOURCE_SUWAYOMI_URL','PYTHONPATH','SETTINGS_CONFIG_DIR','METADATA_ENRICHMENT_ON_STARTUP','METADATA_ENRICHMENT_STARTUP_LIMIT','DUPLICATE_AUTO_MERGE_MIN_EXACT_TITLES','DUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD','AUTO_MISSING_SOURCE_DISCOVERY','AUTO_MISSING_SOURCE_ENTRIES_PER_FEED','AUTO_MISSING_SOURCE_SOURCE_LIMIT','METADATA_IDENTITY_ENABLED','METADATA_IDENTITY_PROVIDERS','METADATA_IDENTITY_INCLUDE_ADULT','AUTOMATIC_DUPLICATE_MATCHING','IMPORT_RESOLVE_CONCURRENCY','EXACT_THUMBNAIL_MATCHING','THUMBNAIL_FINGERPRINT_ENTRIES_PER_PASS','AUTHELIA_USER_HEADER','DATABASE_DIR','CONTINUOUS_CATALOG_DISCOVERY','CONTINUOUS_CATALOG_INTERVAL_SECONDS','CONTINUOUS_CATALOG_MODES','CONTINUOUS_CATALOG_BACKLOG_LIMIT'):
        assert key in auto, f'autosourcarr env missing {key}'
    for key in ('BIND_PORT','EXTENSION_STORES','MAX_SOURCES_IN_PARALLEL','WEB_UI_ENABLED'):
        assert key in source, f'source-host env missing {key}'
    assert auto['SOURCE_ENGINE_MODE'] == 'integrated'
    assert auto['SOURCE_SUWAYOMI_URL'].startswith('http://'), 'External rollback URL retained'
    assert int(auto['SOURCE_HOST_WORK_CONCURRENCY']) >= 1
    assert int(source['MAX_SOURCES_IN_PARALLEL']) >= 1
    assert auto['BACKGROUND_REFRESH_ON_STARTUP'].lower() == 'false'
    assert auto['BACKGROUND_DEDUP_ON_STARTUP'].lower() == 'true'
    assert auto['METADATA_ENRICHMENT_ON_STARTUP'].lower() == 'false'
    assert int(auto['AUTO_VARIANT_STARTUP_ENTRIES']) == 0
    assert int(auto['BACKGROUND_TRANSLATION_ENTRIES_PER_STARTUP']) == 0
    assert float(auto['DUPLICATE_AUTO_MERGE_SUMMARY_THRESHOLD']) >= 0.70
    print(f'config/autosourcarr.env{suffix} OK')
    print(f'config/source-host.env{suffix} OK')
PY

python scripts/sync-bundled-tools.py --check

step 5 'JavaScript syntax'
if command -v node >/dev/null 2>&1; then
  node --check autosourcarr/app/static/repair-login.js
  node --check autosourcarr/app/static/confirmation-dialog.js
  node autosourcarr/tests/ui_v0337_confirmation.js
  node autosourcarr/tests/ui_v0338_account_resources.js
  node autosourcarr/tests/ui_v0338_api_recovery.js
  node autosourcarr/tests/ui_v0339_original_apk.js
  node autosourcarr/tests/ui_repair_new_host.js
  node autosourcarr/tests/ui_v045_entry_webview.js
  node --check autosourcarr/app/static/repair-tasks.js
  node autosourcarr/tests/ui_v0340_repair_tasks.js
  node autosourcarr/tests/ui_v0343_repair_visibility.js
  node autosourcarr/tests/ui_v0344_address_review.js
  node --check autosourcarr/app/static/source-raws.js
  node autosourcarr/tests/ui_v0345_repair_lifecycle.js
  node --check autosourcarr/app/static/source-health.js
  node autosourcarr/tests/ui_v0341_source_health.js
  node --check autosourcarr/app/static/setup.js
  node autosourcarr/tests/ui_setup.js
  node autosourcarr/tests/ui_setup_connections.js
  node autosourcarr/tests/ui_v0321.js
  node --check autosourcarr/app/static/matching-review.js
  node autosourcarr/tests/ui_matching_review.js
  node autosourcarr/tests/ui_v0325_matching_state.js
  node autosourcarr/tests/ui_v0325_duplicate_poll.js
  node autosourcarr/tests/ui_v0326_response_ownership.js
  node autosourcarr/tests/ui_v0327_reader_preflight.js
  node --check autosourcarr/app/static/repair-isolation.js
  node --check autosourcarr/app/static/reimagined-core.js
  node --check autosourcarr/app/static/reimagined.js
  node --check autosourcarr/app/static/reimagined-controls.js
  node --check autosourcarr/app/static/instance-tools.js
  node --check autosourcarr/app/static/library-controller.js
  node autosourcarr/tests/ui_reimagined_arr.js
  node autosourcarr/tests/ui_reimagined_core.js
  node autosourcarr/tests/ui_reimagined_tools.js
  node --check autosourcarr/app/static/app.js
  node --check autosourcarr/app/static/navigation-state.js
  node --check autosourcarr/app/static/connections.js
  node --check autosourcarr/app/static/internal-services.js
  node --check autosourcarr/app/static/workbench.js
  node --check autosourcarr/app/static/mixed-entries.js
  node --check autosourcarr/app/static/source-controls.js
  node --check autosourcarr/app/static/source-repairs.js
  node --check autosourcarr/app/static/tag-review.js
  node --check autosourcarr/app/static/orphan-sources.js
  if grep -q 'statusCard(' autosourcarr/app/static/app.js; then
    echo 'ERROR: obsolete undefined statusCard() call remains in app.js' >&2
    exit 1
  fi
  if grep -q 'setInterval(poll,1500)' autosourcarr/app/static/app.js; then
    echo 'ERROR: obsolete 1.5s permanent task polling remains in app.js' >&2
    exit 1
  fi
  grep -q "timeoutMs:20000,suppressClientError:true" autosourcarr/app/static/app.js
  grep -q "tasksPollTimer=setTimeout(poll,delay)" autosourcarr/app/static/app.js
  grep -q "method:'POST',body:JSON.stringify({ids})" autosourcarr/app/static/app.js
  grep -q "renderErrorsSnapshot();" autosourcarr/app/static/app.js
  grep -q "English hint" autosourcarr/app/static/app.js
  grep -q "loadManualGroups" autosourcarr/app/static/app.js
  grep -q "reset-manual-groups" autosourcarr/app/static/app.js
  grep -q "reset-manual-matches" autosourcarr/app/static/app.js
  grep -q "Searching local catalog and enabled sources for possible matches" autosourcarr/app/static/app.js
  grep -q "item.applied_match||item.matched_entry" autosourcarr/app/static/app.js
  grep -q "data-review=\"searching\"" autosourcarr/app/static/index.html
  grep -q "defer_hydration:true" autosourcarr/app/static/app.js
  grep -q "refresh-metadata?background=true" autosourcarr/app/static/app.js
  grep -q "discover-additional?background=true" autosourcarr/app/static/app.js
  grep -q 'match/${entryId}?background=true' autosourcarr/app/static/app.js
  if grep -q "await refreshBothMatrices();await renderInlineEntry" autosourcarr/app/static/app.js; then
    echo 'ERROR: simple entry mutation again waits for both 500-row matrices' >&2
    exit 1
  fi
  grep -q "item.backup_thumbnail_url" autosourcarr/app/static/app.js
  grep -q "comparisonCover" autosourcarr/app/static/workbench.js
  grep -q "importAcceptedMatchHtml" autosourcarr/app/static/app.js
  grep -q "rejectImportMatch" autosourcarr/app/static/app.js
  grep -q "resetImportRejections" autosourcarr/app/static/index.html
  grep -q "resetImportManualAutoresolve" autosourcarr/app/static/index.html
  grep -q "/api/admin/imports/reset-manual-autoresolve" autosourcarr/app/static/app.js
  grep -q "h.errors?.items" autosourcarr/app/static/app.js
  if grep -q "duplicate-candidates?ids=" autosourcarr/app/static/app.js; then
    echo 'ERROR: giant duplicate-candidate query-string regression returned' >&2
    exit 1
  fi
  grep -q '/static/mixed-entries.js?v=0.4.5' autosourcarr/app/static/index.html
  grep -q '/static/app.js?v=0.4.5' autosourcarr/app/static/index.html
  grep -q '/static/ui.css?v=0.4.5' autosourcarr/app/static/index.html
grep -q 'data-tab="manual-actions"' autosourcarr/app/static/index.html
grep -q 'data-manual-action-subtab="titles"' autosourcarr/app/static/index.html
grep -q '/api/admin/manual-actions' autosourcarr/app/static/app.js
grep -q 'CREATE TABLE IF NOT EXISTS manual_title_scopes' autosourcarr/app/db.py
  grep -q 'autoButton.disabled=!!automatic.paused' autosourcarr/app/static/app.js
  grep -q 'id="databaseStoragePath"' autosourcarr/app/static/index.html
  grep -q 'apiV1().getDatabaseStorage()' autosourcarr/app/static/app.js
  grep -q 'deferredUiRenders' autosourcarr/app/static/app.js
  grep -q 'entryInspectorGeneration' autosourcarr/app/static/app.js
  grep -q 'id="duplicateScopeSubbed"' autosourcarr/app/static/index.html
  grep -q 'id="duplicateScopeUnsubbed"' autosourcarr/app/static/index.html
  grep -q 'subbed_entry_ids' autosourcarr/app/main.py
  grep -q 'duplicateScope===.*subbed' autosourcarr/app/static/app.js
  grep -q 'data-source-subtab="installed"[^>]*>Installed' autosourcarr/app/static/index.html
  grep -q 'data-source-subtab="manage"[^>]*>Discover' autosourcarr/app/static/index.html
  grep -q 'id="sourceManagePanel"' autosourcarr/app/static/index.html
  grep -q '/api/v1/source-extensions/update-all' autosourcarr/app/static/app.js
  grep -q 'syncSourceRowsToMatrices' autosourcarr/app/static/app.js
  grep -q 'async def update_all_extensions' autosourcarr/app/suwayomi.py
  grep -q '@app.post("/api/admin/source-extensions/update-all"' autosourcarr/app/main.py
  grep -q 'id="sourceExtensionUpdatesSection"' autosourcarr/app/static/index.html
  grep -q 'id="sourceExtensionInstalledSection"' autosourcarr/app/static/index.html
  grep -q 'id="sourceExtensionAvailableSection"' autosourcarr/app/static/index.html
  grep -q '/api/admin/ui-preferences' autosourcarr/app/static/app.js
  grep -q '/api/v1/source-extension-icon/' autosourcarr/app/static/app.js
  grep -q 'data-theme="emerald"' autosourcarr/app/static/index.html
  grep -q 'data-settings-subtab="customization"' autosourcarr/app/static/index.html
  grep -q 'id="uiIdentityDiagnostics"' autosourcarr/app/static/index.html
  grep -q 'data-density="compact"' autosourcarr/app/static/index.html
  grep -q 'refreshManagedSourcesAfterMutation' autosourcarr/app/static/app.js
  grep -q 'exact_thumbnail_matching' autosourcarr/app/service.py
  grep -q 'schedule_automatic_duplicate_matching' autosourcarr/app/service.py
  grep -q 'schedule_source_reconciliation' autosourcarr/app/service.py
  grep -q 'thumbnail_fingerprints' autosourcarr/app/db.py
  echo 'app.js OK'
else
  echo 'SKIP: node is not installed'
fi

python - <<'PYHOTPATH'
from pathlib import Path
main=Path('autosourcarr/app/main.py').read_text()
service=Path('autosourcarr/app/service.py').read_text()
appjs=Path('autosourcarr/app/static/app.js').read_text()
heartbeat=appjs[appjs.index('async function refreshSparseEntryMetadata()'):appjs.index('function scheduleMetadataLivePoll')]
syncui=appjs[appjs.index('async function syncEntrySnapshot'):appjs.index('function scheduleEntrySnapshotSync')]
assert "rerenderMatrixPreservingScroll('matrix')" not in heartbeat and "renderDuplicates();" not in heartbeat, 'metadata heartbeat must not unconditionally rebuild active surfaces'
assert 'renderInlineEntry(' not in syncui and 'openBrowseEntry(' not in syncui, 'background entry sync must not rebuild/reopen an inspector'
assert 'patchOpenInspectorSnapshot(entry)' in syncui, 'background entry sync must patch open inspectors in place'
health=main[main.index('async def health'):main.index('@app.get("/api/v1/sources")')]
matrix=main[main.index('async def admin_matrix'):main.index('@app.get("/api/admin/duplicate-candidates"')]
assert 'service.duplicate_groups(' not in health, 'health must not rebuild duplicate graph'
assert 'cached_library_status()' in health, 'health must be cache-first for library status'
assert 'cached_subbed_payload()' in matrix, 'normal matrix must use cached main-library state'
assert 'import_manual_autoresolve' in service, 'manual import autoresolve persistence is missing'
assert '_import_manual_autoresolve_entry(item)' in service, 'backup import must consult learned manual mappings first'
assert 'subscription = await service.subbed_entry_ids()' not in matrix, 'matrix must not unconditionally wait on main Suwayomi'
assert 'schedule_duplicate_snapshot_refresh' in service
assert 'background_source_work' in service, 'background source priority context missing'
assert '_background_task_semaphore' in service, 'background task admission limit missing'
assert 'service.begin_interactive_request()' in main and 'service.end_interactive_request()' in main, 'interactive priority signal missing'
assert 'subscription = await service.subbed_entry_ids(force=True)' not in main[main.index('async def admin_delete_entry'):main.index('@app.delete("/api/admin/entries/{entry_id}/variants')], 'delete must not synchronously walk main Suwayomi'
assert '_background_source_semaphore = asyncio.Semaphore(' in service and 'settings.background_source_concurrency' in service, 'automatic source-host work must remain bounded by reserved capacity'
assert '_backup_existing_from_index(item, existing_index)' in service, 'import must check existing before source resolution'
assert 'await self.federated(' not in service[service.index('async def resolve_backup_manga_entry_details'):service.index('async def _main_autosourcarr_source_id')], 'backup resolver must not broad-federate every unresolved row'
assert 'generate_candidates=False' in service, 'import suggestion ranking must stay off the critical add/existing path'
assert 'manual_match_bindings' in service, 'persistent manual-match storage integration missing'
assert 'restore_persistent_manual_matches' in service, 'manual matches must restore after a normal database reset'
assert '_bind_backup_source_to_entry' in service, 'import must bind the enabled original source when resolvable'
assert '_fallback_title_queries' in service, 'conservative title fallback helper missing'
assert '_manual_candidate_manga' in service and 'schedule_manual_match_hydration' in service, 'local-first manual match path missing'
assert 'schedule_entry_refresh_job' in service and 'schedule_entry_discovery_job' in service, 'explicit entry network work must be queueable'
assert 'schedule_import_item_match' in service and 'schedule_import_item_retry' in service, 'import manual actions must be queueable'
assert 'schedule_import_item_reject' in service and 'import_match_rejections' in service, 'accepted import matches must support durable reject review'
assert "state='resolving'" in service and "state='retrying'" in service, 'import actions must leave unresolved UI state immediately'
assert 'def import_run_item(' in service, 'single-item import polling endpoint support missing'
assert '__autosourcarr_entry_id__:' in service, 'main-Suwayomi materialization must use the local exact-entry fast path'
assert 'fallback-title reference' in service and 'unique exact fallback title' in service, 'import source binding no longer uses fallback title queries'
assert 'automatic=True' in service[service.index('def schedule_import_translation_enrichment'):service.index('async def _main_autosourcarr_source_id')], 'no-candidate import rows must receive background live-source rescue'
assert 'suggestion_checked' in service, 'import live-suggestion completion state missing'
assert '_resolve_import_metadata_identity' in service, 'external metadata must participate in import resolution'
assert '_apply_import_metadata_to_entry' in service, 'accepted import metadata must transfer onto the resolved entry'
assert '_import_auto_resolve_candidate' in service, 'strict import auto-accept gate missing'
assert 'Auto-matched from combined metadata:' in service, 'post-discovery compatible candidates must auto-apply'
assert 'version = "0.2.84"' in service[service.index('def schedule_import_candidate_pipeline_upgrade'):service.index('async def _main_autosourcarr_source_id')], 'v0.2.84 must recover/requeue interrupted and unresolved imports once'
assert '_import_source_semaphore = self._background_source_semaphore' in service, 'import discovery must share the reserved background source lane'
assert 'source_ids = [sid for sid, _cfg in rows]' in service, 'automatic import discovery must consider all enabled Search sources before manual review gives up'
assert 'suggestion_state="searching"' in service and '_write_import_discovery_fields' in service and 'matched_snapshot_json' in service, 'incremental import discovery/audit snapshot state missing'
print('request hot-path regression guards OK')
PYHOTPATH

step 6 'Shell syntax'
for f in autosourcarr/start-truenas.sh extension/build-in-container.sh extension/build.sh extension/install-to-suwayomi.sh extension/verify-suwayomi-jar.sh extension/verify-tachiyomi-apk.sh extension/publish-apk.sh scripts/check-project.sh scripts/build-integrated-image.sh scripts/smoke-test.sh scripts/init-truenas-config.sh scripts/cleanup-old-helper-files.sh; do
  IFS= read -r first_line < "$f"
  if [[ "$first_line" == '#!/bin/sh' ]]; then sh -n "$f"; else bash -n "$f"; fi
  echo "$f OK"
done

step 7 'Extension compatibility + JAR/APK packaging guards + validators'
grep -q 'src:all:autosourcarr:assembleRelease' extension/build-in-container.sh
grep -q 'build/outputs/jar/release' extension/build-in-container.sh
grep -q 'build/outputs/apk/release' extension/build-in-container.sh
grep -q "name '\*.jar'" extension/build-in-container.sh
grep -q "name '\*.apk'" extension/build-in-container.sh
grep -q 'verify-tachiyomi-apk.sh' extension/build-in-container.sh
if grep -qE '(^|[[:space:]])python([[:space:]]|$)' extension/build-in-container.sh; then
  echo 'Builder must not depend on a Python command inside the Android SDK image.' >&2
  exit 1
fi
grep -q 'libVersion = "1.4"' extension/module/build.gradle.kts
grep -q 'name = "AutoSourcarr"' extension/module/build.gradle.kts
if grep -q 'name = "AutoSourcarr v\$autosourcarrVersion"' extension/module/build.gradle.kts; then echo 'Extension display name must stay AutoSourcarr.' >&2; exit 1; fi
python - <<'PYVER'
from pathlib import Path
v=Path('VERSION').read_text().strip(); p=[int(x) for x in v.split('.')]; code=p[0]*1000000+p[1]*1000+p[2]
assert v=='0.4.5' and code==4005
assert Path('repair-worker/server.py').read_text().count("'version': '"+v+"'")==2, 'Repair status/new-job version drifted'
print(f'extension version mapping OK: {v} -> 1.4.{code}')
PYVER
grep -q 'HttpSource' extension/module/src/autosourcarr/AutoSourcarr.kt
if grep -R -qE '^[[:space:]]*import kotlinx\.serialization|@Serializable|parseAs<' extension/module/src/autosourcarr; then
  echo 'Legacy Android extension reintroduced generated kotlinx.serialization parsing; old TachiyomiSY clients may crash.' >&2
  exit 1
fi
grep -q 'AUTOSOURCARR_READER_API_KEY' extension/module/src/autosourcarr/AutoSourcarr.kt
grep -q 'X-AutoSourcarr-API-Key' extension/module/src/autosourcarr/AutoSourcarr.kt
grep -q 'AUTOSOURCARR_READER_API_TOKEN' extension/build-in-container.sh
grep -q 'write_auth_config "/api/v1" ""' extension/build-in-container.sh
grep -q 'write_auth_config "/api/reader/v1" "$READER_API_TOKEN"' extension/build-in-container.sh
grep -q 'SIGN_DIR="$CONFIG_DIR/.extension-signing"' extension/build-in-container.sh
grep -q 'apksigner' extension/build-in-container.sh
if grep -q 'KeiSource' extension/module/src/autosourcarr/AutoSourcarr.kt; then
  echo 'AutoSourcarr source still links KeiSource; Suwayomi compatibility regression.' >&2
  exit 1
fi
if grep -q '@Synchronized' extension/module/src/autosourcarr/AutoSourcarr.kt; then
  echo 'loadFilterSources reintroduced synchronized JVM bytecode; avoid this compatibility risk.' >&2
  exit 1
fi
if grep -q 'JAR is missing AutoSourcarr.class' extension/build-in-container.sh extension/verify-suwayomi-jar.sh; then
  echo 'Builder reintroduced the v0.2.11 exact-class-name false failure.' >&2
  exit 1
fi
AUTOSOURCARR_CHECK_BASH="$BASH" python - <<'PYARTIFACT'
import os
from pathlib import Path
import subprocess
import tempfile
from zipfile import ZipFile, ZIP_DEFLATED

# Per-invocation synthetic files cannot collide with another checker or overwrite
# a pre-existing fixed /tmp filename. They are structure fixtures, not builds.
with tempfile.TemporaryDirectory(prefix='autosourcarr-check-') as temporary:
    folder = Path(temporary).resolve()
    assert folder.parent == Path(tempfile.gettempdir()).resolve()
    jar = folder / 'fixture.jar'
    with ZipFile(jar, 'w', ZIP_DEFLATED) as z:
        z.writestr('AndroidManifest.xml', b'<manifest/>')
        # Release JARs may rename the original Kotlin class after generation.
        z.writestr('a/b/C.class', b'\xca\xfe\xba\xbe' + b'placeholder')
    apk = folder / 'fixture.apk'
    with ZipFile(apk, 'w', ZIP_DEFLATED) as z:
        z.writestr('AndroidManifest.xml', b'manifest')
        z.writestr('classes.dex', b'dex\n035\0placeholder')
        z.writestr('resources.arsc', b'resources')
    for script, artifact in (('verify-suwayomi-jar.sh', jar), ('verify-tachiyomi-apk.sh', apk)):
        subprocess.run([os.environ['AUTOSOURCARR_CHECK_BASH'], 'extension/' + script,
                        artifact.as_posix()], check=True)
PYARTIFACT

step 8 'Required files + overlay-safe extension staging'
# This private project archive retains its active env files at the user's request.
# Validate templates above; never parse, print, replace or remove active settings.
for f in \
  VERSION CHANGELOG.md README.md TRUENAS.md \
  compose-env-file.yml \
  config/autosourcarr.env.example config/source-host.env.example config/README.md \
  autosourcarr/VERSION autosourcarr/app/main.py autosourcarr/app/service.py autosourcarr/app/db.py autosourcarr/app/env_settings.py autosourcarr/app/error_registry.py autosourcarr/app/metadata_catalog.py \
  autosourcarr/app/static/index.html autosourcarr/app/static/workbench.js autosourcarr/app/static/workbench.css \
  extension/verify-suwayomi-jar.sh extension/verify-tachiyomi-apk.sh extension/INSTALL-SUWAYOMI.txt extension/signing_state.py \
  extension/module/build.gradle.kts \
  extension/module/src/autosourcarr/AutoSourcarr.kt \
  extension/module/src/autosourcarr/Dto.kt \
  extension/module/src/autosourcarr/Filters.kt \
  extension/module/src/autosourcarr/MiniJson.kt \
  extension/module/src/autosourcarr/BuildAuth.kt \
  repair-worker/baseline_manifest.py autosourcarr/app/repair_baseline.py autosourcarr/app/repair_evidence.py \
  scripts/cleanup-old-helper-files.sh; do
  test -s "$f" || { echo "Missing: $f" >&2; exit 1; }
done
# Builder must recreate the injected module and copy an explicit current-file manifest;
# it must not recursively copy a possibly stale local source tree.
grep -q 'rm -rf "$TARGET"' extension/build-in-container.sh
for f in AutoSourcarr.kt Dto.kt Filters.kt MiniJson.kt BuildAuth.kt; do grep -q "$f" extension/build-in-container.sh; done
if grep -q 'cp -a "$ROOT/extension/module"' extension/build-in-container.sh; then
  echo 'Extension staging recursively copies extension/module; stale overlay files could leak into the build.' >&2
  exit 1
fi
echo 'overlay-safe staging OK'
shopt -s nullglob
legacy_helpers=(autosourcarr-v*-compose*.yml)
if ((${#legacy_helpers[@]})); then
  echo "Versioned Compose helper files must not ship in new releases: ${legacy_helpers[*]}" >&2
  exit 1
fi
grep -q 'cleanup-old-helper-files.sh' extension/build-in-container.sh
echo 'stable Compose-helper naming + automatic legacy cleanup OK'

step 9 'Browse UI + exact TrueNAS env paths'
python - <<'PYUI'
from pathlib import Path
from html.parser import HTMLParser
import re
class Elements(HTMLParser):
    def __init__(self):
        super().__init__(); self.by_id = {}
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if attrs.get('id'): self.by_id[attrs['id']] = (tag, attrs)
    handle_startendtag = handle_starttag
html = Path('autosourcarr/app/static/index.html').read_text()
nodes = Elements(); nodes.feed(html)
def classes(id): return set(nodes.by_id[id][1].get('class', '').split())
def section_start(id): return re.search(r'<section\b[^>]*\bid="'+re.escape(id)+r'"', html).start()
order = [html.index(f'data-tab="{name}"') for name in ('subbed','browse','imports','matrix','duplicates','mixed-entries','manual-actions','tasks','sources','attention','statistics','settings','about')]
assert order == sorted(order), 'Reimagined sidebar must retain every core workspace in its reviewed order'
assert {'browseInspectorBackdrop','workbenchDrawer'} <= classes('browseInspectorBackdrop') and 'hidden' in nodes.by_id['browseInspectorBackdrop'][1], 'Browse details must retain the initially closed scroll-stable workbench'
assert all(x in html for x in ('id="browsePageSizePreset"','id="browsePageSize"','id="applyBrowsePageSize"')), 'Browse quick/custom entries-per-page controls are missing'
assert {'tab','active'} <= classes('subbed') and 'active' not in classes('browse'), 'Manga library must be the experimental default'
assert 'data-tab="settings"' in html and 'class="settingsNavButton"' in html, 'Settings navigation is missing'
assert re.findall(r'data-tab="([^"]+)"', html)[-1] == 'about', 'System must remain the last sidebar destination'
assert {'riLibraryGrid','riSidebar','riSearchDialog','riStatisticsMetrics'} <= nodes.by_id.keys(), 'Reimagined functional surfaces are missing'
assert html.index('id="health"') < html.index('</header>'), 'Errors must remain visible in the header'
assert {'tab','settingsTab'} <= classes('settings')
status_block = html[section_start('about'):section_start('imports')]
imports_block = html[section_start('imports'):section_start('tasks')]
assert 'backupImportFile' not in status_block, 'backup upload must not remain on the Status tab'
assert 'id="backupImportFile"' in imports_block and 'id="importBackup"' in imports_block, 'backup upload controls must live on the Imports tab'
assert 'id="errorPanel"' in html and {'pill','healthButton'} <= classes('health'), 'clickable health diagnostics panel is missing'
assert 'id="duplicateManagerModal"' in html and 'id="resetCovers"' in html, 'duplicate manager or Reset covers action missing'
assert 'data-tab="manual-actions"' in html and 'id="manualGroupsList"' in html, 'Manual actions tab is missing'
manual_block = html[section_start('manual-actions'):section_start('sources')]
assert 'id="resetManualGroups"' in manual_block and 'id="resetManualMatches"' in manual_block, 'manual persistence reset controls are missing'
assert 'id="syncStatusSources"' not in status_block and 'id="scanAdditionalRecords"' not in status_block and 'id="unmatchDuplicates"' not in status_block, 'Status still duplicates controls owned by other tabs'
imports_block = html[section_start('imports'):section_start('tasks')]
assert 'Has possible match' in imports_block and 'No possible match' in imports_block, 'Imports possible-match review filters are missing'
assert 'Source reference found' in imports_block and 'Has English hint' in imports_block, 'Imports source/translation review filters are missing'
js = Path('autosourcarr/app/static/app.js').read_text()
assert 'importPassesReviewFilter' in js and 'importReviewFilter' in js, 'Imports review-filter behavior is missing'
for required in ('/api/admin/browse','/api/admin/maintenance/unmatch-duplicates','/api/admin/maintenance/reset-database','splitDuplicateRecords','duplicateRuleBadge','page_size','browsePageSizePreset','applyBrowsePageSize','browseInspectorBackdrop','browseState.scrollY','window.scrollTo(0,y)','/api/admin/maintenance/refresh-metadata','/api/admin/maintenance/reset-covers','/api/admin/entries/${id}/refresh-covers','duplicateManagerModal','duplicateBrowserGrid','subscriptionStatsFor','downloaded_chapter_count','chapter_count','apiV1().getSettings()','saveEnvSettings','<details class=\"settingsGroup','if(tab===\'matrix\'){if(matrixData)renderMatrix','Find missing + extra source records','cataloged_entries||0)>0){matrixData=null','/preferred-title','Set main title',"entryDetailSection('titles','Alternate titles'",'applyMatchBrowserEntry','AUTO ELIGIBLE','matchBrowserState.results=results','.getErrors()','runLiveEvents()','openErrors','loadErrors'):
    assert required in js, required
assert '<h3>All known titles</h3>' not in js, 'Inspector should separate canonical main title from alternate titles'
assert 'refreshAfterManualEdit' not in js, 'manual matching must update the open browser in-place instead of reloading both matrices after every click'
assert "document.body.classList.add('modalOpen')" not in js[js.index('window.openBrowseEntry'):js.index('window.refreshBrowseEntryMetadata')], 'Browse drawer must not lock body scrolling'
assert 'renderBrowse();\n  const backdrop' not in js[js.index('window.openBrowseEntry'):js.index('window.refreshBrowseEntryMetadata')], 'Opening Browse details must not re-render the grid'
# Status/diagnostics must stay on the cheap path introduced in 0.2.35.
main = Path('autosourcarr/app/main.py').read_text()
service = Path('autosourcarr/app/service.py').read_text()
health_block = main[main.index('@app.get("/api/v1/health")'):main.index('@app.get("/api/v1/sources")')]
assert 'service.duplicate_groups(' not in health_block, 'health must not rebuild duplicate groups'
assert 'cached_library_status' in health_block, 'health must use cached library metrics'
assert 'duplicate_snapshot = self._duplicate_snapshot' in service, 'status must use completed duplicate snapshot only'
assert 'schedule_duplicate_snapshot_refresh' in service, 'background duplicate snapshot scheduler missing'
# Identity safety: normal read/ingest paths may attach one unowned source record but
# must not merge two existing catalog identities. Merging remains an explicit
# duplicate-review/Auto-merge operation.
ingest_block = service[service.index('    def ingest_manga('):service.index('    def _entry_summaries(', service.index('    def ingest_manga('))]
assert 'self.merge_entries(' not in ingest_block, 'browse ingestion must not merge existing entries'
assert 'ambiguous-local-candidates:new-entry' in ingest_block, 'ambiguous ingest must create a reviewable separate row'
matrix_start = main.index('async def admin_matrix(')
matrix_end = main.index('\n@app.', matrix_start)
matrix_block = main[matrix_start:matrix_end]
assert 'schedule_missing_source_discovery' not in matrix_block, 'opening/filtering Matrix must not launch source-host discovery'
missing_block = service[service.index('    async def discover_missing_source_matches('):service.index('    async def discover_all_source_records(')]
assert 'background=True' in missing_block, 'automatic source discovery must use bounded background search mode'
assert '_record_source_result' in missing_block, 'automatic source discovery must feed failures into source circuit breaker'
print('browse/subbed/settings/status/identity hot-path UI checks OK')
PYUI
grep -q 'v0.2.18: denser Match Matrix' autosourcarr/app/static/style.css
grep -q 'AUTO_MISSING_SOURCE_DISCOVERY=true' config/autosourcarr.env.example
grep -q 'discover_missing_source_matches' autosourcarr/app/service.py
grep -q 'Verified candidate belongs to another catalogue entry' autosourcarr/app/service.py
grep -q 'blocked_candidates' autosourcarr/app/service.py
grep -q 'Generated translations are intentionally transient search hints' autosourcarr/app/service.py
grep -q 'title_type not in {\"description-alias\", \"matched-alias\", \"manual\"}' autosourcarr/app/service.py
grep -q '_split_comma_aliases' autosourcarr/app/matcher.py
grep -q '_split_top_level' autosourcarr/app/matcher.py
grep -q 'matrixSourceColWidth' autosourcarr/app/static/app.js
grep -q 'table-layout:fixed' autosourcarr/app/static/style.css
grep -q 'content-visibility:visible;contain:none' autosourcarr/app/static/style.css
grep -q 'matchBrowserEnabledSources' autosourcarr/app/static/app.js
grep -q 'if(matchSearchAbort)matchSearchAbort.abort()' autosourcarr/app/static/app.js
grep -q 'Placeholder covers are deliberately not retried by the browser' autosourcarr/app/static/app.js
if grep -q '_cover_retry' autosourcarr/app/static/app.js; then echo 'Browser cover retry storm regression returned.' >&2; exit 1; fi
grep -q 'COVER_INLINE_WAIT=0.35' config/autosourcarr.env.example
grep -q 'COVER_FETCH_CONCURRENCY=4' config/autosourcarr.env.example
grep -q 'Rebuild source-owned aliases from the original raw source description' autosourcarr/app/service.py
EXPECTED_AUTO='/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/autosourcarr.env'
EXPECTED_HOST='/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/source-host.env'
EXPECTED_CONFIG='/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config:/config'
for f in docker-compose.truenas.yml examples/manga-cluster-autosourcarr-services.yml compose-env-file.yml; do
  grep -Fq "$EXPECTED_AUTO" "$f"
  # Both persistent env files are read through the unchanged /config directory mount.
done
for f in docker-compose.truenas.yml examples/manga-cluster-autosourcarr-services.yml compose-env-file.yml; do
  grep -Fq "$EXPECTED_CONFIG" "$f"
done
grep -q 'SETTINGS_CONFIG_DIR=/config' config/autosourcarr.env.example
grep -q 'SETTINGS_CONFIG_DIR=/config' config/autosourcarr.env.example
grep -q 'downloadCount chapters { totalCount }' autosourcarr/app/suwayomi.py
grep -q 'promoteVariantPrimary' autosourcarr/app/static/app.js
grep -q 'Latest ch.' autosourcarr/app/static/app.js
grep -q 'missing_chapter_count' autosourcarr/app/service.py
grep -q 'best_automatic_title_score' autosourcarr/app/service.py
grep -q 'data-tab="tasks"' autosourcarr/app/static/index.html
grep -q 'taskNavBadge' autosourcarr/app/static/app.js
grep -q '/api/admin/tasks' autosourcarr/app/main.py
grep -Fq 'app.include_router(tasks_router(admin_guard, lambda: task_registry))' autosourcarr/app/main.py
grep -Fq "prefix='/api/admin/tasks'" autosourcarr/app/repair_tasks.py
grep -Fq "@router.post('/{task_id}/cancel')" autosourcarr/app/repair_tasks.py
grep -q '/api/admin/errors' autosourcarr/app/main.py
grep -q 'source-host-pages' autosourcarr/app/main.py
grep -q 'tasksRunningList' autosourcarr/app/static/index.html
grep -q 'tasksCompletedList' autosourcarr/app/static/index.html
grep -q 'tasksFailedList' autosourcarr/app/static/index.html
grep -q 'cancelTask' autosourcarr/app/static/app.js
grep -q 'autoMergePreviewModal' autosourcarr/app/static/index.html
grep -q '/api/admin/maintenance/consolidate-preview' autosourcarr/app/static/app.js
grep -q 'Confirm and merge' autosourcarr/app/static/app.js
grep -q 'Downloaded chapters' autosourcarr/app/static/app.js
grep -q 'Downloaded size' autosourcarr/app/static/app.js
grep -q 'Catalogued entries' autosourcarr/app/static/app.js
grep -q 'MAIN_SUWAYOMI_DOWNLOADS_PATH' config/autosourcarr.env.example
grep -q 'MAIN_SUWAYOMI_DOWNLOADS_PATH' autosourcarr/app/env_settings.py
grep -q 'automatic_title_similarity' autosourcarr/app/matcher.py
grep -q 'check-chapters' autosourcarr/app/main.py
grep -q 'No chapters found' autosourcarr/app/static/app.js
grep -q 'source_record_scans' autosourcarr/app/db.py
grep -q 'entry_chapter_stats' autosourcarr/app/service.py
grep -q 'entryChapterStatsHtml' autosourcarr/app/static/app.js
grep -q 'exact_local_match' autosourcarr/app/service.py
grep -q 'matched-alias' autosourcarr/app/service.py
grep -q '_mutation_task_name' autosourcarr/app/main.py
grep -q '/api/admin/import/mihon-backup' autosourcarr/app/main.py
grep -q 'decode_backup_file' autosourcarr/app/backup_import.py
grep -q 'set_manga_in_library' autosourcarr/app/suwayomi.py
grep -q 'Import Mihon/Tachiyomi backup' autosourcarr/app/service.py
grep -q 'backupImportFile' autosourcarr/app/static/index.html
grep -q 'uploadMihonBackup' autosourcarr/app/static/app.js
grep -q 'data-tab="imports"' autosourcarr/app/static/index.html
grep -q 'importRunSelect' autosourcarr/app/static/index.html
grep -q '/api/admin/imports' autosourcarr/app/main.py
grep -q 'import_runs' autosourcarr/app/db.py
grep -q 'import_items' autosourcarr/app/db.py
grep -q 'manual_match_bindings' autosourcarr/app/db.py
grep -q 'manual_match_groups' autosourcarr/app/db.py
grep -q '_parse_backup_source' autosourcarr/app/backup_import.py
grep -q 'sourceName' autosourcarr/app/backup_import.py
grep -q 'matchImportItem' autosourcarr/app/static/app.js
grep -q 'retryImportItem' autosourcarr/app/static/app.js
grep -q 'skipImportItem' autosourcarr/app/static/app.js
grep -q 'title_media_variant' autosourcarr/app/matcher.py
grep -q 'manga/webtoon adaptations are separate works' autosourcarr/app/service.py
grep -q 'grid-template-columns:repeat(3,minmax(0,1fr))' autosourcarr/app/static/style.css
grep -q '/mnt/hdd/storage/komga/manga:/mnt/manga-downloads:ro' docker-compose.truenas.yml
grep -q '/mnt/hdd/storage/komga/manga:/mnt/manga-downloads:ro' compose-env-file.yml
grep -q '^MAIN_SUWAYOMI_DOWNLOADS_PATH=/mnt/manga-downloads$' config/autosourcarr.env.example
echo 'browse/subbed UI, settings env editor, main-Suwayomi chapter stats, and TrueNAS config mount OK'
shopt -s nullglob
obsolete_docs=()
for f in UPGRADE-*.md REGRESSION-AUDIT*.md autosourcarr-v*-upgrade*.md autosourcarr-v*-regression-audit*.md; do [[ -e "$f" ]] && obsolete_docs+=("$f"); done
for f in OVERLAY-UPDATES.md RENAME-TO-AUTOSOURCARR.md OPTIMIZATION-REPORT.md; do [[ -e "$f" ]] && obsolete_docs+=("$f"); done
if ((${#obsolete_docs[@]})); then echo "Obsolete one-off docs must not ship: ${obsolete_docs[*]}" >&2; exit 1; fi
for version in 0.1.0 0.1.1 0.1.2 0.1.3 0.1.4 0.1.5 0.1.6 0.1.7 0.1.8 0.1.9 0.2.0 0.2.1 0.2.2 0.2.3 0.2.4 0.2.5 0.2.6 0.2.7 0.2.8 0.2.9 0.2.10 0.2.11 0.2.12 0.2.13 0.2.14 0.2.15 0.2.16 0.2.17 0.2.18 0.2.19 0.2.20 0.2.21 0.2.22 0.2.23 0.2.24 0.2.25 0.2.26 0.2.27 0.2.28 0.2.29 0.2.30 0.2.31 0.2.32; do grep -q "^## $version\b" CHANGELOG.md || { echo "CHANGELOG missing $version" >&2; exit 1; }; done
grep -q 'Every future AutoSourcarr release must add a new version section here' CHANGELOG.md
test "$(grep -c '^## 0.4.5 - ' CHANGELOG.md)" -eq 1 || { echo 'CHANGELOG must contain exactly one 0.4.5 section' >&2; exit 1; }
echo 'single CHANGELOG policy + complete version history OK'

step 10 'Docker validation'
if command -v docker >/dev/null 2>&1; then
  docker compose config >/dev/null
  echo 'docker compose config OK'
else
  echo 'SKIP: docker executable is not installed in this environment'
fi

echo 'Project checks completed.'
