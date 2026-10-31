"""Stage corresponding source addresses; every live change still needs review.

A redirect is evidence of a destination, not proof of ownership. Candidates also
retain package/source identity, pass content checks and require explicit address
review at installation. Unknown destinations never become an automatic mirror.
"""
import re
from urllib.parse import urlsplit
import json

from website_probe import origin, public_url


class AddressBlocked(RuntimeError):
    pass


class AddressNeedsUpdate(ValueError):
    pass


class AddressPolicy:
    def __init__(self, job, probe):
        self.job,self.probe=job,probe

    def _remember(self, sid, previous, target, basis):
        target=public_url(target)
        if not target.startswith('https://'):
            raise AddressBlocked('A changed website address must use HTTPS')
        rows=self.job.setdefault('address_targets',[])
        item={'source_id':str(sid),'previous_url':previous,'new_url':target,'basis':basis}
        if basis=='maintained-source-revision':item['source_revision']=self.job['source_revision']
        if item not in rows and len(rows)<200:rows.append(item)
        return item

    def _redirect(self, source, previous):
        sid=str(source['id'])
        for row in self.job.get('website_checks',[]):
            if row.get('state')!='working' or sid not in row.get('source_ids',[]):continue
            try:
                chain=[public_url(row['requested_url']),*[public_url(url) for url in row.get('redirects',[])]]
                final=public_url(row['final_url'])
                secure=all(url.startswith('https://') for url in chain)
                upgrade=final.startswith('https://') and all(urlsplit(url).hostname==urlsplit(previous).hostname for url in chain)
                if origin(chain[0])==origin(previous) and (secure or upgrade) and origin(final)!=origin(previous):
                    return self._remember(sid,previous,final,'observed-https-redirect' if secure else 'same-host-https-upgrade')
            except (ValueError,KeyError,UnicodeError):continue
        return None

    def check(self, sources, expected, *, official=False):
        previous={str(row['id']):row for row in expected or []}
        pairs=[]
        for source in sources:
            sid=str(source['id']);old=str(previous.get(sid,{}).get('baseUrl') or '')
            try:new=public_url(str(source.get('baseUrl') or ''))
            except (ValueError,UnicodeError):raise AddressBlocked('Candidate has no supported public website address') from None
            if not old:
                # First-time orphan installation has no existing address to migrate.
                if self.job.get('recovery'):continue
                raise AddressBlocked('Existing website address is unavailable; no address migration was inferred')
            try:old=public_url(old)
            except (ValueError,UnicodeError):raise AddressBlocked('Existing website address cannot be verified safely') from None
            redirected=self._redirect(source,old)
            if redirected and origin(new)==origin(old):
                raise AddressNeedsUpdate('Source still declares '+old+' but its checked HTTPS website redirects to '+redirected['new_url']+'. Update the corresponding base address and API paths before validation.')
            if origin(old)!=origin(new):pairs.append((source,old,new))
        if official and pairs:
            if not re.fullmatch(r'[0-9a-f]{40}',str(self.job.get('source_revision',''))):
                raise AddressBlocked('Changed official addresses require the pinned source revision')
            observations=self.probe([source for source,_,_ in pairs])
            self.job['website_checks']=(self.job.get('website_checks',[])+observations)[-16:]
            for source,old,new in pairs:
                row=next((row for row in observations if str(source['id']) in row.get('source_ids',[]) and row.get('state')=='working'),None)
                if row is None:raise AddressBlocked('The maintained source declares a new website, but its public availability is inconclusive or requires verification')
                final=public_url(row['final_url'])
                self._remember(source['id'],old,final,'maintained-source-revision')
                if origin(final)!=origin(new):
                    raise AddressNeedsUpdate('Maintained source address '+new+' redirects to '+final+'. Match its current website and API addresses before validation.')
        changes=[]
        for source,old,new in pairs:
            target=next((row for row in self.job.get('address_targets',[]) if row.get('source_id')==str(source['id']) and origin(row['new_url'])==origin(new)),None)
            if target is None:raise AddressBlocked('Changed website does not match the checked corresponding redirect or pinned maintained source; no unrelated replacement was accepted')
            changes.append({**target,'previous_url':old,'new_url':new})
        if changes and self.job.get('raw_first'):
            self.job['automatic_address_trust']=all(row.get('trust')=='corroborated' and row.get('basis')=='original-maintainer-code' for row in changes)
        return changes


def prompt_evidence(job, limit=16000):
    """Keep complete records within the editor argument/conversation budget."""
    result={'website_checks':[],'address_targets':[],'omitted':0,'address_research':job.get('address_research',{})}
    for key in ('website_checks','address_targets'):
        for row in job.get(key,[]):
            result[key].append(row)
            if len(json.dumps(result).encode())>limit:
                result[key].pop();result['omitted']+=1
    return json.dumps(result,separators=(',',':'))
