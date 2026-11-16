"""Finite offline feasibility on fresh issue83 recorder inputs; no live admission.

Actual TS/PES extents select pictures, rather than chunk ordinal/GOP duration.
Source/current-product content oracles and fresh full output decode precede the
test-only final rename. No installed recorder, capture or preferences changes.
"""
from __future__ import annotations
import argparse, contextlib, importlib.util, json, math, os, re, sys, threading, time
from fractions import Fraction as F
from pathlib import Path
import numpy as np

HERE=Path(__file__).resolve(); REPO=HERE.parents[1]
s=importlib.util.spec_from_file_location('hybrid',HERE.with_name('replay-hybrid-precision.py'))
h=importlib.util.module_from_spec(s); s.loader.exec_module(h)
sha,write=h.sha,h.write
CROP='crop=576:48:16:8,scale=24:2:flags=area,format=gray'


def parent_resources():
    result={};creation,exit_time,kernel,user=(h.C.c_uint64() for _ in range(4))
    handle=h.C.c_void_p(-1)  # Windows GetCurrentProcess pseudo handle.
    if h.K.GetProcessTimes(handle,h.C.byref(creation),h.C.byref(exit_time),h.C.byref(kernel),h.C.byref(user)):
        result['cpu_seconds']=(kernel.value+user.value)/10000000
    else:result['cpu_query_error']=h.C.get_last_error()
    io=h.C.create_string_buffer(48)
    if h.K.GetProcessIoCounters(handle,io):
        result['read_bytes']=h.C.c_uint64.from_buffer(io,24).value;result['write_bytes']=h.C.c_uint64.from_buffer(io,32).value
    else:result['io_query_error']=h.C.get_last_error()
    return result


def audio_extents(path,meta):
    """Derive AAC LC coverage; ADTS may span TS packets, not PES boundaries."""
    rows=np.frombuffer(path.read_bytes(),np.uint8).reshape(-1,188)
    starts={p['row']:p for p in meta['pes'] if p['pid']==257}; pending=None; payload=bytearray(); extents=[]
    def finish():
        if pending is None:return
        data=bytes(payload); off=0; samples=0
        while off<len(data):
            b=data[off:off+7]
            if len(b)<7 or b[0]!=255 or b[1]&246!=240 or b[2]>>6!=1 or (b[2]>>2)&15!=3 or ((b[2]&1)<<2 | b[3]>>6)!=2 or b[6]&3:
                raise ValueError('Unsupported AAC LC48k stereo ADTS frame')
            length=((b[3]&3)<<11)|(b[4]<<3)|(b[5]>>5)
            if length<7 or off+length>len(data):raise ValueError('Truncated ADTS frame')
            off+=length; samples+=1024
        extents.append((pending['pts'],pending['pts']+samples*90000//48000,samples))
    for n in np.flatnonzero((h.ts.pids(rows)==257)&((rows[:,3]&16)!=0)):
        row=rows[n]
        off=4+(int(row[4])+1 if row[3]&32 else 0)
        if n in starts:
            finish(); pending=starts[n]; payload=bytearray(); off+=9+int(row[off+8])
        if pending is not None:payload.extend(row[off:])
    finish()
    if not extents:raise ValueError('AAC coverage missing')
    return extents


class Reference(h.Pilot):
    def __init__(self,plan):
        super().__init__(plan);self.observer_lock=threading.RLock();self.observed_at=None;self.observed=None
        self.observer=dict(scans=0,work_seconds=0.,maximum_scan_seconds=0.,maximum_gap_seconds=0.)
    def observe(self,force=False):
        with self.observer_lock:
            if not force and self.observed_at is not None and time.perf_counter()-self.observed_at<.2:
                # Other retained roots are closed. Fresh native active-output
                # sizes must still overlay the cached closed-tree accounting.
                local=self.used_bytes(self.root)
                return local,self.observed[1]+local-self.observed[0]
            t=time.perf_counter();local=self.used_bytes(self.root)
            retained=sum(self.used_bytes(root) for root in self.plan['retained_roots'])
            completed=time.perf_counter();work=completed-t
            if self.observed_at is not None:self.observer['maximum_gap_seconds']=max(self.observer['maximum_gap_seconds'],completed-self.observed_at)
            self.observer['scans']+=1;self.observer['work_seconds']+=work;self.observer['maximum_scan_seconds']=max(self.observer['maximum_scan_seconds'],work)
            self.observed_at=completed;self.observed=(local,retained);return self.observed
    def guard(self):
        if time.perf_counter()-self.started>self.bounds['seconds']:raise ValueError('Whole-cohort deadline')
        if h.shutil.disk_usage(self.root).free<self.bounds['minimum_free_bytes']:raise ValueError('Free-space floor')
        local,retained=self.observe()
        if local>self.bounds['early_stop_bytes'] or retained>19500000000:raise ValueError('Retained-media early stop')
    def reserve(self,forward):
        self.guard();local,retained=self.observe(force=True)
        if local+forward+self.bounds['closure_reserve_bytes']>self.bounds['output_bytes']:raise ValueError('Local forward+closure reservation')
        if retained+forward+self.bounds['closure_reserve_bytes']>20000000000:
            raise ValueError('Cumulative next-stage plus closure reservation')
    def assemble(self,pieces,dest):
        self.reserve(self.bounds['stage_derivative_bytes']); counters={}
        with self.track_live([dest]), open(dest,'xb') as out:
            for p,meta,shift,keep in pieces:
                self.guard(); rows=h.ts.shifted(p,meta,shift)
                if keep is not None:rows=rows[keep(h.ts.pids(rows))].copy()
                pids=h.ts.pids(rows)
                for pid in np.unique(pids):
                    loc=np.flatnonzero(pids==pid); present=(rows[loc,3]&16)!=0
                    seq=np.cumsum(present,dtype=np.int64)-1+counters.get(int(pid),0)
                    rows[loc,3]=(rows[loc,3]&240)|(seq&15).astype(np.uint8)
                    counters[int(pid)]=counters.get(int(pid),0)+int(present.sum())
                out.write(rows.tobytes())
                if out.tell()>self.bounds['stage_derivative_bytes']:raise ValueError('Assembly cap')
    def ids(self,raw):
        rows=np.frombuffer(raw,np.uint8).reshape(-1,2,24)
        if np.any((rows>64)&(rows<192)) or np.any((rows[:,0]>128)==(rows[:,1]>128)):
            raise ValueError('Unresolved source-ID/complement cells')
        return [sum(int(v)<<bit for bit,v in enumerate(row)) for row in rows[:,0]>128]
    def run_case(self,case):
        started=time.perf_counter(); label=case['label']; target=self.root/label; target.mkdir()
        start=F(case['start']); duration=F(case['duration']); end=start+duration
        first,last=math.ceil(start*60),math.ceil(end*60); stages={}; chunks=[]; pieces=[]
        self.progress=dict(label=label,start=str(start),duration=str(duration),stages=stages,status='working',parent_resource_start=parent_resources(),parent_resource_checkpoints={})
        def checkpoint(name,seconds):
            stages[name]=seconds;self.progress['seconds_so_far']=time.perf_counter()-started
            self.progress['parent_resource_checkpoints'][name]=parent_resources()
            write(target/'stage-progress.json',self.progress)
        clock=time.perf_counter()
        with contextlib.ExitStack() as leases:
            # No index/payload work is prepared outside the service timer.
            previous=None; parameters=None
            for entry in self.plan['source_inputs']:
                self.guard()
                p=Path(entry['path']); leases.enter_context(h.ts.fixture.Lease(p,entry['sha256']))
                meta=h.ts.index_chunk(p); video=[x['pts'] for x in meta['pes'] if x['pid']==256]
                frame_ids=[round(F(t,1500)) for t in video]
                if any(abs(t-n*1500)>1 for t,n in zip(video,frame_ids)) or frame_ids!=list(range(frame_ids[0],frame_ids[0]+len(frame_ids))):
                    raise ValueError('Unsupported nonregular source picture clock')
                if previous is not None and frame_ids[0]!=previous+1:raise ValueError('Source picture gap')
                previous=frame_ids[-1]; ps=h.parameter_sets(p)
                if parameters is None:parameters=ps
                if ps!=parameters:raise ValueError('Source configuration changed')
                chunks.append(dict(path=p,meta=meta,frames=frame_ids,audio=audio_extents(p,meta)))
            if chunks[0]['frames'][0]>first or chunks[-1]['frames'][-1]+1<last:raise ValueError('Missing requested pictures')
            # Two AAC frames of decoder preroll are included by actual PES extents.
            audio_begin=math.floor(start*90000)-3840; audio_end=math.ceil(end*90000)
            coverage=sorted((a,b) for c in chunks for a,b,_ in c['audio'])
            selected=[x for x in coverage if x[1]>audio_begin and x[0]<audio_end]
            if not selected or selected[0][0]>audio_begin or selected[-1][1]<audio_end or any(a[1]!=b[0] for a,b in zip(selected,selected[1:])):
                raise ValueError('Missing contiguous AAC head/preroll/tail coverage')
            checkpoint('protection_index_audio_extents_seconds',time.perf_counter()-clock); clock=time.perf_counter()
            shift=h.EPOCH-round(start*90000); selected_inputs=[]
            for n,c in enumerate(chunks):
                frame_ids=c['frames']; left=max(first,frame_ids[0]); right=min(last,frame_ids[-1]+1)
                audio_needed=any(b>audio_begin and a<audio_end for a,b,_ in c['audio'])
                if left>=right and not audio_needed:continue
                p,meta=c['path'],c['meta']; selected_inputs.append(str(p))
                if left==frame_ids[0] and right==frame_ids[-1]+1:
                    pieces.append((p,meta,shift,None));continue
                pieces.append((p,meta,shift,lambda ids:(ids!=256)&(ids!=257)))
                if left<right:
                    boundary=target/f'boundary-{n}.ts'
                    self.run(['-hide_banner','-v','warning','-xerror','-nostdin','-i',p,'-map','0:v:0','-an',
                        '-vf',f'trim=start_frame={left-frame_ids[0]}:end_frame={right-frame_ids[0]},setpts=PTS-STARTPTS',
                        *h.ENC,'-g','60','-f','mpegts',boundary],label+f'-boundary-{n}')
                    if h.parameter_sets(boundary)!=parameters:raise ValueError('Boundary SPS/PPS incompatible; splice rejected')
                    bm=h.ts.index_chunk(boundary,video_only=True)
                    pieces.append((boundary,bm,h.EPOCH+left*1500-round(start*90000)-bm['origin'],lambda ids:ids==256))
                if audio_needed:pieces.append((p,meta,shift,lambda ids:ids==257))
            checkpoint('boundary_seconds',time.perf_counter()-clock);clock=time.perf_counter()
            assembled=target/'input.ts';self.assemble(pieces,assembled)
            checkpoint('assembly_seconds',time.perf_counter()-clock);clock=time.perf_counter()
            partial=target/'result.partial.mp4';final=target/'result.mp4'; epoch=h.EPOCH/90000
            self.run(['-hide_banner','-v','warning','-xerror','-nostdin','-copyts','-i',assembled,
                '-map','0:v:0','-map','0:a:0','-c:v','copy','-bsf:v',f'setts=pts=PTS-{epoch}/TB:dts=DTS-{epoch}/TB',
                '-af',f'atrim=start={epoch}:end={epoch+float(duration):.9f},asetpts=PTS-{epoch}/TB',
                '-c:a','aac','-b:a','192k','-ar','48000','-ac','2','-avoid_negative_ts','disabled',
                '-movflags','+faststart','-video_track_timescale','48000',partial],label+'-mux')
            checkpoint('mux_seconds',time.perf_counter()-clock);clock=time.perf_counter()
            schema=json.loads(self.run(['-v','error','-show_streams','-show_format','-of','json',partial],label+'-schema',probe=True))
            tracks=schema['streams']
            if len(tracks)!=2 or [(s['codec_type'],s['codec_name']) for s in tracks]!=[('video','h264'),('audio','aac')]:raise ValueError('Unexpected tracks')
            if (tracks[0]['width'],tracks[0]['height'],tracks[0]['time_base'],tracks[1]['sample_rate'],tracks[1]['channels'])!=(1920,1080,'1/48000','48000',2):raise ValueError('Unexpected schema')
            write(target/'schema.json',schema)
            raw=self.run(['-hide_banner','-loglevel','info','-xerror','-err_detect','explode','-copyts','-progress','pipe:1','-nostats','-i',partial,
                '-filter_complex',"[0:v:0]split[full][timing];[timing]select='eq(n,0)+gt(abs((pts-prev_pts)*TB-0.016666667),0.0011)+gt(abs((pts-start_pts)*TB-n/60),0.000022)',showinfo,nullsink",
                '-map','[full]','-map','0:a:0','-fps_mode','passthrough','-f','null','-'],label+'-full')
            log=Path(self.rows[-1]['stderr']).read_text(errors='replace');counts=re.findall(rb'(?m)^frame=(\d+)\s*$',raw)
            timing=re.findall(r'\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:([-0-9.e+]+)',log)
            if not counts or int(counts[-1])!=last-first or b'progress=end' not in raw or len(timing)!=1:raise ValueError('Fresh full decode/count/drift failed')
            pts=float(timing[0]);tol=1/48000+.000001
            if not -tol<=pts<=1/60+tol or abs(pts+(last-first)/60-float(duration))>1/60+tol:raise ValueError('Output endpoint failed')
            checkpoint('schema_full_validation_seconds',time.perf_counter()-clock);clock=time.perf_counter()
            candidate=self.ids(self.run(['-v','error','-xerror','-err_detect','explode','-ignore_editlist','1','-i',partial,'-map','0:v:0','-an','-vf',CROP,'-fps_mode','passthrough','-f','rawvideo','-'],label+'-candidate-ids'))
            input_start=F(self.plan['input_start']); source_filter=f'select=concatdec_select,settb=1/48000,setpts=round((PTS*TB+{float(input_start):.9f})*60)*800,trim=start={float(start):.9f}:end={float(end):.9f},'+CROP
            common=['-v','error','-xerror','-err_detect','explode','-copyts','-f','concat','-safe','1','-protocol_whitelist','file','-segment_time_metadata','1','-i',self.plan['concat']]
            source=self.ids(self.run(common+['-map','0:v:0','-an','-vf',source_filter,'-fps_mode','passthrough','-f','rawvideo','-'],label+'-source-ids'))
            if len(candidate)!=last-first or candidate!=source:raise ValueError('Every-picture original source identity mismatch')
            if label=='original-request':
                trusted=self.ids(self.run(['-v','error','-xerror','-err_detect','explode','-ignore_editlist','1','-i',self.plan['baseline_movie'],'-map','0:v:0','-an','-vf',CROP,'-fps_mode','passthrough','-f','rawvideo','-'],label+'-accepted-path-ids'))
                if candidate!=trusted:raise ValueError('Original accepted full-reencode picture identities differ')
            # Source oracle uses current accepted concat/audio selection, independently
            # from the indexed TS's clock rewriting. This source's repeating chirp
            # provides phase only; it is not an absolute audio-identity qualification.
            a=self.run(common+['-vn','-map','0:a:0','-af',f'aselect=concatdec_select,atrim=start={float(start)-float(input_start):.9f}:end={float(end)-float(input_start):.9f},asetpts=PTS-STARTPTS','-ar','48000','-ac','2','-f','f32le','-'],label+'-source-audio')
            b=self.run(['-v','error','-xerror','-err_detect','explode','-i',partial,'-vn','-map','0:a:0','-ar','48000','-ac','2','-f','f32le','-'],label+'-candidate-audio')
            a=np.frombuffer(a,np.float32).reshape(-1,2); b=np.frombuffer(b,np.float32).reshape(-1,2)
            if abs(len(a)-len(b))>800:raise ValueError('Audio length mismatch')
            checks=[]
            for pos in [0,48000,max(0,len(a)//2-2048),max(0,len(a)-8192)]:
                reference=a[pos:pos+4096].astype(float);matches=[]
                for lag in range(-16,17):
                    if pos+lag<0:continue
                    actual=b[pos+lag:pos+lag+4096].astype(float)
                    if actual.shape!=reference.shape:continue
                    denom=np.sqrt(np.sum(reference*reference)*np.sum(actual*actual))
                    if denom>0:matches.append((float(np.sum(reference*actual)/denom),lag))
                if not matches:raise ValueError('No audio alignment evidence')
                corr,lag=max(matches);checks.append(dict(position=pos,correlation=corr,lag_samples=lag))
                write(target/'audio-phase.json',checks)
                if corr<.97 or abs(lag)>4:raise ValueError('Audio phase diagnostic rejected')
            checkpoint('independent_source_oracles_seconds',time.perf_counter()-clock);clock=time.perf_counter()
            if not all(sha(Path(x['path']))==x['sha256'] for x in self.plan['source_inputs']):raise ValueError('Protected sources changed')
            checkpoint('prepublication_integrity_seconds',time.perf_counter()-clock)
            os.rename(partial,final)
            result=dict(label=label,status='offline-test-published',stages=stages,selected_inputs=selected_inputs,decoded_frames=len(candidate),
                source_id_sequence_sha256=h.hashlib.sha256(np.asarray(source,np.uint32).tobytes()).hexdigest(),audio_phase=checks,
                seconds_through_test_publication=time.perf_counter()-started,queue_seconds=0,sha256=sha(final),bytes=final.stat().st_size)
            write(target/'receipt.json',result);return result


def main():
    p=argparse.ArgumentParser();g=p.add_mutually_exclusive_group(required=True);g.add_argument('--prepare',action='store_true');g.add_argument('--run',action='store_true')
    p.add_argument('--root',type=Path);p.add_argument('--plan-file',type=Path,required=True);p.add_argument('--engine',type=Path);p.add_argument('--expected-sha256');args=p.parse_args()
    if args.prepare:
        root=args.root.resolve()
        if not root.is_relative_to(REPO/'artifacts/issue83') or root.exists():raise ValueError('Fresh owned issue83 root required')
        receipt=REPO/'artifacts/issue83/baseline-reports-02/first-use.json';original=json.loads(receipt.read_text());selection=original['selection']
        engine=args.engine.resolve();probe=engine.with_name('ffprobe.exe')
        if sha(engine).upper()!=h.ts.fixture.PINS['ExecutableSha256'].upper() or sha(probe).upper()!=h.ts.fixture.PINS['ProbeSha256'].upper():raise ValueError('Wrong engines')
        inputs=[dict(path=x['Path'],sha256=sha(Path(x['Path'])),bytes=Path(x['Path']).stat().st_size) for x in selection['Inputs']]
        concat=Path(inputs[0]['path']).parent/'concat.txt'; baseline=Path(original['final'])
        sizing_file=REPO/'artifacts/issue83/hybrid/retained-before-reference-01.json'
        sizing=json.loads(sizing_file.read_text());retained_roots=[x['root'] for x in sizing]
        retained=sum(h.logical_bytes(root) for root in retained_roots)
        if retained+7000000000>20000000000:raise ValueError('Retained evidence plus complete forward/closure budget cannot fit')
        observer_comparison=REPO/'artifacts/issue83/hybrid/observer-comparison-01.json'
        retirement_receipt=REPO/'artifacts/issue83/plans/retire-reference02-assembly-01.json.receipt.json'
        bindings=[HERE,HERE.with_name('replay-hybrid-precision.py'),HERE.with_name('replay-hybrid-precision.ps1'),HERE.with_name('indexed-reader-prototype.py'),HERE.with_name('precision-export-prototype.py'),receipt,engine,probe,Path(sys.executable),concat,baseline,sizing_file,observer_comparison,retirement_receipt]
        bindings+=list(Path(np.__file__).parent.rglob('*.pyd'))+list((Path(np.__file__).parent.parent/'numpy.libs').glob('*.dll'))
        start=F(str(selection['Start']));first_source=F(str(selection['Inputs'][0]['Start']))
        qualification_clock=time.perf_counter();idrm=h.ts.index_chunk(Path(inputs[1]['path']))
        idr=F(idrm['origin'],90000)
        case_qualification=dict(path=inputs[1]['path'],sha256=inputs[1]['sha256'],first_video_pts=idrm['origin'],seconds=time.perf_counter()-qualification_clock,
            limitation='Preflight selects a fixed actual chunk-start cut. It is independently reindexed inside every service timer; no index or payload is reused during a save.')
        bounds=h.preview()['bounds'];bounds.update(seconds=600,child_seconds=90,children=32,output_bytes=7000000000,early_stop_bytes=6300000000,stage_stdout_bytes=150000000,stage_derivative_bytes=1000000000,closure_reserve_bytes=500000000,outer_seconds=620,outer_memory_bytes=1610612736,cumulative_helpers_before=245,metadata_helpers=2,cumulative_helpers_ceiling=280)
        plan=dict(kind='indexed-reference-feasibility-v1',root=str(root),runner=str(HERE),python=sys.executable,engine=str(engine),probe=str(probe),bounds=bounds,
            files={str(x):sha(x) for x in bindings},source_inputs=inputs,concat=str(concat),input_start=str(first_source),baseline_movie=str(baseline),
            retained_roots=retained_roots,retained_logical_bytes_before=retained,cumulative_cap_bytes=20000000000,cumulative_early_stop_bytes=19500000000,
            observer=dict(method='os.scandir entry sizes; conservative logical alias charging; reject reparse',nominal_interval_seconds=.2,force_fresh_before_reservation=True,work_and_gap_costs_reported=True,operating_system_quota=False),
            case_qualification=case_qualification,cases=[dict(label='original-request',start=str(start),duration='300'),dict(label='idr-request',start=str(idr),duration='299'),dict(label='overlap-request',start=str(start+F(1,60)),duration='300')],
            effects='Offline only; serial boundary NVENC, audio AAC, full closed-output software decoding and full source/candidate ID/audio oracles. No capture/playback/worker/UI/Saved/catalog/settings/NVIDIA/startup/install changes.',
            claims='Finite full-duration construction/phase costs. No20-sample median/p95, native-validator equivalence, ordinary-worker storage qualification or latency acceptance.',
            exclusions='Regular60Hz H264/AAC LC48kstereo source only; variable chunk/multiple GOP extents derived from actual packets. Fulloutput fresh decode retained. Baseline audio is repeating and establishes phase only. Stop first failure; after a valid first case stop remaining trials if protection+index+boundaries+assembly/mux exceeds5seconds.',
            timing='Per-case locks and sourceSHA/index/AAC extents, boundary work, assembly,mux,schema,full closed-filedecode,independent originalsource content oracles,source rehash,test-onlyrename included; stage costs separate. Queue0; no ordinary Saved event.')
        root.mkdir(parents=True);write(args.plan_file,plan);print(sha(args.plan_file));return
    if sha(args.plan_file).upper()!=args.expected_sha256.upper():raise ValueError('Reviewed plan changed')
    plan=json.loads(args.plan_file.read_text());root=Path(plan['root'])
    if plan['kind']!='indexed-reference-feasibility-v1' or any(root.iterdir()):raise ValueError('Fresh admitted reference run required')
    h.ENC=[('20M' if x=='2M' else '40M' if x=='4M' else x) for x in h.ENC]
    runner=Reference(plan);outcome=dict(status='failed',results=[])
    with contextlib.ExitStack() as locks:
        for path,digest in plan['files'].items():locks.enter_context(h.ts.fixture.Lease(Path(path),digest))
        write(root/'admitted-plan.json',plan)
        try:
            for case in plan['cases']:
                result=runner.run_case(case);outcome['results'].append(result);write(root/'outcome.json',outcome)
                export=sum(result['stages'][k] for k in ['protection_index_audio_extents_seconds','boundary_seconds','assembly_seconds','mux_seconds'])
                if export>5:outcome['status']='feasibility-stopped-after-valid-case';break
            else:outcome['status']='passed-offline-construction'
        except Exception as ex:outcome['error']=str(ex);raise
        finally:
            outcome.update(seconds=time.perf_counter()-runner.started,children=len(runner.rows),all_children_stopped=all(x['stopped'] for x in runner.rows),observer=runner.observer,partial_progress=getattr(runner,'progress',None))
            write(root/'outcome.json',outcome)


if __name__=='__main__':main()
