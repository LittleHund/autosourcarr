"""Raw-first repair orchestration. The runner owns isolation and live promotion
remains exclusively in the application controller.
"""
import io
import json
from pathlib import Path
import re
import zipfile

import httpx

from baseline_manifest import manifest_contract
from contracts import digest, safe_text, source_identity, tree_manifest
from reconstruction import loading_candidates, validation_envelope
from source_tools.archive import RawStore, metadata
from validate import SourceBlocked
from address_policy import AddressPolicy
from website_probe import public_url


def cold_baseline(runner,job):
    directory=runner.path(job['id']);baseline=(directory/'baseline.jar').read_bytes()
    if digest(baseline)!=job['baseline_sha256']:raise SourceBlocked('Saved installed binary changed; repair stopped')
    expected=job['installed_sources']
    with zipfile.ZipFile(io.BytesIO(baseline)) as archive:has_manifest='AndroidManifest.xml' in archive.namelist()
    if has_manifest:
        identity=manifest_contract(baseline)
        if identity[0]!=job['pkg_name']:raise SourceBlocked('Installed manifest package differs from the selected source')
        job.update(baseline_contract_basis='native-manifest',baseline_format='native-jar')
        loaded,_=runner.inspect_artifact(job,baseline,expected=expected,functional=False)
        envelope=baseline
    elif (directory/'source.apk').is_file():
        envelope,identity=runner.converted_baseline(job,baseline)
        loaded,_=runner.inspect_artifact(job,envelope,expected=expected,functional=False)
    else:
        runner.save(job,'checking-bytecode','Deriving loading candidates from the exact installed bytecode; no original-file equivalence is assumed.')
        candidates=loading_candidates(baseline,job['pkg_name']);matches=[]
        job.update(baseline_contract_basis='cold-import-installed-jar',baseline_metadata_basis='installed-bytecode',
                   baseline_format='converted-apk-jar')
        for entrypoint in candidates:
            runner.check_cancelled(job)
            envelope=validation_envelope(baseline,pkg=job['pkg_name'],entrypoint=entrypoint,
                version=job['installed_version'],library=job['installed_library'])
            try:loaded,_=runner.inspect_artifact(job,envelope,expected=expected,functional=False)
            except (ValueError,RuntimeError,httpx.HTTPError):continue
            matches.append((envelope,(job['pkg_name'],entrypoint,job['installed_library']),loaded))
        if len(matches)!=1:raise SourceBlocked('Exact installed bytecode did not produce one matching cold-import identity. Raw backup is unchanged; provide original source/loading metadata.')
        envelope,identity,loaded=matches[0]
    job['baseline_contract']={'package':identity[0],'entrypoint':identity[1],'library':identity[2]}
    job['baseline_import_proof']={'verified':True,'installed_jar_sha256':digest(baseline),
        'validation_envelope_sha256':digest(envelope),'source_identity_sha256':digest(json.dumps(source_identity(loaded)).encode()),
        'basis':job['baseline_contract_basis']}
    (directory/'validation-baseline.jar').write_bytes(envelope)
    return envelope,identity,loaded


def export_tree(directory,info):
    """Archive only source inputs, never build output or a job's private state."""
    output=io.BytesIO();root=Path(directory)
    before=tree_manifest(root)
    with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(before):
            if any(part in {'.gradle-cache','.gradle','build','.git'} for part in Path(name).parts):continue
            if info['module'].startswith('src/') and name.startswith('src/') and not name.startswith(info['module']+'/'):continue
            archive.write(root/name,name)
    payload=output.getvalue()
    # Enforce the same code-only archive boundary used for downloaded inputs.
    return payload


def save_code(runner,job,original,*,candidate=False):
    directory=runner.path(job['id']);info=dict(job['raw_meta'])
    if candidate:info.update(kind='repaired',parent=job['raw_id'])
    if candidate and info['recipe']=='jvm':info['overlay_sources']=sorted(set(info.get('overlay_sources',[]))|set(job.get('changed_files',[])))
    payload=export_tree(original,info)
    store=RawStore(directory/'raw-store');snapshot=store.put_zip(payload,metadata(info))
    name='candidate-raw.zip' if candidate else 'raw.zip'
    (directory/name).write_bytes(store.export_zip(job['pkg_name'],snapshot['id']))
    key='candidate_raw_sha256' if candidate else 'raw_sha256'
    job[key]=digest((directory/name).read_bytes())
    job['candidate_raw_meta' if candidate else 'raw_meta']=metadata(info)
    job['candidate_raw_id' if candidate else 'raw_id']=snapshot['id']
    return snapshot


def patch_build_addresses(original,module,targets):
    """Only literal baseUrl values in existing source DSL/build metadata.

    The controller performs this narrow substitution. Models still cannot edit
    build scripts, dependencies, signing or arbitrary Gradle code.
    """
    root=Path(original) if module=='.' else Path(original)/module;changes=[]
    for name in ('build.gradle.kts','build.gradle'):
        path=root/name
        if not path.is_file():continue
        text=path.read_text(encoding='utf-8');before=text
        pattern=re.compile(r'(\bbaseUrl\s*=\s*)(["\'])(https?://[^"\'\s]+)(\2)')
        def replace(match):
            try:old=public_url(match[3])
            except (ValueError,UnicodeError):return match[0]
            matches={public_url(row['new_url']) for row in targets if row.get('previous_url') and public_url(row['previous_url'])==old}
            if len(matches)!=1:return match[0]
            new=next(iter(matches))
            if not new.startswith('https://') or re.search(r'["\'\\$`{}]',new):raise SourceBlocked('New address is not a safe literal source baseUrl')
            return match[1]+match[2]+new+match[2]
        text=pattern.sub(replace,text)
        if text!=before:
            path.write_text(text,encoding='utf-8');changes.append(path.relative_to(original).as_posix())
    return changes


def retain_recovery_code(runner,job,original,candidate,artifact):
    from baseline_manifest import read_manifest,NS
    version=read_manifest(artifact).get(NS+'versionName')
    job['raw_meta']={'package':job['pkg_name'],'version':version,'module':job['module'],'ecosystem':'keiyoushi',
        'recipe':'gradle','kind':'upstream','origin':{'repository':'https://github.com/keiyoushi/extensions-source',
        'revision':job['source_revision'],'basis':'known-repository'}}
    save_code(runner,job,original);save_code(runner,job,candidate,candidate=True)


def run_raw(runner,job):
    directory=runner.path(job['id'])
    if not callable(getattr(runner,'require_isolation',None)):raise SourceBlocked('Raw-first repairs require the integrated per-instance repair runtime')
    runner.require_isolation()
    original=directory/'original'
    if job.get('reconstruct'):
        baseline=(directory/'baseline.jar').read_bytes()
        if digest(baseline)!=job['baseline_sha256']:raise SourceBlocked('Installed binary backup changed; reconstruction stopped')
        runner.save(job,'reconstructing','Original raws were marked unavailable. Decompiling every installed class and retaining resources in a private workspace.')
        runner.reconstruct_code(job,baseline,original)
        job['raw_meta']={'package':job['pkg_name'],'version':job['installed_version'],'module':'.',
            'ecosystem':'tachiyomi','recipe':'jvm','kind':'reconstructed',
            'origin':{'basis':'binary-reconstruction'}}
        if job.get('reconstruction_origin'):
            job['raw_meta'].update(job['reconstruction_origin'])
        job['module']='.'
        save_code(runner,job,original)
        if not job.get('raw_only'):
            baseline,identity,expected=cold_baseline(runner,job)
            job['raw_meta']['extension_lib']=identity[2]
            save_code(runner,job,original)
    else:
        raw=(directory/'raw.zip').read_bytes()
        if digest(raw)!=job.get('raw_sha256'):raise SourceBlocked('Saved raw-code input changed after admission')
        store=RawStore(directory/'raw-store');snapshot=store.put_zip(raw,job['raw_meta'])
        job['raw_id']=snapshot['id'];store.materialize(job['pkg_name'],snapshot['id'],original)
        runner.save(job,'raws-acquired','Original ecosystem code pinned in this job; checking website and API addresses.')
        baseline,identity,expected=cold_baseline(runner,job)
    if job.get('raw_only'):
        runner.save(job,'raws-retained','Reconstructed code retained privately. It is derived code; no working version was asserted or installed.')
        return
    job['expected_sources']=expected;job['expected_ids']=[str(row['id']) for row in expected]
    runner.save(job,'checking-website','Checking the original website and API observations, then researching changed locations.')
    job['website_checks']=runner.check_websites(job,expected)
    if any(row.get('state') in {'verification','rate_limited'} for row in job['website_checks']):
        raise SourceBlocked('Website requires human verification or is rate limited. The code was retained; no automatic retry or bypass was attempted.')
    research=getattr(runner,'research_addresses',None)
    if research:research(job,expected,original)
    policy=AddressPolicy(job,lambda rows:runner.check_websites(job,rows))
    for source in expected:
        try:policy._redirect(source,public_url(source['baseUrl']))
        except (KeyError,ValueError,UnicodeError):continue
    job['controlled_address_changes']=patch_build_addresses(original,job['module'],job.get('address_targets',[]))
    runner.save(job,'checking-baseline','Testing the saved installed code separately from its cold-import proof.')
    try:
        _,checks=runner.inspect_artifact(job,baseline,expected=expected)
        job['baseline_checks']=checks
    except SourceBlocked as exc:
        can_build=job['raw_meta']['recipe']=='gradle' or bool(job['raw_meta'].get('overlay_sources'))
        evidence=any(row.get('basis')=='original-maintainer-code' for row in job.get('address_targets',[]))
        if 'Source transport failed' not in str(exc) or not (can_build or evidence):raise
        # An inconclusive old transport does not prove a parser bug. It also
        # should not prevent independently building/testing acquired code. A
        # further inconclusive candidate still blocks before any AI edit.
        job['baseline_failure']=safe_text(exc,2000)+' Old transport remains inconclusive; acquired code must pass its independent checks.'
    except (ValueError,RuntimeError,httpx.HTTPError) as exc:
        job['baseline_failure']=safe_text(exc,2000)
    candidate_tree=original
    try:
        if job['raw_meta']['recipe']=='jvm' and not job['raw_meta'].get('overlay_sources'):
            # Decompilation does not promise a clean whole-project recompile.
            # Keep exact installed class bytes until a bounded code patch exists.
            raise ValueError('Reconstructed installed code needs the requested functional correction: '+job.get('baseline_failure',job['issue']))
        if job['raw_meta']['recipe']=='jvm':job['changed_files']=job['raw_meta']['overlay_sources'][:]
        runner.save(job,'checking-upstream','Building the acquired original ecosystem code before requesting AI edits.')
        artifact=runner.build(job,original,'upstream-build')
        if manifest_contract(artifact)!=identity:
            raise SourceBlocked('Acquired code builds a different entrypoint/library. Provide matching historical raws or choose Unavailable; automatic ecosystem migration was refused.')
        source_revision=job['raw_meta'].get('origin',{}).get('revision','')
        unchanged=(job['raw_meta']['kind'] in {'upstream','provided'} and bool(re.fullmatch(r'[0-9a-f]{40}',source_revision))
                   and not job.get('controlled_address_changes'))
        _,checks=runner.inspect_artifact(job,artifact,expected=expected,official=unchanged)
        kind='original-raw-build'
    except SourceBlocked:raise
    except (ValueError,RuntimeError,httpx.HTTPError) as failure:
        runner.save(job,'repairing','Requesting one bounded correction using retained code, website/API evidence and previous feedback.')
        candidate_tree=runner.edit(job,original,failure)
        if job['raw_meta']['recipe']=='jvm':job['changed_files']=sorted(set(job.get('changed_files',[]))|set(job['raw_meta'].get('overlay_sources',[])))
        artifact=runner.build(job,candidate_tree,'repair-build')
        if manifest_contract(artifact)!=identity:raise SourceBlocked('Repair changed installed package/entrypoint/library identity')
        runner.save(job,'validating','Independently testing catalog, search, metadata, chapters, covers and page images.')
        _,checks=runner.inspect_artifact(job,artifact,expected=expected)
        kind='raw-code-repair'
    runner.check_cancelled(job)
    save_code(runner,job,candidate_tree,candidate=True)
    (directory/'candidate.jar').write_bytes(artifact)
    runner.save(job,'ready','Compiled repair passed functional samples. The instance will install this requested repair after final identity/address checks.',
        candidate_sha256=digest(artifact),candidate_kind=kind,checks=checks,validation_time=__import__('time').time())
