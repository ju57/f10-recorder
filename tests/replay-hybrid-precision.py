"""Bounded, generated-media correctness pilot; never a product save or admission.

No desktop/audio capture, playback, recorder, preferences or catalog access.
Only --plan and --prepare are media-free. Execution needs the frozen plan hash.
"""
from __future__ import annotations
import argparse, contextlib, ctypes as C, hashlib, importlib.util, json, math
import os, re, shutil, subprocess, sys, threading, time
from fractions import Fraction as F
from pathlib import Path
import numpy as np

HERE = Path(__file__).resolve()
REPO = HERE.parents[1]
spec = importlib.util.spec_from_file_location('ts_reader', HERE.with_name('indexed-reader-prototype.py'))
ts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ts)
sha = ts.fixture.sha
write = ts.write
W, H, FPS, EPOCH = 320, 192, 60, 900000
ENC = ['-c:v','h264_nvenc','-preset','p3','-tune','ll','-rc','cbr','-b:v','2M',
       '-maxrate','2M','-bufsize','4M','-bf','0','-forced-idr','1','-flags','+cgop',
       '-pix_fmt','yuv420p','-fps_mode','passthrough','-enc_time_base:v','1:48000']
CASES = [('idr','a','2','3'), ('fraction','a','283/120','3'),
         ('minimum','a','203/1000','1'), ('warmup','a','0','15/4'),
         ('overlap1','a','3217/1000','3'), ('overlap2','a','4217/1000','3'),
         ('same-gop','b','1/10','1'), ('nonzero-epoch','b','2','2')]


def logical_bytes(root,live=None):
    """Use Windows directory-entry metadata; retain conservative alias charging."""
    total=0
    with os.scandir(root) as entries:
        for entry in entries:
            info=entry.stat(follow_symlinks=False)
            if info.st_file_attributes & 0x400:raise ValueError('Reparse entry is not admitted: '+entry.path)
            total+=logical_bytes(entry.path,live) if entry.is_dir(follow_symlinks=False) else live(entry.path,info.st_size) if live else info.st_size
    return total


def preview():
    return dict(kind='hybrid-physical-precision-v1', effects=dict(generated_nvenc=True,
        capture=False, playback=False, ui=False, settings=False, installation=False),
        sources=[dict(name='a', seconds=12, gop_seconds=1, epoch='169/60'),
                 dict(name='b', seconds=8, gop_seconds=2, epoch='2469/20')],
        cases=CASES, source_audio='Independent stereo polynomial chirps, no periodic phase ambiguity',
        bounds=dict(children=64, concurrent_children=1, seconds=330, child_seconds=35,
                    child_memory_bytes=2147483648, output_bytes=750000000,
                    early_stop_bytes=650000000, minimum_free_bytes=60000000000,
                    stage_stdout_bytes=60000000, stage_derivative_bytes=16000000,
                    closure_reserve_bytes=50000000, cleanup_seconds=5,
                    outer_seconds=350, outer_cleanup_seconds=5, outer_memory_bytes=1073741824,
                    outer_sampled_threads=128, outer_log_bytes=524288,
                    child_log_bytes=131072, cumulative_helpers_before=177,
                    metadata_helpers=1, cumulative_helpers_ceiling=243),
        claims='Small generated physical-cut construction only. No runtime qualification or latency acceptance.',
        contract='Fresh full software decode of closed MP4 before test publication; independent every-picture and unique-audio oracles also run before publication.',
        timing='Pins, per-save source indexing, boundaries, assembly, mux, full decode, independent oracles and rename included; queue zero. No capture work.',
        exclusions='No 1/5/30m qualification, worker/UI Saved, corruption-equivalence or storage-model transfer. Warmup means source-start selection only; overlapping intervals are sequential; three audio windows are sampled alignment, not full audio identity proof. Stop cohort on first failed correctness case.')


K = C.WinDLL('kernel32', use_last_error=True) if os.name == 'nt' else None
if K:
    K.CreateJobObjectW.argtypes = [C.c_void_p,C.c_wchar_p]; K.CreateJobObjectW.restype = C.c_void_p
    K.SetInformationJobObject.argtypes = [C.c_void_p,C.c_int,C.c_void_p,C.c_uint32]
    K.AssignProcessToJobObject.argtypes = [C.c_void_p,C.c_void_p]
    K.CloseHandle.argtypes = [C.c_void_p]
    K.CreateFileW.argtypes=[C.c_wchar_p,C.c_uint32,C.c_uint32,C.c_void_p,C.c_uint32,C.c_uint32,C.c_void_p]
    K.CreateFileW.restype=C.c_void_p
    K.GetFileInformationByHandleEx.argtypes=[C.c_void_p,C.c_int,C.c_void_p,C.c_uint32]
    K.GetProcessTimes.argtypes=[C.c_void_p,C.c_void_p,C.c_void_p,C.c_void_p,C.c_void_p]
    K.GetProcessIoCounters.argtypes=[C.c_void_p,C.c_void_p]
    K.K32GetProcessMemoryInfo.argtypes=[C.c_void_p,C.c_void_p,C.c_uint32]


class Job:
    def __init__(self,memory_bytes=2147483648):self.memory_bytes=memory_bytes
    def __enter__(self):
        # JOBOBJECT_EXTENDED_LIMIT_INFORMATION, Windows x64 ABI.
        if not K or C.sizeof(C.c_void_p) != 8: raise RuntimeError('Windows x64 required')
        self.handle = K.CreateJobObjectW(None, None)
        if not self.handle: raise C.WinError(C.get_last_error())
        limits = C.create_string_buffer(144)
        # KILL_ON_JOB_CLOSE | PROCESS_MEMORY | ACTIVE_PROCESS (one child).
        C.c_uint32.from_buffer(limits,16).value = 0x2000 | 0x100 | 8
        C.c_uint32.from_buffer(limits,40).value = 1
        C.c_uint64.from_buffer(limits,112).value = self.memory_bytes
        if not K.SetInformationJobObject(self.handle,9,limits,144):
            K.CloseHandle(self.handle); raise C.WinError(C.get_last_error())
        return self
    def assign(self,p):
        if not K.AssignProcessToJobObject(self.handle,int(p._handle)):
            p.kill(); p.wait(); raise C.WinError(C.get_last_error())
    def __exit__(self,*args): K.CloseHandle(self.handle)


def parameter_sets(path):
    rows = np.frombuffer(path.read_bytes(),np.uint8).reshape(-1,188)
    rows=rows[(ts.pids(rows)==256)&((rows[:,3]&16)!=0)]
    offsets=4+np.where((rows[:,3]&32)!=0,rows[:,4].astype(np.int64)+1,0)
    starts=np.flatnonzero((rows[:,1]&64)!=0)
    if any(bytes(rows[n,offsets[n]:offsets[n]+3])!=b'\0\0\1' for n in starts):raise ValueError('PES layout')
    offsets[starts]+=9+rows[starts,offsets[starts]+8].astype(np.int64)
    data=rows[np.arange(188)[None,:]>=offsets[:,None]].tobytes()
    nals = re.split(b'\x00\x00\x00?\x01',data)
    sets = {bytes(n).rstrip(b'\0') for n in nals if n and n[0]&31 in (7,8)}
    if {n[0]&31 for n in sets} != {7,8}: raise ValueError('Missing SPS/PPS')
    return sets


class Pilot:
    def __init__(self,plan):
        self.plan=plan; self.root=Path(plan['root']); self.exe=Path(plan['engine']); self.probe=Path(plan['probe'])
        self.bounds=plan['bounds']
        self.root_prefix=os.path.normcase(os.path.abspath(self.root))+os.sep
        self.live_files=frozenset();self.live_scopes=();self.live_known_files=frozenset()
        self.live_observations=dict(queries=0,query_failures=0,maximum_directory_underreport_bytes=0,maximum_directory_eof_underreport_bytes=0,maximum_live_bytes=0,files={})
        self.rows=[]; self.started=time.perf_counter(); self.stop_reason=None
    def guard(self):
        if time.perf_counter()-self.started>self.bounds['seconds']: raise RuntimeError('Whole-cohort deadline')
        used=self.used_bytes(self.root)
        if used>self.bounds['early_stop_bytes'] or shutil.disk_usage(self.root).free<self.bounds['minimum_free_bytes']:
            raise RuntimeError('Storage early stop')
    def reserve(self,forward):
        self.guard()
        used=self.used_bytes(self.root)
        if used+forward+self.bounds['closure_reserve_bytes']>self.bounds['output_bytes']: raise RuntimeError('Stage plus closure storage reservation')
    def run(self,args,label,feed=None,probe=False,tool=None):
        self.guard()
        limits=self.bounds
        self.reserve(limits['stage_stdout_bytes']+limits['stage_derivative_bytes']+limits['child_log_bytes'])
        if time.perf_counter()-self.started+limits['child_seconds']+limits['cleanup_seconds']>limits['seconds']: raise RuntimeError('Insufficient child/cleanup time reserve')
        before_bytes=self.used_bytes(self.root)
        executable=Path(tool).resolve() if tool is not None else self.probe if probe else self.exe
        if str(executable) not in self.plan['files']:raise ValueError('Unbound executable')
        if len(self.rows)>=limits['children']: raise RuntimeError('Child-count ceiling')
        err=self.root/(label+'.stderr.txt'); out=self.root/(label+'.stdout.bin')
        owned_files=[err,out];owned_scopes=[]
        if not probe and args:
            last=Path(str(args[-1]))
            if last.is_absolute() and last.resolve().is_relative_to(self.root.resolve()):
                if '%' in last.name:owned_scopes.append((last.parent,last.name))
                elif not last.exists():owned_files.append(last)
        row=dict(label=label,tool=str(executable),tool_sha256=self.plan['files'][str(executable)],args=list(map(str,args)),started_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()))
        start=time.perf_counter(); p=None; done=threading.Event(); failure=[]; peak_memory=0; resource_errors=set()
        with self.track_live(owned_files,owned_scopes), Job(limits['child_memory_bytes']) as job, open(err,'xb') as e, open(out,'xb') as o:
            try:
                p=subprocess.Popen([str(executable),*map(str,args)],stdin=subprocess.PIPE if feed else subprocess.DEVNULL,
                    stdout=o,stderr=e,cwd=self.root,creationflags=0x08000000)
                job.assign(p); row['pid']=p.pid
                def watch():
                    nonlocal peak_memory
                    while not done.wait(.1):
                        try:
                            memory=C.create_string_buffer(72);C.c_uint32.from_buffer(memory).value=72
                            if K.K32GetProcessMemoryInfo(int(p._handle),memory,72):peak_memory=max(peak_memory,C.c_uint64.from_buffer(memory,8).value)
                            elif p.poll() is None:resource_errors.add('K32GetProcessMemoryInfo:'+str(C.get_last_error()))
                            self.guard()
                            current_bytes=self.used_bytes(self.root)
                            err_size=self.live_length(err,err.stat().st_size);out_size=self.live_length(out,out.stat().st_size)
                            derivative_bytes=current_bytes-before_bytes-err_size-out_size
                            if derivative_bytes>limits['stage_derivative_bytes']: raise RuntimeError('Child derivative bound')
                            if time.perf_counter()-start>limits['child_seconds'] or err_size>limits['child_log_bytes'] or out_size>limits['stage_stdout_bytes']:
                                raise RuntimeError('Child time/log/output bound')
                        except Exception as ex:
                            failure.append(str(ex))
                            row['guard_stop']=dict(reason=str(ex),at=time.perf_counter(),live=json.loads(json.dumps(self.live_observations)))
                            if p.poll() is None: p.kill()
                            return
                t=threading.Thread(target=watch,daemon=True); t.start()
                try:
                    if feed:
                        feed(p.stdin); p.stdin.close()
                    p.wait(timeout=limits['child_seconds']+1)
                finally:
                    done.set(); t.join()
                if failure: raise RuntimeError(failure[0])
                if p.returncode: raise RuntimeError(label+' failed: '+err.read_text(errors='replace')[-1200:])
                current_bytes=self.used_bytes(self.root)
                err_size=self.live_length(err,err.stat().st_size);out_size=self.live_length(out,out.stat().st_size)
                if err_size>limits['child_log_bytes'] or out_size>limits['stage_stdout_bytes'] or current_bytes-before_bytes-err_size-out_size>limits['stage_derivative_bytes']:
                    raise RuntimeError('Final child output/derivative bound')
            finally:
                if p is not None and p.poll() is None: p.kill(); p.wait(timeout=5)
                row.update(seconds=time.perf_counter()-start,exit=p.returncode if p else None,
                           stopped=p is None or p.poll() is not None,stderr=str(err),stdout=str(out))
                row['fresh_local_bytes_after_stop']=self.used_bytes(self.root)
                row['live_size_observations']=json.loads(json.dumps(self.live_observations))
                if p is not None:
                    creation,exit_time,kernel,user=(C.c_uint64() for _ in range(4))
                    if K.GetProcessTimes(int(p._handle),C.byref(creation),C.byref(exit_time),C.byref(kernel),C.byref(user)):
                        row['cpu_seconds']=(kernel.value+user.value)/10000000
                    else:resource_errors.add('GetProcessTimes:'+str(C.get_last_error()))
                    io=C.create_string_buffer(48)
                    if K.GetProcessIoCounters(int(p._handle),io):
                        row['read_bytes']=C.c_uint64.from_buffer(io,24).value;row['write_bytes']=C.c_uint64.from_buffer(io,32).value
                    else:resource_errors.add('GetProcessIoCounters:'+str(C.get_last_error()))
                    row['sampled_peak_working_set_bytes']=peak_memory or None
                    row['resource_errors']=sorted(resource_errors)
                self.rows.append(row); write(self.root/'commands.json',self.rows)
        return out.read_bytes()
    @contextlib.contextmanager
    def track_live(self,files=(),scopes=()):
        previous=(self.live_files,self.live_scopes,self.live_known_files)
        self.live_files=self.live_files|frozenset(os.path.normcase(str(Path(p).resolve())) for p in files)
        registered=[]
        for folder,pattern in scopes:
            if '%06d' not in pattern or pattern.count('%')!=1:raise ValueError('Only bounded six-digit segment names are admitted')
            registered.append((os.path.normcase(str(Path(folder).resolve()))+os.sep,re.escape(pattern).replace('%06d',r'\d{6}')))
        self.live_scopes=self.live_scopes+tuple(registered)
        self.live_known_files=frozenset(os.path.normcase(os.path.abspath(os.path.join(folder,name))) for folder,_,names in os.walk(self.root) for name in names)
        try:yield
        finally:self.live_files,self.live_scopes,self.live_known_files=previous
    def active_path(self,normalized):
        if normalized in self.live_files:return True
        for prefix,pattern in self.live_scopes:
            if normalized.startswith(prefix):
                if re.fullmatch(pattern,normalized[len(prefix):]) is None:raise ValueError('Unexpected producer output in declared segment scope')
                return True
        if self.live_files and normalized.startswith(self.root_prefix) and normalized not in self.live_known_files:raise ValueError('Unexpected undeclared producer output')
        return False
    def live_length(self,path,directory_size):
        normalized=os.path.normcase(os.path.abspath(os.fspath(path)))
        if not self.active_path(normalized):return directory_size
        began=time.perf_counter()
        handle=K.CreateFileW(str(path),0x80,7,None,3,0x00200000,None)
        if handle==C.c_void_p(-1).value:
            self.live_observations['query_failures']+=1;raise C.WinError(C.get_last_error())
        try:
            info=C.create_string_buffer(24)
            if not K.GetFileInformationByHandleEx(handle,1,info,24):
                self.live_observations['query_failures']+=1;raise C.WinError(C.get_last_error())
            if info.raw[20] or info.raw[21]:raise ValueError('Deleting/directory producer output rejected')
            allocation=C.c_int64.from_buffer(info,0).value;end=C.c_int64.from_buffer(info,8).value
            if allocation<0 or end<0:raise ValueError('Invalid native producer size')
            size=max(allocation,end)
            self.live_observations['queries']+=1
            self.live_observations['maximum_directory_underreport_bytes']=max(self.live_observations['maximum_directory_underreport_bytes'],size-directory_size)
            self.live_observations['maximum_directory_eof_underreport_bytes']=max(self.live_observations['maximum_directory_eof_underreport_bytes'],end-directory_size)
            self.live_observations['maximum_live_bytes']=max(self.live_observations['maximum_live_bytes'],size)
            if normalized not in self.live_observations['files']:
                if len(self.live_observations['files'])>=1024:raise ValueError('Native output-observation record cap')
                self.live_observations['files'][normalized]=dict(first_at=began,last_at=began,maximum_gap_seconds=0.,maximum_query_seconds=0.,maximum_eof_bytes=0,maximum_allocation_bytes=0)
            record=self.live_observations['files'][normalized];completed=time.perf_counter()
            record['maximum_gap_seconds']=max(record['maximum_gap_seconds'],completed-record['last_at'])
            record['maximum_query_seconds']=max(record['maximum_query_seconds'],completed-began)
            record.update(last_at=completed,last_eof_bytes=end,last_allocation_bytes=allocation,
                maximum_eof_bytes=max(record['maximum_eof_bytes'],end),maximum_allocation_bytes=max(record['maximum_allocation_bytes'],allocation))
            return size
        finally:K.CloseHandle(handle)
    def used_bytes(self,root):
        seen=set()
        def observe(path,size):
            normalized=os.path.normcase(os.path.abspath(os.fspath(path)));seen.add(normalized)
            return self.live_length(path,size)
        result=logical_bytes(root,observe)
        root_prefix=os.path.normcase(os.path.abspath(os.fspath(root)))+os.sep
        for path in self.live_observations['files']:
            if path.startswith(root_prefix) and self.active_path(path) and path not in seen:raise ValueError('Observed active output disappeared')
        return result
    def source(self,entry):
        name=entry['name']; folder=self.root/name; folder.mkdir()
        seconds=entry['seconds']; g=entry['gop_seconds']; epoch=float(F(entry['epoch']))
        filters=['drawbox=x=0:y=0:w=320:h=32:color=black:t=fill']
        for bit in range(20):
            for row in range(2):
                enabled=f"{'not(' if row else ''}mod(floor(n/{1<<bit}),2){')' if row else ''}"
                filters.append(f"drawbox=x={bit*16}:y={row*16}:w=16:h=16:color=white:t=fill:enable='{enabled}'")
        filters.append(f'setpts=PTS+{epoch:.9f}/TB')
        self.run(['-hide_banner','-v','warning','-nostdin','-copyts','-f','lavfi','-i',f'testsrc2=size={W}x{H}:rate=60:duration={seconds}',
            '-f','lavfi','-i',f'aevalsrc=0.35*sin(2*PI*(123*t+17*t*t))|0.32*sin(2*PI*(197*t+31*t*t)):s=48000:d={seconds}',
            '-vf',','.join(filters),'-af',f'asetpts=PTS+{epoch:.9f}/TB',*ENC,'-g',str(g*60),
            '-force_key_frames',f'expr:gte(n,n_forced*{g*60})',
            '-c:a','aac','-b:a','192k','-ar','48000','-ac','2','-f','segment','-segment_time',str(g),
            # The nonzero epoch is rounded to microseconds by segment.c. Admit
            # that sub-tick conversion when comparing exact 90kHz IDR times.
            '-segment_time_delta','0.000001',
            '-segment_format','mpegts','-segment_format_options','mpegts_copyts=1','-reset_timestamps','0',folder/'chunk-%06d.ts'], 'source-'+name)
        files=sorted(folder.glob('chunk-*.ts')); inventory={p.name:sha(p) for p in files}
        write(folder/'inventory.json',inventory)
        source_index=[ts.index_chunk(p) for p in files]
        write(folder/'source-index.json',source_index)
        if len(files)!=seconds//g or any(x['video_pes']!=g*60 or abs(x['origin']-(round(F(entry['epoch'])*90000)+n*g*90000))>1 for n,x in enumerate(source_index)):
            raise ValueError('Generated source did not establish declared complete GOP layout')
        # One continuous TS for the source-audio oracle; only CC changes, no clock reset.
        self.assemble([(p,info,0,None) for p,info in zip(files,source_index)],folder/'oracle.ts')
        pcm=self.run(['-v','error','-copyts','-i',folder/'oracle.ts','-map','0:a:0','-af',
            f'atrim=start={epoch:.9f}:end={epoch+seconds:.9f},asetpts=PTS-STARTPTS',
            '-ar','48000','-ac','2','-f','f32le','-'], 'source-audio-'+name)
        return dict(folder=folder,entry=entry,files=files,inventory=inventory,pcm=np.frombuffer(pcm,np.float32).reshape(-1,2).copy())
    def assemble(self,pieces,dest):
        self.reserve(16000000)
        counters={}
        with self.track_live([dest]), open(dest,'xb') as output:
            for path,info,shift,keep in pieces:
                rows=ts.shifted(path,info,shift)
                if keep is not None: rows=rows[keep(ts.pids(rows))].copy()
                pid=ts.pids(rows)
                for identity in np.unique(pid):
                    loc=np.flatnonzero(pid==identity); payload=(rows[loc,3]&16)!=0
                    seq=np.cumsum(payload,dtype=np.int64)-1+counters.get(int(identity),0)
                    rows[loc,3]=(rows[loc,3]&240)|(seq&15).astype(np.uint8)
                    counters[int(identity)]=counters.get(int(identity),0)+int(payload.sum())
                output.write(rows.tobytes())
                if output.tell()>16000000: raise RuntimeError('Assembly derivative bound')
    def export(self,source,label,start,duration):
        started=time.perf_counter(); start,duration=F(start),F(duration); end=start+duration
        first,last=math.ceil(start*60),math.ceil(end*60); g=source['entry']['gop_seconds']; frames=g*60
        target=self.root/label; target.mkdir(); leases=contextlib.ExitStack()
        stages={}; clock=time.perf_counter(); pieces=[]; indices={}
        try:
            self.reserve(16000000)
            # Pin every closed input before export; index freshly inside each save.
            # AAC frames may straddle a video IDR/chunk boundary. Include the
            # preceding closed chunk's audio so trim can reconstruct the head;
            # its video is never emitted. Tail audio similarly gets one chunk.
            low=max(0,first//frames-1); high=min(len(source['files']), math.ceil(F(last,frames))+1)
            for n in range(low,high):
                original=source['files'][n]; p=target/original.name
                leases.enter_context(ts.fixture.Lease(original,source['inventory'][original.name]))
                if label=='minimum': shutil.copyfile(original,p)
                else: os.link(original,p)
                leases.enter_context(ts.fixture.Lease(p,source['inventory'][original.name]))
                indices[n]=(p,ts.index_chunk(p))
            origin=round(F(source['entry']['epoch'])*90000)
            for n,(p,info) in indices.items():
                if abs(info['origin']-(origin+n*g*90000))>1: raise ValueError('Source acquisition clock discontinuity')
            stages['protection_index_seconds']=time.perf_counter()-clock; clock=time.perf_counter()
            for n,(p,info) in indices.items():
                left,right=max(first,n*frames),min(last,(n+1)*frames)
                shift=EPOCH-round(start*90000)-origin
                if left==n*frames and right==(n+1)*frames:
                    pieces.append((p,info,shift,None)); continue
                pieces.append((p,info,shift,lambda pid:(pid!=256)&(pid!=257)))
                if left<right:
                    boundary=target/f'boundary-{n}.ts'
                    self.run(['-hide_banner','-v','warning','-nostdin','-i',p,'-map','0:v:0','-an',
                        '-vf',f'trim=start_frame={left-n*frames}:end_frame={right-n*frames},setpts=PTS-STARTPTS',
                        *ENC,'-g',str(frames),'-f','mpegts',boundary],label+f'-boundary-{n}')
                    if parameter_sets(p)!=parameter_sets(boundary): raise ValueError('SPS/PPS differ; avc1 splice rejected')
                    bm=ts.index_chunk(boundary,video_only=True)
                    pieces.append((boundary,bm,EPOCH+left*1500-round(start*90000)-bm['origin'],lambda pid:pid==256))
                pieces.append((p,info,shift,lambda pid:pid==257))
            stages['boundary_seconds']=time.perf_counter()-clock; clock=time.perf_counter()
            assembled=target/'input.ts'; self.assemble(pieces,assembled)
            partial=target/'result.partial.mp4'; final=target/'result.mp4'; epoch=EPOCH/90000
            self.run(['-hide_banner','-v','warning','-nostdin','-xerror','-copyts','-i',assembled,
                '-map','0:v:0','-map','0:a:0','-c:v','copy','-bsf:v',f'setts=pts=PTS-{epoch}/TB:dts=DTS-{epoch}/TB',
                '-af',f'atrim=start={epoch}:end={epoch+float(duration):.9f},asetpts=PTS-{epoch}/TB',
                '-c:a','aac','-b:a','192k','-ar','48000','-ac','2','-avoid_negative_ts','disabled',
                '-movflags','+faststart','-video_track_timescale','48000',partial],label+'-mux')
            stages['assembly_mux_seconds']=time.perf_counter()-clock; clock=time.perf_counter()
            schema=json.loads(self.run(['-v','error','-show_streams','-show_format','-of','json',partial],label+'-schema',probe=True))
            streams=schema['streams']
            if len(streams)!=2 or [(s['codec_type'],s['codec_name']) for s in streams]!=[('video','h264'),('audio','aac')]:
                raise ValueError('Unexpected output tracks/codecs')
            v,a=streams
            if (v['width'],v['height'],v['time_base'],a['sample_rate'],a['channels'])!=(W,H,'1/48000','48000',2):
                raise ValueError('Unexpected output schema')
            write(target/'schema.json',schema)
            # Same full closed-file decode and timing expression as current product.
            result=self.run(['-hide_banner','-loglevel','info','-xerror','-err_detect','explode','-copyts',
                '-progress','pipe:1','-nostats','-i',partial,'-filter_complex',
                "[0:v:0]split[full][timing];[timing]select='eq(n,0)+gt(abs((pts-prev_pts)*TB-0.016666667),0.0011)+gt(abs((pts-start_pts)*TB-n/60),0.000022)',showinfo,nullsink",
                '-map','[full]','-map','0:a:0','-fps_mode','passthrough','-f','null','-'],label+'-full')
            log=Path(self.rows[-1]['stderr']).read_text(errors='replace')
            counts=re.findall(rb'(?m)^frame=(\d+)\s*$',result)
            timing=re.findall(r'\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:([-0-9.e+]+)',log)
            if not counts or int(counts[-1])!=last-first or b'progress=end' not in result or len(timing)!=1:
                raise ValueError('Full runtime decode/count/drift rejection')
            first_pts=float(timing[0]); tolerance=1/48000+.000001
            if not -tolerance<=first_pts<=1/60+tolerance or abs(first_pts+(last-first)/60-float(duration))>1/60+tolerance:
                raise ValueError('Full runtime endpoint rejection')
            stages['full_validation_seconds']=time.perf_counter()-clock; clock=time.perf_counter()
            raw=self.run(['-v','error','-xerror','-err_detect','explode','-ignore_editlist','1','-i',partial,
                '-map','0:v:0','-an','-fps_mode','passthrough','-pix_fmt','rgb24','-f','rawvideo','-'],label+'-pictures')
            pixels=np.frombuffer(raw,np.uint8).reshape(-1,H,W,3); decoded=[]
            for image in pixels:
                value=0
                for bit in range(20):
                    a=image[4:12,bit*16+4:bit*16+12].mean()>128
                    b=image[20:28,bit*16+4:bit*16+12].mean()>128
                    if a==b: raise ValueError('Damaged frame-ID/complement')
                    value|=int(a)<<bit
                decoded.append(value)
            if decoded!=list(range(first,last)): raise ValueError('Physical frame IDs differ')
            audio=self.run(['-v','error','-xerror','-err_detect','explode','-i',partial,'-map','0:a:0',
                '-ar','48000','-ac','2','-f','f32le','-'],label+'-audio')
            pcm=np.frombuffer(audio,np.float32).reshape(-1,2)
            expected=source['pcm'][round(start*48000):round(end*48000)]
            if abs(len(pcm)-len(expected))>800: raise ValueError('Audio endpoint/count differs')
            checks=[]
            for pos in [0,max(0,len(expected)//2-1024),max(0,len(expected)-4096)]:
                size=min(2048,len(expected)-pos,len(pcm)-pos-16)
                matches=[]
                for lag in range(-16,17):
                    a=expected[pos:pos+size]; offset=pos+lag
                    if offset<0: continue
                    b=pcm[offset:offset+size]
                    corr=float(np.sum(a*b)/np.sqrt(np.sum(a*a)*np.sum(b*b)))
                    matches.append((corr,lag))
                corr,lag=max(matches); checks.append(dict(position=pos,correlation=corr,lag_samples=lag))
                write(target/'audio-alignment.json',dict(checks=checks,minimum_correlation=.97,maximum_lag_samples=4))
                if corr<.97 or abs(lag)>4: raise ValueError('Unique audio source alignment differs: '+str(checks[-1]))
            stages['independent_oracles_seconds']=time.perf_counter()-clock
            if not all(sha(p)==source['inventory'][p.name] for p,_ in indices.values()):
                raise ValueError('Protected source identity changed before test publication')
            # Test-only publication; current full decoder and independent oracles succeeded.
            os.rename(partial,final)
            receipt=dict(label=label,status='test-published',start=str(start),duration=str(duration),
                first_id=first,last_id=last-1,count=len(decoded),audio=checks,audio_samples=len(pcm),
                audio_alignment_threshold=dict(minimum_correlation=.97,maximum_lag_samples=4,windows=3),
                stages=stages,seconds_through_test_publication=time.perf_counter()-started,queue_seconds=0,
                sha256=sha(final),bytes=final.stat().st_size)
            write(target/'receipt.json',receipt); return receipt
        finally: leases.close()


def main():
    parser=argparse.ArgumentParser(); mode=parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--plan',action='store_true'); mode.add_argument('--prepare',action='store_true'); mode.add_argument('--run',action='store_true')
    parser.add_argument('--root',type=Path); parser.add_argument('--plan-file',type=Path); parser.add_argument('--expected-sha256')
    parser.add_argument('--engine',type=Path); args=parser.parse_args()
    if args.plan: print(json.dumps(preview(),indent=2)); return
    if args.prepare:
        root=args.root.resolve()
        if not root.is_relative_to(REPO/'artifacts'/'issue83') or root.exists(): raise ValueError('Fresh issue83 artifacts root required')
        engine=args.engine.resolve()
        if sha(engine).upper()!=ts.fixture.PINS['ExecutableSha256'].upper(): raise ValueError('Wrong pinned engine')
        probe=engine.with_name('ffprobe.exe')
        if sha(probe).upper()!=ts.fixture.PINS['ProbeSha256'].upper(): raise ValueError('Wrong pinned probe')
        identities=[HERE,HERE.with_suffix('.ps1'),HERE.with_name('indexed-reader-prototype.py'),HERE.with_name('precision-export-prototype.py'),engine,probe,Path(sys.executable)]
        identities += list(Path(np.__file__).parent.rglob('*.pyd'))
        identities += list((Path(np.__file__).parent.parent/'numpy.libs').glob('*.dll'))
        files={str(p):sha(p) for p in identities}
        plan=preview(); plan.update(root=str(root),engine=str(engine),probe=str(probe),python=sys.executable,files=files)
        root.mkdir(parents=True); write(args.plan_file,plan); print(sha(args.plan_file)); return
    if sha(args.plan_file).upper()!=args.expected_sha256.upper(): raise ValueError('Reviewed plan changed')
    plan=json.loads(args.plan_file.read_text())
    if plan['kind']!='hybrid-physical-precision-v1': raise ValueError('Wrong plan')
    root=Path(plan['root'])
    if any(root.iterdir()): raise ValueError('Existing attempt; preserve it')
    pilot=Pilot(plan); outcome=dict(status='failed',results=[])
    with contextlib.ExitStack() as pins:
        for path,digest in plan['files'].items(): pins.enter_context(ts.fixture.Lease(Path(path),digest))
        write(root/'admitted-plan.json',plan)
        try:
            sources={x['name']:pilot.source(x) for x in plan['sources']}
            for label,name,start,duration in CASES:
                outcome['results'].append(pilot.export(sources[name],label,start,duration))
                write(root/'outcome.json',outcome)
            outcome['status']='passed-construction-cohort'
        except Exception as ex:
            outcome['error']=str(ex); raise
        finally:
            outcome['seconds']=time.perf_counter()-pilot.started
            outcome['children']=len(pilot.rows); outcome['all_children_stopped']=all(x['stopped'] for x in pilot.rows)
            outcome['source_hashes_unchanged']=all(sha(p)==digest for folder in [root/'a',root/'b']
                if (folder/'inventory.json').exists() for name,digest in json.loads((folder/'inventory.json').read_text()).items() for p in [folder/name])
            if not outcome['source_hashes_unchanged']: outcome.update(status='failed',error='Source hashes changed')
            write(root/'outcome.json',outcome)
            if not outcome['source_hashes_unchanged']: raise ValueError('Source integrity closure failed')


if __name__=='__main__': main()
