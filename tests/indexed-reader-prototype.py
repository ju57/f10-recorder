"""THROWAWAY E1 indexed MPEG-TS reader, generated H264/AAC fixture only.

No production integration, live pixels/audio, UI, settings or installation.
Reuses the pinned engine, controlled inputs and independent pixel validator.
"""
from __future__ import annotations
import argparse, hashlib, importlib.util, json, math, os, re, shutil, statistics
import subprocess, sys, threading, time
from fractions import Fraction
from pathlib import Path
import numpy as np

HERE = Path(__file__).resolve()
spec = importlib.util.spec_from_file_location('precision_fixture', HERE.with_name('precision-export-prototype.py'))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
REPO = HERE.parents[1]
BASE = 126000  # Explicit fixture transport origin, not inferred output duration.
MASK = (1 << 33) - 1

def write(path, value):
    path.write_text(json.dumps(value, indent=2), encoding='utf-8')

def plan():
    return {'version': 1, 'kind': 'THROWAWAY indexed virtual MPEGTS reader',
            'question': 'Does retained timestamp/transport metadata avoid repeated concat child analysis while preserving physical exact hybrid cuts?',
            'source': 'Existing generated H2641080p60/AAC48k stereo, GOP60 bf0, precision-export-20261024-b',
            'effects': {'desktop_capture': False, 'audio_capture': False, 'ui': False,
                        'settings': False, 'installation': False, 'synthetic_nvenc': True},
            'candidate': 'Index closed chunks once; rebase PES PTS/DTS and PCR; regenerate continuity counters; pipe one continuous TS input to pinned FFmpeg; encode partial video GOPs only and trim/reencode audio once.',
            'index_limit': 'Only clear188-byte TS, known PAT/PMT/PIDs, unsplit PES timing headers, H264/AAC, no Bframes or source transitions. Reject other layouts.',
            'timing': 'Before input pins through boundary encoding, virtual reader, authenticated mux/faststart, structural ffprobe and rename. Metadata prepared before save; per-source bytes read and patched inside timer.',
            'preparation': 'Record hashing/index wall+CPU time and each chunk cost separately. No claim this establishes continuous acquisition overhead.',
            'concurrency': 'One real-time generated NVENC producer; progress monitor samples every250ms; no full validation decoding during timing.',
            'qualification': 'Five five-second phase-varied clips, default and edit-list-ignored full video validation; then one300-second candidate and legacy-hybrid control before a fresh20 candidate batch.',
            'schedule': 'Two passes over10 rational offsets from prior prototype; first/repeat labels do not establish OS-cold cache; do not pool old observations.',
            'targets': {'median_seconds': 3, 'p95_seconds': 5, 'video_boundary_frames': 1,
                        'audio_marker_seconds': .05, 'physical_video_interval': True},
            'storage': {'budget_bytes': 30_000_000_000, 'expected_bytes': 22_000_000_000,
                        'next_trial_reserve_bytes': 2_000_000_000, 'minimum_free_bytes': 40_000_000_000},
            'limits': 'Closed fixture omits live tail/hotkey latency; periodic audio establishes phase only; no game/fullscreen/coexistence or production acceptance.'}

def pts_decode(b):
    return ((int(b[0]) >> 1 & 7) << 30 | int(b[1]) << 22 |
            (int(b[2]) >> 1) << 15 | int(b[3]) << 7 | int(b[4]) >> 1)

def pts_encode(rows, positions, offsets, values):
    if not positions:
        return
    r, c = np.asarray(positions), np.asarray(offsets)
    v = np.asarray(values, dtype=np.int64) & MASK
    rows[r, c] = (rows[r, c] & 0xf0) | (((v >> 30) & 7) << 1).astype(np.uint8) | 1
    rows[r, c+1] = ((v >> 22) & 255).astype(np.uint8)
    rows[r, c+2] = ((((v >> 15) & 127) << 1) | 1).astype(np.uint8)
    rows[r, c+3] = ((v >> 7) & 255).astype(np.uint8)
    rows[r, c+4] = (((v & 127) << 1) | 1).astype(np.uint8)

def pids(rows):
    return ((rows[:, 1].astype(np.uint16) & 31) << 8) | rows[:, 2]

def index_chunk(path, video_only=False):
    tick, cpu = time.perf_counter(), time.process_time()
    raw = path.read_bytes()
    if not raw or len(raw) % 188:
        raise ValueError('Unsupported TS size: '+str(path))
    b = np.frombuffer(raw, np.uint8).reshape(-1, 188)
    if not np.all(b[:, 0] == 0x47) or np.any(b[:, 1] & 0x80) or np.any(b[:, 3] & 0xc0):
        raise ValueError('Unsupported TS synchronization/error/scrambling')
    pid = pids(b)
    if not set(map(int, np.unique(pid))) <= {0, 17, 256, 257, 4096, 8191}:
        raise ValueError('Unexpected fixture PID')
    payload = (b[:, 3] & 0x10) != 0
    adapt = (b[:, 3] & 0x20) != 0
    starts = np.flatnonzero(((b[:, 1] & 0x40) != 0) & payload & np.isin(pid, [256, 257]))
    pes = []
    for r in starts:
        r = int(r)
        off = 4 + (int(b[r, 4])+1 if adapt[r] else 0)
        if off+14 > 188 or list(b[r, off:off+3]) != [0, 0, 1]:
            raise ValueError('PES header split/layout unsupported')
        flag = int(b[r, off+7]) >> 6
        if flag not in (2, 3) or (flag == 3 and off+19 > 188):
            raise ValueError('Unsupported PES timestamps')
        rec = {'row': r, 'pid': int(pid[r]), 'offset': off+9, 'pts': pts_decode(b[r, off+9:off+14])}
        if flag == 3:
            rec.update(dts_offset=off+14, dts=pts_decode(b[r, off+14:off+19]))
        pes.append(rec)
    video = [x for x in pes if x['pid'] == 256]
    if not video or (not video_only and not any(x['pid'] == 257 for x in pes)):
        raise ValueError('Expected video/audio PES missing')
    if any(x.get('dts', x['pts']) != x['pts'] for x in video):
        raise ValueError('Bframes not admitted by this fixture reader')
    pcr_rows = np.flatnonzero(adapt & (b[:, 4] >= 7) & ((b[:, 5] & 0x10) != 0))
    pcr = [{'row': int(r), 'base': (int(b[r, 6]) << 25 | int(b[r, 7]) << 17 |
            int(b[r, 8]) << 9 | int(b[r, 9]) << 1 | int(b[r, 10]) >> 7)} for r in pcr_rows]
    # The known fixture's PMT must advertise the admitted H264/AAC stream set.
    pmt = np.flatnonzero((pid == 4096) & ((b[:, 1] & 0x40) != 0) & payload)
    if not len(pmt):
        raise ValueError('PMT missing')
    r = int(pmt[0]); off = 4 + (int(b[r, 4])+1 if adapt[r] else 0)
    off += 1 + int(b[r, off])
    if b[r, off] != 2:
        raise ValueError('Unsupported PMT')
    end = off+3+(((int(b[r, off+1]) & 15) << 8) | int(b[r, off+2]))-4
    pos = off+12+(((int(b[r, off+10]) & 15) << 8) | int(b[r, off+11]))
    streams = {}
    while pos < end:
        streams[((int(b[r, pos+1]) & 31) << 8) | int(b[r, pos+2])] = int(b[r, pos])
        pos += 5+(((int(b[r, pos+3]) & 15) << 8) | int(b[r, pos+4]))
    if streams != ({256: 27} if video_only else {256: 27, 257: 15}):
        raise ValueError('Unexpected PMT stream schema: '+str(streams))
    return {'name': path.name, 'bytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest(),
            'packets': len(b), 'pes': pes, 'pcr': pcr, 'origin': video[0]['pts'],
            'video_pes': len(video), 'streams': streams,
            'index_seconds': time.perf_counter()-tick, 'index_cpu_seconds': time.process_time()-cpu}

def shifted(path, index, shift):
    # Read bytes only on demand; no prepared merged input or retained payload cache.
    raw = bytearray(path.read_bytes())
    if len(raw) != index['bytes']:
        raise ValueError('Pinned input size changed')
    b = np.frombuffer(raw, np.uint8).reshape(-1, 188)
    pes = index['pes']
    pts_encode(b, [p['row'] for p in pes], [p['offset'] for p in pes], [p['pts']+shift for p in pes])
    dts = [p for p in pes if 'dts' in p]
    pts_encode(b, [p['row'] for p in dts], [p['dts_offset'] for p in dts], [p['dts']+shift for p in dts])
    if index['pcr']:
        r = np.asarray([p['row'] for p in index['pcr']])
        v = (np.asarray([p['base'] for p in index['pcr']], dtype=np.int64)+shift) & MASK
        for col, bits in [(6, 25), (7, 17), (8, 9), (9, 1)]:
            b[r, col] = ((v >> bits) & 255).astype(np.uint8)
        b[r, 10] = (b[r, 10] & 127) | ((v & 1) << 7).astype(np.uint8)
    return b

class ReaderExperiment(fixture.Experiment):
    def check_space(self):
        unique = {}
        for p in self.root.rglob('*'):
            if p.is_file():
                s = p.stat(); unique[(s.st_dev, s.st_ino)] = s.st_size
        used = sum(unique.values()); budget = plan()['storage']
        if used > budget['budget_bytes']-budget['next_trial_reserve_bytes'] or shutil.disk_usage(self.root).free < budget['minimum_free_bytes']:
            raise RuntimeError('Indexed-reader storage admission rejected')

    def prepare(self, seed):
        self.root.mkdir(parents=True, exist_ok=False)
        write(self.root/'plan.json', plan())
        write(self.root/'pins.json', fixture.PINS)
        (self.root/'vendor').mkdir(); (self.root/'logs').mkdir()
        for name, key in [('ffmpeg.exe', 'ExecutableSha256'), ('ffprobe.exe', 'ProbeSha256')]:
            src = seed/'vendor'/name
            if fixture.sha(src) != fixture.PINS[key]:
                raise RuntimeError('Seed executable hash mismatch')
            shutil.copyfile(src, self.root/'vendor'/name)
        self.run(['-version'], 'version')
        tick, cpu = time.perf_counter(), time.process_time()
        results = {}
        retained = {x['name']: x for x in json.loads((seed/'reference-inputs.json').read_text())}
        for kind in ['qualification', 'reference']:
            (self.root/kind).mkdir()
            entries = {}
            for src in sorted((seed/kind).glob('chunk-*.ts')):
                rec = index_chunk(src)
                if kind == 'reference' and rec['sha256'] != retained[src.name]['sha256']:
                    raise RuntimeError('Original controlled input hash mismatch')
                os.link(src, self.root/kind/src.name)
                entries[src.name] = rec
            results[kind] = entries
        write(self.root/'index.json', results)
        write(self.root/'preparation.json', {'seed': str(seed), 'seconds': time.perf_counter()-tick,
            'cpu_seconds': time.process_time()-cpu, 'files': sum(map(len, results.values())),
            'index_bytes': (self.root/'index.json').stat().st_size, 'harness_sha256': fixture.sha(HERE),
            'source_inventory_sha256': fixture.sha(seed/'reference-inputs.json'),
            'max_chunk_index_seconds': max(x['index_seconds'] for e in results.values() for x in e.values())})
        write(self.root/'harness-source.json', {'indexed_reader': fixture.sha(HERE), 'fixture': fixture.sha(HERE.with_name('precision-export-prototype.py'))})
        print('Prepared metadata and immutable source links', flush=True)

    def feed(self, target, source, index, first, last, output):
        counters = {}; count = 0; reader_cpu = time.thread_time(); tick = time.perf_counter()
        def emit(rows):
            nonlocal count
            if not len(rows): return
            pid = pids(rows)
            for identity in np.unique(pid):
                loc = np.flatnonzero(pid == identity)
                payload = (rows[loc, 3] & 0x10) != 0
                seq = np.cumsum(payload, dtype=np.int64)-1+counters.get(int(identity), 0)
                rows[loc, 3] = (rows[loc, 3] & 0xf0) | (seq & 15).astype(np.uint8)
                counters[int(identity)] = counters.get(int(identity), 0)+int(payload.sum())
            data = rows.tobytes(); output.write(data); count += len(data)
        low = first//60; high = math.ceil(Fraction(last+1, 60))+1
        for second in range(low, high):
            name = f'chunk-{second:06d}.ts'; meta = index[name]
            # Map independently reset chunk transport time onto one exact source epoch.
            shift = BASE+second*90000-first*1500-meta['origin']
            rows = shifted(target/name, meta, shift)
            left, right = max(first, second*60), min(last+1, (second+1)*60)
            complete = left == second*60 and right == (second+1)*60
            if complete:
                emit(rows)
            else:
                pid = pids(rows)
                emit(rows[(pid != 256) & (pid != 257)])
                if left < right:
                    boundary = target/f'boundary-{second:06d}.ts'
                    info = index_chunk(boundary, video_only=True)
                    video = shifted(boundary, info, BASE+(left-first)*1500-info['origin'])
                    emit(video[pids(video) == 256])
                emit(rows[pid == 257])
        output.flush()
        return {'bytes_served': count, 'seconds_including_pipe_backpressure': time.perf_counter()-tick,
                'reader_thread_cpu_seconds': time.thread_time()-reader_cpu}

    def export_indexed(self, source, start, duration, label):
        self.check_space()
        target = self.root/'exports'/label; target.mkdir(parents=True, exist_ok=False)
        start, duration = Fraction(start), Fraction(duration); end = start+duration
        first, last = math.ceil(start*60), math.ceil(end*60)-1
        receipt = {'label': label, 'method': 'indexed-hybrid', 'start': str(start), 'end': str(end),
            'duration': str(duration), 'expected_first_id': first, 'expected_last_id': last,
            'expected_count': last-first+1, 'status': 'started', 'harness_sha256': fixture.sha(HERE)}
        write(target/'receipt.json', receipt)
        stages = {}; tick = time.perf_counter()
        try:
            before = time.perf_counter()
            index = self.index[source.name]
            for second in range(first//60, math.ceil(Fraction(last+1, 60))+1):
                name = f'chunk-{second:06d}.ts'
                os.link(source/name, target/name)
            stages['snapshot_seconds'] = time.perf_counter()-before
            before = time.perf_counter()
            for second in sorted({first//60, last//60}):
                left, right = max(first, second*60), min(last+1, (second+1)*60)
                if left == second*60 and right == (second+1)*60: continue
                self.run(['-hide_banner','-v','warning','-nostdin','-i',target/f'chunk-{second:06d}.ts',
                    '-map','0:v:0','-an','-vf',f'trim=start_frame={left-second*60}:end_frame={right-second*60},setpts=PTS-STARTPTS']
                    +fixture.ENC+['-f','mpegts',target/f'boundary-{second:06d}.ts'], label+'-boundary')
            stages['boundary_seconds'] = time.perf_counter()-before
            partial, final = target/'result.partial.mp4', target/'result.mp4'
            args = ['-hide_banner','-v','warning','-nostdin','-copyts','-f','mpegts','-i','pipe:0',
                '-map','0:v:0','-map','0:a:0','-c:v','copy','-bsf:v','setts=pts=PTS-1.4/TB:dts=DTS-1.4/TB',
                '-af',f'atrim=start=1.4:end={1.4+float(duration):.9f},asetpts=PTS-1.4/TB',
                '-c:a','aac','-b:a','192k','-ar','48000','-ac','2','-avoid_negative_ts','disabled',
                '-movflags','+faststart','-video_track_timescale','90000',str(partial)]
            before = time.perf_counter()
            with fixture.Lease(self.exe, fixture.PINS['ExecutableSha256']), open(target/'mux.stderr.txt','wb') as err:
                process = subprocess.Popen([str(self.exe)]+args, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                    stderr=err, creationflags=0x08000000)
                watchdog = threading.Timer(120, lambda: process.kill() if process.poll() is None else None)
                watchdog.start()
                write(target/'mux-identity.json', {'pid':process.pid, 'args':args, 'tool_sha256':fixture.PINS['ExecutableSha256']})
                try:
                    receipt['reader'] = self.feed(target, source, index, first, last, process.stdin)
                    process.stdin.close(); process.wait(timeout=120)
                    if process.returncode: raise RuntimeError('Mux failed: '+(target/'mux.stderr.txt').read_text()[-1600:])
                finally:
                    watchdog.cancel()
                    if process.poll() is None: process.kill(); process.wait(timeout=15)
                    write(target/'mux-stopped.json', {'pid':process.pid,'returncode':process.returncode,'confirmed_stopped':process.poll() is not None})
            stages['reader_mux_seconds'] = time.perf_counter()-before
            before = time.perf_counter()
            raw, _ = self.run(['-v','error','-show_streams','-show_format','-of','json',partial],label+'-structural',probe=True)
            meta = json.loads(raw); write(target/'structural.json',meta)
            if [s['codec_type'] for s in meta['streams']] != ['video','audio']: raise RuntimeError('Wrong stream schema')
            v, a = meta['streams']
            if (v['codec_name'],v['width'],v['height'],a['codec_name'],a['sample_rate'],a['channels']) != ('h264',1920,1080,'aac','48000',2):
                raise RuntimeError('Wrong codec/schema')
            os.rename(partial, final)
            stages['validation_publication_seconds'] = time.perf_counter()-before
            receipt.update(status='committed',seconds=time.perf_counter()-tick,bytes=final.stat().st_size,stages=stages)
        except Exception as error:
            receipt.update(status='failed',seconds=time.perf_counter()-tick,error=str(error),stages=stages)
        write(target/'receipt.json',receipt)
        print(f"{label}: {receipt['status']} {receipt['seconds']:.3f}s {receipt.get('error','')}",flush=True)
        return receipt

    def load_index(self):
        self.index = json.loads((self.root/'index.json').read_text())

    def validate_one(self, row):
        target = self.root/'exports'/row['label']
        if row['status'] != 'committed': return {'pass':False,'reason':'not committed'}
        result = self.validate(target)
        physical = self.validate(target, True)
        result['physical_without_edits'] = physical
        result['pass'] = bool(result['pass'] and result['exact_ceil_interval'] and physical['exact_ceil_interval'] and physical['all_ids_valid'] and physical['contiguous_ids'])
        write(target/'indexed-validation.json',result)
        return result

    def qualify(self):
        cases = [('idr',Fraction(2)),('after',Fraction(2)+Fraction(1,60)),('half',Fraction(5,2)),
                 ('before',Fraction(2)+Fraction(59,60)),('subframe',Fraction(2)+Fraction(43,120))]
        rows = []
        for name,start in cases:
            row = self.export_indexed(self.root/'qualification',start,Fraction(5),'qual-'+name)
            row['validation'] = self.validate_one(row); rows.append(row)
            write(self.root/'qualification-results.json',rows)
            if not row['validation']['pass']: raise RuntimeError('Qualification failed; retain attempt')

    def begin_observed_producer(self):
        self.samples = []; self.monitor_stop = threading.Event()
        self.start_producer()
        def monitor():
            while not self.monitor_stop.is_set():
                stamp = time.perf_counter(); frame = None; out_time = None
                try:
                    text = (self.producer_path/'progress.txt').read_text()
                    frames = re.findall(r'^frame=(\d+)',text,re.M)
                    times = re.findall(r'^out_time_us=(\d+)',text,re.M)
                    frame = int(frames[-1]) if frames else None
                    out_time = int(times[-1]) if times else None
                except OSError: pass
                self.samples.append({'time':stamp,'frame':frame,'out_time_us':out_time,'alive':self.producer.poll() is None})
                self.monitor_stop.wait(.25)
        self.monitor = threading.Thread(target=monitor,daemon=True)
        self.monitor.start(); time.sleep(.3)

    def end_observed_producer(self):
        self.monitor_stop.set(); self.monitor.join(timeout=5)
        self.stop_producer()
        write(self.producer_path/'progress-samples.json',self.samples)

    def source_health(self, before, after):
        all_samples = self.samples
        left = [s for s in all_samples if s['time'] <= before]
        right = [s for s in all_samples if s['time'] >= after]
        if not left or not right: return {'pass':False,'reason':'unbracketed progress'}
        points = [s for s in all_samples if left[-1]['time'] <= s['time'] <= right[0]['time']]
        if any(s['frame'] is None or not s['alive'] for s in points): return {'pass':False,'reason':'missing/dead producer'}
        span = points[-1]['time']-points[0]['time']
        frames = points[-1]['frame']-points[0]['frame']
        gaps = [b['time']-a['time'] for a,b in zip(points,points[1:])]
        last_change = points[0]['time']; last_frame = points[0]['frame']; max_stall = 0
        for p in points[1:]:
            max_stall = max(max_stall,p['time']-last_change)
            if p['frame'] != last_frame: last_change=p['time'];last_frame=p['frame']
        return {'pass': bool(span > 0 and frames/span >= 30 and max(gaps,default=999) < 1.5 and max_stall < 2.5),
                'samples':len(points),'bracket_seconds':span,'frame_advance':frames,
                'observed_progress_fps':frames/span if span else 0,
                'maximum_poll_gap_seconds':max(gaps,default=999),'maximum_observed_frame_stall_seconds':max_stall,
                'limit':'Progress supports active generated encoding; it does not prove live source picture acquisition.'}

    def probe_reference(self):
        quals = json.loads((self.root/'qualification-results.json').read_text())
        if len(quals) != 5 or not all(r['validation']['pass'] for r in quals):
            raise RuntimeError('Five short qualifications required')
        amendment = {'phase':'before reference collection','harness_sha256':fixture.sha(HERE),
            'plan_sha256':fixture.sha(self.root/'plan.json'),'rows':['indexed-hybrid n1','legacy-hybrid n1'],
            'producer_progress':{'poll_seconds':.25,'max_gap_seconds':1.5,'max_stall_seconds':2.5,
                'minimum_observed_progress_fps':30,'note':'coarse one-second encoder reports, not frame identity'},
            'no_decode_during_collection':True}
        write(self.root/'probe-plan.json',amendment)
        rows=[]
        try:
            self.begin_observed_producer()
            for method in ['indexed-hybrid','hybrid']:
                before=time.perf_counter()
                if method == 'indexed-hybrid':
                    row=self.export_indexed(self.root/'reference',Fraction(5)+Fraction(43,120),Fraction(300),'probe-indexed')
                else:
                    row=self.export(self.root/'reference',Fraction(5)+Fraction(43,120),Fraction(300),'hybrid','probe-legacy')
                after=time.perf_counter();time.sleep(.35)
                row['producer_progress']=self.source_health(before,after);rows.append(row)
                write(self.root/'probe-results.json',rows)
                if row['status'] != 'committed' or not row['producer_progress']['pass']:
                    raise RuntimeError('Probe collection failed; retain attempt')
        finally:
            if hasattr(self,'monitor'):self.end_observed_producer()
            else:self.stop_producer()
        for row in rows:
            row['validation']=self.validate_one(row) if row['method']=='indexed-hybrid' else self.validate(self.root/'exports'/row['label'])
            write(self.root/'validated-probe-results.json',rows)
            if not row['validation']['pass']:raise RuntimeError('Five-minute qualification failed')

    def benchmark(self):
        probes=json.loads((self.root/'validated-probe-results.json').read_text())
        if not all(r['validation']['pass'] and r['producer_progress']['pass'] for r in probes):
            raise RuntimeError('Five-minute control qualification required')
        schedule={'harness_sha256':fixture.sha(HERE),'fixture_sha256':fixture.sha(HERE.with_name('precision-export-prototype.py')),
            'plan_sha256':fixture.sha(self.root/'plan.json'),'index_sha256':fixture.sha(self.root/'index.json'),
            'trials':[{'label':f'ref-indexed-{i:02d}','start':str(Fraction(5)+offset),'duration':300,
                       'phase':'first-pass' if i<10 else 'repeat-pass'} for i,offset in enumerate(fixture.OFFSETS+fixture.OFFSETS)],
            'timing_targets':plan()['targets'],'cache_note':'No OS-cold claim; n1 controls and historical timings excluded.'}
        path=self.root/'frozen-reference-schedule.json'
        if path.exists():raise RuntimeError('Fresh reference schedule required')
        write(path,schedule);rows=[]
        try:
            self.begin_observed_producer()
            for trial in schedule['trials']:
                before=time.perf_counter()
                row=self.export_indexed(self.root/'reference',Fraction(trial['start']),Fraction(300),trial['label'])
                after=time.perf_counter();time.sleep(.35)
                row.update(phase=trial['phase'],producer_progress=self.source_health(before,after))
                rows.append(row);write(self.root/'reference-results.json',rows)
                if row['status']!='committed' or not row['producer_progress']['pass']:
                    raise RuntimeError('Collection failed; retain attempt and stop batch')
        finally:
            if hasattr(self,'monitor'):self.end_observed_producer()
            else:self.stop_producer()
        print('Reference timing complete; owned producer stopped before decoding',flush=True)

    def validate_reference(self):
        rows=json.loads((self.root/'reference-results.json').read_text())
        for row in rows:
            row['validation']=self.validate_one(row)
            write(self.root/'validated-reference-results.json',rows)
        times=sorted(r['seconds'] for r in rows)
        summary={'n':len(rows),'median_seconds':statistics.median(times),'p95_seconds':times[math.ceil(.95*len(times))-1],
            'minimum_seconds':min(times),'maximum_seconds':max(times),
            'all_valid':all(r['validation']['pass'] for r in rows),'all_progress_bracketed':all(r['producer_progress']['pass'] for r in rows),
            'precision_frames':sum(r['validation']['decoded_frames'] for r in rows),
            'targets':plan()['targets']}
        summary['joint_fixture_pass']=bool(len(rows)==20 and summary['all_valid'] and summary['all_progress_bracketed'] and summary['median_seconds']<=3 and summary['p95_seconds']<=5)
        write(self.root/'summary.json',summary);print(json.dumps(summary,indent=2),flush=True)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root',type=Path,required=True)
    parser.add_argument('--seed',type=Path,default=REPO/'artifacts/precision-export-20261024-b')
    for action in ['plan','prepare','qualify','probe','benchmark','validate']:
        parser.add_argument('--'+action,action='store_true')
    args = parser.parse_args(); exp = ReaderExperiment(args.root)
    if args.plan: print(json.dumps(plan(),indent=2)); return
    if args.prepare: exp.prepare(args.seed.resolve()); return
    exp.load_index()
    if args.qualify: exp.qualify()
    elif args.probe: exp.probe_reference()
    elif args.benchmark: exp.benchmark()
    elif args.validate: exp.validate_reference()
    else: raise ValueError('Choose an action')

if __name__ == '__main__': main()
