"""Resolve source origins from evidence, not a familiar package/name alone."""
import re
from urllib.parse import quote, urlsplit

from .archive import module_name, package_name, public_reference

KEIYOUSHI_SOURCE='https://github.com/keiyoushi/extensions-source'
KEIYOUSHI_INDEX='https://github.com/keiyoushi/extensions/raw/repo/index.pb'


def repository_plan(value):
    public_reference(value);parsed=urlsplit(value)
    if parsed.hostname.lower() not in {'github.com','gitlab.com'}:
        raise ValueError('Provide the original project ZIP for this repository host; no unrelated repository was guessed')
    parts=parsed.path.strip('/').split('/')
    if not parts or any(not re.fullmatch(r'[A-Za-z0-9_.-]+',part) or part in {'.','..'} for part in parts):
        raise ValueError('Enter the original source repository root, not a file or ambiguous path')
    if parts[-1].endswith('.git'):parts[-1]=parts[-1][:-4]
    if not parts[-1] or parts[-1] in {'.','..'} or parsed.hostname.lower()=='github.com' and len(parts)!=2 or len(parts)>6:
        raise ValueError('Enter the original source repository root')
    host=parsed.hostname.lower();repository='https://'+host+'/'+'/'.join(parts)
    if host=='github.com':
        resolve='https://api.github.com/repos/'+'/'.join(parts)+'/commits/HEAD'
    else:
        resolve='https://gitlab.com/api/v4/projects/'+quote('/'.join(parts),safe='')+'/repository/commits/HEAD'
    return {'repository':repository,'host':host,'path':'/'.join(parts),'resolve_url':resolve}


def repository_archive(plan, revision):
    if not isinstance(revision,str) or not re.fullmatch(r'[0-9a-f]{40}',revision):
        raise ValueError('Resolve the immutable source commit before downloading its raw files')
    checked=repository_plan(plan.get('repository'))
    if checked['host']=='github.com':return 'https://codeload.github.com/'+checked['path']+'/zip/'+revision
    return checked['repository']+'/-/archive/'+revision+'/'+checked['path'].split('/')[-1]+'-'+revision+'.zip'


def module_hint(pkg):
    prefix='eu.kanade.tachiyomi.extension.'
    suffix=pkg.removeprefix(prefix)
    if pkg.startswith(prefix) and re.fullmatch(r'[a-z0-9]+\.[a-z0-9_]+',suffix):
        return 'src/'+suffix.replace('.','/')
    return '.'


def _known_index(value):
    if not isinstance(value,str) or len(value)>2048 or any(ord(c)<33 for c in value):return False
    try:
        parsed=urlsplit(value)
        if parsed.scheme!='https' or parsed.username or parsed.password or parsed.fragment or parsed.port not in {None,443}:return False
        path=parsed.path.lower().strip('/').split('/')
        if parsed.hostname=='raw.githubusercontent.com':
            return len(path)==4 and path[:3]==['keiyoushi','extensions','repo'] and path[-1] in {'index.pb','index.json','index.min.json'}
        if parsed.hostname=='github.com':
            return path[:2]==['keiyoushi','extensions'] and (
                len(path)==5 and path[2:4]==['raw','repo'] or len(path)==7 and path[2:6]==['raw','refs','heads','repo']) and path[-1] in {'index.pb','index.json','index.min.json'}
    except (ValueError,UnicodeError):return False
    return False


def source_origin(extension, *, supplied=None, manual=None):
    if not isinstance(extension,dict):raise ValueError('Source metadata is unavailable')
    pkg=package_name(extension.get('pkgName'))
    version=extension.get('versionName')
    if not isinstance(version,str) or not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}',version):
        raise ValueError('Refresh the installed source version before acquiring raw code')
    result={'package':pkg,'version':version,'module':module_hint(pkg),'ecosystem':'tachiyomi'}
    if supplied is not None:
        if not isinstance(supplied,dict) or set(supplied)-{'repository','module','revision','ecosystem'}:
            raise ValueError('Unsupported source-origin fields')
        plan=repository_plan(supplied.get('repository'))
        revision=supplied.get('revision') or ''
        if not isinstance(revision,str) or revision and not re.fullmatch(r'[0-9a-f]{40}',revision):
            raise ValueError('The optional original source revision must be a full commit hash')
        ecosystem=supplied.get('ecosystem','tachiyomi')
        if not isinstance(ecosystem,str) or ecosystem not in {'tachiyomi','keiyoushi','mihon','suwayomi'}:
            raise ValueError('Unsupported source ecosystem')
        result.update(state='known',repository=plan['repository'],module=module_name(supplied.get('module',result['module'])),
                      revision=revision,ecosystem=ecosystem,basis='user-provided')
        return result
    if manual is True or manual is None and extension.get('orphan_reason')=='manual-upload':
        return {**result,'state':'needs-input','reason':'This source was installed locally. Provide its original raw project or choose Unavailable to reconstruct the actual installed code.'}
    if _known_index(extension.get('storeIndexUrl')):
        return {**result,'state':'known','repository':KEIYOUSHI_SOURCE,'revision':'','ecosystem':'keiyoushi',
                'index_url':KEIYOUSHI_INDEX,'basis':'known-repository'}
    return {**result,'state':'needs-input','reason':'The original code repository is not declared by this source. Provide its project ZIP/repository or choose Unavailable; no similar source was substituted.'}
