"""Derive bounded loading candidates from real bytecode, never module names.

This does not prove that a candidate loads. The exact saved payload is wrapped
only for an independent cold import and comparison with installed source IDs,
names, languages and website addresses. Original rollback bytes stay unchanged.
"""
import io
import re
import struct
import xml.etree.ElementTree as ET
import zipfile

from baseline_manifest import _payloads, NS
from source_tools.archive import package_name


def class_header(data):
    if not isinstance(data,bytes) or not 24<=len(data)<=8*1024*1024:raise ValueError('Invalid class input')
    pos=0
    def take(size):
        nonlocal pos
        if size<0 or pos+size>len(data):raise ValueError('Truncated class file')
        result=data[pos:pos+size];pos+=size;return result
    def u2():return struct.unpack('>H',take(2))[0]
    def u4():return struct.unpack('>I',take(4))[0]
    if take(4)!=b'\xca\xfe\xba\xbe':raise ValueError('Invalid class magic')
    u2();major=u2()
    if not 45<=major<=70:raise ValueError('Unsupported class version')
    count=u2();pool=[None]*count;index=1
    while index<count:
        tag=take(1)[0]
        if tag==1:pool[index]=take(u2())  # MUTF8 metadata stays opaque.
        elif tag in {7,8,16,19,20}:pool[index]=(tag,u2())
        elif tag in {3,4,9,10,11,12,17,18}:take(4)
        elif tag in {5,6}:
            take(8);index+=1
            if index>=count:raise ValueError('Invalid double-slot constant')
        elif tag==15:take(3)
        else:raise ValueError('Unknown class constant')
        index+=1
    def raw_string(index):
        if not 0<index<count or not isinstance(pool[index],bytes):raise ValueError('Invalid class string reference')
        return pool[index]
    decoded={}
    def text(index):
        if index in decoded:return decoded[index]
        raw=raw_string(index)
        if len(raw)>1024:raise ValueError('Class identity exceeds its supported limit')
        try:value=raw.decode('ascii')
        except UnicodeError:raise ValueError('Non-ASCII class/method identity') from None
        decoded[index]=value;return value
    def classname(index):
        if not 0<index<count or not isinstance(pool[index],tuple) or pool[index][0]!=7:raise ValueError('Invalid class reference')
        value=text(pool[index][1])
        if not re.fullmatch(r'[A-Za-z_$][A-Za-z0-9_$]*(?:/[A-Za-z_$][A-Za-z0-9_$]*)*',value):raise ValueError('Invalid class name')
        return value
    access=u2();name=classname(u2());parent=u2();parent=classname(parent) if parent else ''
    interface_count=u2()
    if interface_count>256:raise ValueError('Class interface metadata exceeds its limit')
    interfaces=[classname(u2()) for _ in range(interface_count)]
    def attributes():
        for _ in range(u2()):u2();take(u4())
    for _ in range(u2()):take(6);attributes()
    constructor=False
    for _ in range(u2()):
        flags=u2();method=raw_string(u2());descriptor=raw_string(u2())
        if method==b'<init>' and descriptor==b'()V' and flags&1:constructor=True
        attributes()
    attributes()
    if pos!=len(data):raise ValueError('Trailing class file data')
    return {'name':name,'parent':parent,'interfaces':interfaces,'public':bool(access&1),
            'concrete':not bool(access&(0x0200|0x0400)),'constructor':constructor}


def loading_candidates(payload,pkg):
    package_name(pkg);files=_payloads(payload);headers={}
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        for name in files:
            if not name.endswith('.class') or name.startswith('META-INF/') or name.rsplit('/',1)[-1] in {'module-info.class','package-info.class'}:continue
            try:header=class_header(archive.read(name))
            except ValueError:continue  # Unrelated resources are not loading candidates.
            if name!=header['name']+'.class':continue
            headers[header['name']]=header
    api={'eu/kanade/tachiyomi/source/Source','eu/kanade/tachiyomi/source/SourceFactory',
         'eu/kanade/tachiyomi/source/online/HttpSource','eu/kanade/tachiyomi/source/online/ParsedHttpSource',
         'eu/kanade/tachiyomi/source/online/KeiSource'}
    # Reverse reachability visits each edge once, including malformed cycles.
    # Recursive per-candidate traversal can explode on shared interface graphs.
    children={}
    for row in headers.values():
        for parent in {row['parent'],*row['interfaces']}:children.setdefault(parent,set()).add(row['name'])
    reachable=set(api);queue=list(api)
    for parent in queue:
        for child in children.get(parent,()):
            if child not in reachable:reachable.add(child);queue.append(child)
    choices=[row['name'].replace('/','.') for row in headers.values()
             if row['public'] and row['concrete'] and row['constructor'] and row['name'] in reachable]
    local=[name for name in choices if name.startswith(pkg+'.')]
    choices=local or choices  # A renamed APK may retain its actual old class namespace.
    factories=[name for name in choices if 'eu/kanade/tachiyomi/source/SourceFactory' in headers[name.replace('.','/')]['interfaces']]
    choices=sorted(factories or choices)
    if not 1<=len(choices)<=8:raise ValueError('No bounded unambiguous loading candidates exist in the installed source package')
    return choices


def validation_envelope(payload,*,pkg,entrypoint,version,library):
    package_name(pkg);package_name(entrypoint)
    if entrypoint not in loading_candidates(payload,pkg):raise ValueError('Entrypoint is not supported by installed bytecode')
    if (not isinstance(version,str) or not re.fullmatch(r'[0-9][A-Za-z0-9.+_-]{0,99}',version)
            or library not in {'1.3','1.4','1.5','1.6'}):raise ValueError('Installed compatible library/version metadata is required')
    files=_payloads(payload)
    root=ET.Element('manifest',{'package':pkg,NS+'versionName':version,NS+'versionCode':'1'})
    ET.SubElement(root,'uses-feature',{NS+'name':'tachiyomi.extension'})
    app=ET.SubElement(root,'application')
    ET.SubElement(app,'meta-data',{NS+'name':'tachiyomi.extension.class',NS+'value':entrypoint})
    ET.SubElement(app,'meta-data',{NS+'name':'tachiyomix.extensionLib',NS+'value':library})
    output=io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(payload)) as source,zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as target:
        for info in source.infolist():
            if info.filename!='AndroidManifest.xml':target.writestr(info,source.read(info))
        target.writestr('AndroidManifest.xml',ET.tostring(root,encoding='utf-8',xml_declaration=True))
    return output.getvalue()
