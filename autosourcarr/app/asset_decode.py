"""Bounded decoding for buffered assets. Reader page streams are independent."""
import zlib


async def buffered_asset(response, limit):
    encoding=response.headers.get('content-encoding','identity').strip().lower()
    if encoding not in {'identity','', 'gzip','deflate'}:
        raise ValueError('Unsupported buffered asset content encoding')
    length=response.headers.get('content-length','')
    if length.isdigit() and int(length)>limit: raise ValueError('Buffered asset exceeds byte limit')
    if response.is_stream_consumed:
        # A custom/pre-buffered transport has already decoded its response before
        # this boundary. Preserve compatibility and reject its decoded size here;
        # actual network streams take the allocation-bounded decoder below.
        content=response.content
        if len(content)>limit:raise ValueError('Buffered asset exceeds decoded byte limit')
        return content
    output=bytearray(); decoder=None; encoded=0
    async for raw in response.aiter_raw(chunk_size=16*1024):
        encoded+=len(raw)
        if encoded>limit: raise ValueError('Buffered asset exceeds encoded byte limit')
        if encoding in {'gzip','deflate'}:
            if decoder is None:
                decoder=zlib.decompressobj(16+zlib.MAX_WBITS if encoding=='gzip' else zlib.MAX_WBITS)
            try: part=decoder.decompress(raw, limit-len(output)+1)
            except zlib.error as exc: raise ValueError('Invalid compressed asset') from exc
            if decoder.unconsumed_tail or decoder.unused_data:
                raise ValueError('Compressed asset exceeds limit or contains extra streams')
        else: part=raw
        if len(output)+len(part)>limit: raise ValueError('Buffered asset exceeds decoded byte limit')
        output.extend(part)
    if decoder is not None and not decoder.eof: raise ValueError('Truncated compressed asset')
    return bytes(output)
