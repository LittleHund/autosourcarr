"""Explicit read-only engine capability checks; promotion requires native proof."""
from .db import now_iso

BASELINE='2.3.2243'
TARGET='2.4.2366'


def status(database):
    return {'baseline':BASELINE,'target':TARGET,'pin_promoted':False,'native_qualified':False,
            'event_mode':'authoritative polling','event_policy':'No event payload is treated as a complete library or queue snapshot. Omission/reconnect requires a full reconciliation.',
            'last_check':database.setting('reimagined_engine_contract',{})}


async def check(database,client,target):
    if client is None:raise ValueError('Configure this engine first')
    about=await client.client.get(client.base_url+'/api/v1/settings/about')
    about.raise_for_status();payload=about.json()
    if not isinstance(payload,dict):raise ValueError('Engine about endpoint returned an incomplete response')
    version=payload.get('version') or payload.get('serverVersion')
    if not isinstance(version,str) or not version or len(version)>100:raise ValueError('Engine did not identify its version')
    data=await client.graphql('query ReimaginedEngineContract{__schema{mutationType{fields{name args{name}}} subscriptionType{fields{name args{name}}}}}')
    schema=data.get('__schema')
    if not isinstance(schema,dict) or not isinstance(schema.get('mutationType'),dict) or not isinstance(schema['mutationType'].get('fields'),list):raise ValueError('Engine capability introspection is unavailable')
    fields=schema['mutationType']['fields']
    if any(not isinstance(r,dict) or not isinstance(r.get('name'),str) for r in fields):raise ValueError('Invalid engine capability fields')
    names={r['name'] for r in fields}
    required={'updateManga','enqueueChapterDownloads','fetchMangaAndChapters','fetchChapterPages'}
    report={'checked_at':now_iso(),'engine':target,'version':version,
            'required_mutations':{name:name in names for name in sorted(required)},
            'batch_queue_reorder':'reorderChapterDownloads' in names,
            'single_queue_reorder':'reorderChapterDownload' in names,
            'contract_ok':required<=names,'native_qualified':False,
            'limit':'API shapes only. No APK/JAR build, migration, restart, download, event reconnect or native upgrade/rollback was performed.'}
    database.set_setting('reimagined_engine_contract',report)
    return report
