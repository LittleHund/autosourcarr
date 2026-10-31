"""Small per-process phase samples. No URLs, titles, tokens or unbounded labels."""
from collections import deque
from contextlib import contextmanager
import threading
import time
from functools import wraps

PHASES=frozenset({'projection','metadata','crawl','admission','source','quality_decode','observation','journal'})


class RuntimeMetrics:
    def __init__(self):
        self.lock=threading.Lock()
        self.samples={name:deque(maxlen=128) for name in PHASES}
        self.counts={name:0 for name in PHASES}
        self.active=0;self.encoded_bytes=0;self.peak_encoded_bytes=0;self.peak_active=0
        self.decoded_estimate=0;self.peak_decoded_estimate=0

    @contextmanager
    def decoded_pixels(self,pixels):
        estimate=max(0,int(pixels))*4
        with self.lock:
            self.decoded_estimate+=estimate;self.peak_decoded_estimate=max(self.peak_decoded_estimate,self.decoded_estimate)
        try:yield
        finally:
            with self.lock:self.decoded_estimate-=estimate

    @contextmanager
    def phase(self,name,encoded_bytes=0):
        if name not in PHASES:raise ValueError('Unknown metric phase')
        started=time.perf_counter()
        if name=='quality_decode':
            with self.lock:
                self.active+=1;self.encoded_bytes+=encoded_bytes
                self.peak_active=max(self.active,self.peak_active)
                self.peak_encoded_bytes=max(self.encoded_bytes,self.peak_encoded_bytes)
        try:yield
        finally:
            with self.lock:
                self.counts[name]+=1
                self.samples[name].append(round((time.perf_counter()-started)*1000,3))
                if name=='quality_decode':self.active-=1;self.encoded_bytes-=encoded_bytes

    def snapshot(self):
        with self.lock:
            rows=[]
            for name in sorted(PHASES):
                values=sorted(self.samples[name])
                rows.append({'phase':name,'count':self.counts[name],'samples':len(values),
                             'p50_ms':values[len(values)//2] if values else None,
                             'p95_ms':values[min(len(values)-1,int(len(values)*.95))] if values else None})
            return {'phases':rows,'decode':{'active':self.active,'peak_active':self.peak_active,
                    'encoded_bytes':self.encoded_bytes,'peak_encoded_bytes':self.peak_encoded_bytes},
                    'decode_estimates':{'active_rgba_bytes':self.decoded_estimate,'peak_rgba_bytes':self.peak_decoded_estimate},
                    'limits':{'samples_per_phase':128,'memory_measure':'Owned encoded buffers plus 4-byte-per-pixel estimates; native decoder/hash RSS requires a host benchmark'}}


metrics=RuntimeMetrics()


def timed(name):
    def decorate(function):
        @wraps(function)
        def invoke(*args,**kwargs):
            with metrics.phase(name):return function(*args,**kwargs)
        return invoke
    return decorate
