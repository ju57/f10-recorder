"""Throwaway E1 generated-media experiment. No recorder, desktop/audio capture or UI.

Run --plan before --bootstrap or --prepare. All media/vendor bytes stay in an
explicit ignored artifacts directory. This is not the production export path.
"""
from __future__ import annotations
import argparse, ctypes, hashlib, json, math, os, re, shutil, statistics
import subprocess, sys, time, urllib.request, zipfile
from fractions import Fraction
from pathlib import Path
import numpy as np

REPO = Path(__file__).resolve().parents[1]
PIN_SOURCE = REPO/'src/MediaDependency.cs'
PIN_TEXT = PIN_SOURCE.read_text(encoding='utf-8')
def pin(name):
    return re.search(r'const string '+name+r' = "([^"]+)"', PIN_TEXT).group(1)
PINS = {k: pin(k) for k in ['PackageVersion','PackageUrl','PackageSha256','ExecutableSha256','ProbeSha256']}
FPS, W, H, RATE = 60, 1920, 1080, '20M'
ENC = ['-c:v','h264_nvenc','-preset','p3','-tune','ll','-rc','cbr','-b:v',RATE,'-maxrate',RATE,'-bufsize','40M','-g','60','-bf','0','-forced-idr','1','-flags','+cgop','-pix_fmt','yuv420p']
OFFSETS = [Fraction(0),Fraction(1,60),Fraction(7,60),Fraction(1,2),Fraction(59,60),Fraction(17,120),Fraction(43,120),Fraction(77,120),Fraction(119,120),Fraction(31,120)]

def sha(path):
    with open(path,'rb') as f:
        return hashlib.file_digest(f,'sha256').hexdigest()
def write(path, obj):
    path.write_text(json.dumps(obj,indent=2),encoding='utf-8')
def event(message):
    print(message,flush=True)
def plan():
    return {'kind':'PROTOTYPE E1, generated inputs only','version':1,
      'effects':{'desktop_pixels':False,'system_audio':False,'microphone':False,'ui':False,'shortcuts':False,'startup':False,'installation':False,'nvidia_setting_writes':False,'gpu_nvenc':True},
      'source':{'width':W,'height':H,'fps':FPS,'video':'H.264 NVENC p3/ll CBR20Mbps GOP60 bf0','audio':'generated 1kHz10ms pulses every second, stereo48kHz AAC192k','pixel_oracle':'20-bit frameID + complementary row, 16x16 cells','reference_seconds':312},
      'methods':['current ceil-GOP copy','input-seek edit-list copy','exact full reencode','hybrid boundary reencode + copied interior'],
      'reference_offsets':[str(v) for v in OFFSETS],
      'order':'10 first-pass then same10 repeat-pass, method order fixed after qualification; no claim OS cache is cold',
      'staging':'prepare312s closed segments; each trial snapshots inputs; no acquisition-tail wait in these preclosed fixtures',
      'concurrency':'one real-time generated1080p60 NVENC segment producer during reference export timing; no validation decode during timing',
      'timing':'snapshot/assembly/encode/mux/faststart/structural ffprobe/rename included; full pixel/audio/packet validation afterward',
      'feasibility_stop':'a full-encode reference >10s stops that method speed batch; preserve completed result, no median/p95 claim for n<20',
      'targets':{'video_boundary_rounding_frames':1,'median_seconds':3,'p95_seconds':5,'audio_marker_tolerance_seconds':.050},
      'storage':{'expected_bytes':35_000_000_000,'budget_bytes':45_000_000_000,'minimum_free_bytes':40_000_000_000,'trial_reserve_bytes':2_000_000_000,'finalization_reserve_bytes':2_000_000_000,'poll_growth_reserve_bytes':500_000_000},
      'limits':'single H264 reference; not game/live capture/M0 acceptance; no true uncached OS I/O condition; preserve rejected attempts'}

class Lease:
    """Pin authenticated executable bytes against write/delete while subprocess runs."""
    def __init__(self,path,expected):
        self.path=path; self.expected=expected; self.handle=None
    def __enter__(self):
        if os.name!='nt': raise RuntimeError('This experiment requires Windows leases/NVENC')
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p]
        kernel.CreateFileW.restype=ctypes.c_void_p
        self.handle=kernel.CreateFileW(str(self.path),0x80000000,1,None,3,0x80,None)
        if self.handle==ctypes.c_void_p(-1).value: raise ctypes.WinError(ctypes.get_last_error())
        if sha(self.path)!=self.expected:
            self.__exit__(None,None,None); raise RuntimeError('Executable SHA256 mismatch: '+str(self.path))
        return self
    def __exit__(self,*args):
        if self.handle is not None:
            k=ctypes.WinDLL('kernel32',use_last_error=True)
            k.CloseHandle.argtypes=[ctypes.c_void_p]; k.CloseHandle(self.handle); self.handle=None

class Experiment:
    def __init__(self,root):
        self.root=root.resolve(); base=(REPO/'artifacts').resolve()
        if not self.root.is_relative_to(base): raise RuntimeError('Output must stay under repository artifacts')
        self.exe=self.root/'vendor'/'ffmpeg.exe'; self.probe=self.root/'vendor'/'ffprobe.exe'
        receipt=self.root/'commands.json'
        self.commands=json.loads(receipt.read_text()) if receipt.exists() else []
        self.seq=len(self.commands)
        self.faststart=True
        self.probe_options=[]
    def check_space(self):
        st=plan()['storage']; identities={}
        for p in self.root.rglob('*'):
            if p.is_file():
                s=p.stat();identities[(s.st_dev,s.st_ino)]=s.st_size
        used=sum(identities.values())
        if used>st['budget_bytes']-st['trial_reserve_bytes'] or shutil.disk_usage(self.root).free<st['minimum_free_bytes']:
            raise RuntimeError('Experiment storage admission rejected')
        if not self.faststart or self.probe_options:
            combined={}
            for group in self.root.parent.glob('precision-export-20261024-*'):
                for p in group.rglob('*'):
                    if p.is_file():
                        s=p.stat();combined[(s.st_dev,s.st_ino)]=s.st_size
            if sum(combined.values())>88_000_000_000:raise RuntimeError('Combined90GB evidence admission rejected (2GB next-trial reserve)')
    def run(self,args,label,probe=False,timeout=600,stdout=None):
        self.seq+=1; tool=self.probe if probe else self.exe
        expected=PINS['ProbeSha256' if probe else 'ExecutableSha256']
        log=self.root/'logs'/f'{self.seq:04d}-{label}.stderr.txt'; log.parent.mkdir(exist_ok=True)
        row={'label':label,'args':[str(x) for x in args],'tool':str(tool),'sha256':expected,'harness_sha256':sha(Path(__file__)),'started_utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}
        with Lease(tool,expected), open(log,'wb') as err:
            start=time.perf_counter()
            try:
                p=subprocess.run([str(tool)]+list(map(str,args)),cwd=self.root,stdout=stdout if stdout is not None else subprocess.PIPE,stderr=err,timeout=timeout,creationflags=0x08000000)
                row.update(seconds=time.perf_counter()-start,returncode=p.returncode)
            except subprocess.TimeoutExpired:
                row.update(seconds=time.perf_counter()-start,returncode='timeout'); self.commands.append(row);write(self.root/'commands.json',self.commands);raise
        self.commands.append(row);write(self.root/'commands.json',self.commands)
        if p.returncode:
            raise RuntimeError(label+' failed: '+log.read_text(errors='replace')[-1800:])
        return p.stdout,row['seconds']
    def bootstrap(self,package=None):
        self.root.mkdir(parents=True,exist_ok=True)
        if (self.root/'plan.json').exists(): raise RuntimeError('Use a fresh experiment directory')
        write(self.root/'plan.json',plan()); write(self.root/'pins.json',PINS)
        archive=self.root/'vendor-package.zip'
        if package:
            if sha(package)!=PINS['PackageSha256']:raise RuntimeError('Cached package hash mismatch')
            shutil.copyfile(package,archive);event('Reusing independently hash-verified cached official package')
        else:
            event('Downloading pinned official engine into experiment directory')
            with urllib.request.urlopen(PINS['PackageUrl'],timeout=120) as src,open(archive,'wb') as out: shutil.copyfileobj(src,out,1024*1024)
        actual=sha(archive); write(self.root/'package-receipt.json',{'expected':PINS['PackageSha256'],'actual':actual,'bytes':archive.stat().st_size})
        if actual!=PINS['PackageSha256']: raise RuntimeError('Package hash mismatch; retained without execution')
        (self.root/'vendor').mkdir()
        with zipfile.ZipFile(archive) as z:
            for name,expected in [('ffmpeg.exe',PINS['ExecutableSha256']),('ffprobe.exe',PINS['ProbeSha256'])]:
                entry=next(n for n in z.namelist() if n.endswith('/bin/'+name))
                with z.open(entry) as src,open(self.root/'vendor'/name,'wb') as out: shutil.copyfileobj(src,out)
                if sha(self.root/'vendor'/name)!=expected: raise RuntimeError('Tool hash mismatch')
        data,_=self.run(['-version'],'version'); (self.root/'version.txt').write_bytes(data)
        self.run(['-hide_banner','-h','encoder=h264_nvenc'],'nvenc-options')
        event('Pinned package and both tool hashes verified')
    def source_args(self,seconds):
        filters=['drawbox=x=0:y=0:w=320:h=32:color=black:t=fill']
        for bit in range(20):
            for row in range(2):
                enable=f"{'not(' if row else ''}mod(floor(n/{1<<bit}),2){')' if row else ''}"
                filters.append(f"drawbox=x={bit*16}:y={row*16}:w=16:h=16:color=white:t=fill:enable='{enable}'")
        return ['-f','lavfi','-i',f'testsrc2=size={W}x{H}:rate={FPS}:duration={seconds}',
          '-f','lavfi','-i',rf"aevalsrc=if(lt(mod(t\,1)\,0.01)\,0.7*sin(2*PI*1000*t)\,0):s=48000:d={seconds}",
          '-vf',','.join(filters),'-map','0:v:0','-map','1:a:0']+ENC+['-c:a','aac','-b:a','192k','-ar','48000','-ac','2','-force_key_frames','expr:gte(t,n_forced*1)']
    def generate(self,name,seconds):
        self.check_space(); out=self.root/name; out.mkdir(exist_ok=False)
        args=['-hide_banner','-loglevel','warning','-nostdin']+self.source_args(seconds)+['-f','segment','-segment_time','1','-segment_format','mpegts','-reset_timestamps','1','-segment_list',out/'segments.csv','-segment_list_type','csv',out/'chunk-%06d.ts']
        _,elapsed=self.run(args,'generate-'+name,timeout=1200)
        event(f'Generated {name} in {elapsed:.2f}s')
        return out
    def concat(self,path,files,durations=None):
        lines=['ffconcat version 1.0']
        for i,p in enumerate(files):
            lines.append("file '"+str(p.resolve()).replace('\\','/').replace("'","'\\''")+"'")
            for key,value in self.probe_options:lines.append(f'option {key} {value}')
            if durations is not None: lines.append(f'duration {durations[i]:.9f}')
        path.write_text('\n'.join(lines)+'\n',encoding='utf-8')
    def snapshot(self,source,start,end,target):
        low=math.floor(start);high=math.ceil(end)+1
        files=[]
        for second in range(low,high):
            original=source/f'chunk-{second:06d}.ts'; dest=target/original.name
            if not original.exists():raise RuntimeError('Missing closed fixture segment '+str(original))
            os.link(original,dest);files.append(dest)
        self.concat(target/'source.ffconcat',files,[1.0]*len(files))
        return low,files
    def export(self,source,start,duration,method,label):
        self.check_space(); target=self.root/'exports'/label;target.mkdir(parents=True,exist_ok=False)
        start=Fraction(start);end=start+Fraction(duration)
        first,last=math.ceil(start*FPS),math.ceil(end*FPS)-1
        receipt={'label':label,'method':method,'start':str(start),'end':str(end),'duration':str(duration),
          'expected_first_id':first,'expected_last_id':last,'expected_count':last-first+1,'status':'started','faststart':self.faststart,'probe_options':self.probe_options}
        write(target/'receipt.json',receipt)
        tick=time.perf_counter()
        try:
            low,files=self.snapshot(source,start,end,target)
            source_concat=target/'source.ffconcat'
            partial=target/'result.partial.mp4'; final=target/'result.mp4'
            base=['-hide_banner','-loglevel','warning','-nostdin']
            mux=(['-movflags','+faststart'] if self.faststart else [])+['-video_track_timescale','90000']
            if method=='current':
                begin=math.ceil(start);selected=files[begin-low:]
                current=target/'current.ffconcat';self.concat(current,selected,[1.0]*len(selected))
                args=base+['-f','concat','-safe','0','-i',current,'-t',str(float(end-begin)),'-map','0:v:0','-map','0:a:0','-c','copy','-avoid_negative_ts','make_zero']+mux+[partial]
            elif method=='edit':
                args=base+['-ss',str(float(start-low)),'-f','concat','-safe','0','-i',source_concat,'-t',str(float(duration)),'-map','0:v:0','-map','0:a:0','-c','copy','-avoid_negative_ts','disabled','-use_editlist','1']+mux+[partial]
            elif method=='indexed-edit':
                indexed=target/'indexed-input.mp4'
                self.run(base+['-f','concat','-safe','0','-i',source_concat,'-map','0:v:0','-map','0:a:0','-c','copy','-avoid_negative_ts','make_zero',indexed],label+'-index')
                args=base+['-ss',str(float(start-low)),'-i',indexed,'-t',str(float(duration)),'-map','0:v:0','-map','0:a:0','-c','copy','-avoid_negative_ts','disabled','-use_editlist','1']+mux+[partial]
            elif method=='direct-bsf':
                prefix,_=self.run(['-v','error','-f','concat','-safe','0','-read_intervals','%+0.1','-show_packets','-show_entries','packet=stream_index,pts_time','-of','json',source_concat],label+'-origin',probe=True)
                origin=float(next(p['pts_time'] for p in json.loads(prefix)['packets'] if p['stream_index']==0))
                shift=origin+float(start-low)
                bsf=f'setts=pts=PTS-{shift:.9f}/TB:dts=DTS-{shift:.9f}/TB'
                # BSF is intentionally after stream-copy admission, preserving the preceding IDR.
                args=base+['-f','concat','-safe','0','-i',source_concat,'-t',str(float(duration)+shift),'-map','0:v:0','-map','0:a:0','-c','copy','-bsf:v',bsf,'-bsf:a',bsf,'-avoid_negative_ts','disabled','-use_editlist','1']+mux+[partial]
            elif method=='full':
                offset=first-low*FPS;count=last-first+1
                args=base+['-f','concat','-safe','0','-i',source_concat,'-vf',f'trim=start_frame={offset}:end_frame={offset+count},setpts=PTS-STARTPTS',
                  '-af',f'atrim=start_sample={offset*800}:end_sample={(offset+count)*800},asetpts=PTS-STARTPTS','-map','0:v:0','-map','0:a:0']+ENC+['-c:a','aac','-b:a','192k','-ar','48000','-ac','2']+mux+[partial]
            elif method=='hybrid':
                pieces=[];lengths=[]
                # Split exact frame interval at qualified one-second closed GOPs.
                for sec in range(first//FPS,last//FPS+1):
                    left=max(first,sec*FPS);right=min(last+1,(sec+1)*FPS)
                    original=target/f'chunk-{sec:06d}.ts'
                    if left==sec*FPS and right==(sec+1)*FPS:
                        pieces.append(original);lengths.append(1.0);continue
                    boundary=target/f'boundary-{sec:06d}.ts'
                    self.run(base+['-i',original,'-map','0:v:0','-an','-vf',f'trim=start_frame={left-sec*FPS}:end_frame={right-sec*FPS},setpts=PTS-STARTPTS']+ENC+['-f','mpegts',boundary],label+'-boundary')
                    pieces.append(boundary);lengths.append((right-left)/FPS)
                video_concat=target/'hybrid.ffconcat';self.concat(video_concat,pieces,lengths)
                offset=first-low*FPS;count=last-first+1
                args=base+['-f','concat','-safe','0','-i',video_concat,'-f','concat','-safe','0','-i',source_concat,
                  '-map','0:v:0','-map','1:a:0','-c:v','copy','-af',f'atrim=start_sample={offset*800}:end_sample={(offset+count)*800},asetpts=PTS-STARTPTS',
                  '-c:a','aac','-b:a','192k','-ar','48000','-ac','2','-t',str(float(duration))]+mux+[partial]
            else:raise ValueError(method)
            self.run(args,label+'-export',timeout=600)
            data,_=self.run(['-v','error','-show_streams','-show_format','-of','json',partial],label+'-structural',probe=True)
            meta=json.loads(data);write(target/'structural.json',meta)
            if [s['codec_type'] for s in meta['streams']]!=['video','audio']: raise RuntimeError('Wrong output stream schema')
            if meta['streams'][0]['width']!=W or meta['streams'][0]['height']!=H:raise RuntimeError('Wrong output size')
            if meta['streams'][0]['codec_name']!='h264' or meta['streams'][1]['codec_name']!='aac' or meta['streams'][1]['sample_rate']!='48000' or meta['streams'][1]['channels']!=2:raise RuntimeError('Wrong codec/audio format')
            os.rename(partial,final)
            receipt.update(status='committed',seconds=time.perf_counter()-tick,bytes=final.stat().st_size)
        except Exception as ex:
            receipt.update(status='failed',seconds=time.perf_counter()-tick,error=str(ex));write(target/'receipt.json',receipt);event(f'{label}: FAILED {ex}');return receipt
        write(target/'receipt.json',receipt);event(f"{label}: {receipt['seconds']:.3f}s ({receipt['bytes']/1e6:.1f}MB)");return receipt
    def validate(self,target,ignore_edits=False):
        receipt=json.loads((target/'receipt.json').read_text());file=target/'result.mp4'
        if receipt['status']!='committed':return {'pass':False,'reason':'no committed output'}
        extra=['-ignore_editlist','1'] if ignore_edits else []
        raw,_=self.run(['-hide_banner','-v','error','-xerror']+extra+['-i',file,'-map','0:v:0','-an','-vf','crop=320:32:0:0,scale=20:2:flags=area,format=gray','-fps_mode','passthrough','-pix_fmt','gray','-f','rawvideo','-'],target.name+'-pixels')
        cells=np.frombuffer(raw,dtype=np.uint8).reshape(-1,2,20)
        bits=cells>128
        ambiguous=np.logical_and(cells>64,cells<192).any(axis=(1,2))
        valid=(bits[:,0,:]!=bits[:,1,:]).all(axis=1)&~ambiguous
        ids=(bits[:,0,:].astype(np.int64)*(1<<np.arange(20))).sum(axis=1)
        contiguous=bool(np.all(np.diff(ids)==1))
        expected_first=receipt['expected_first_id'];expected_last=receipt['expected_last_id']
        start=float(Fraction(receipt['start']));end=float(Fraction(receipt['end']))
        result={'decoded_frames':len(ids),'first_id':int(ids[0]),'last_id':int(ids[-1]),'all_ids_valid':bool(valid.all()),'contiguous_ids':contiguous,
          'exact_ceil_interval':bool(ids[0]==expected_first and ids[-1]==expected_last and len(ids)==receipt['expected_count']),
          'start_error_frames':float(ids[0]-start*FPS),'end_error_frames':float(ids[-1]+1-end*FPS),'duration_error_frames':len(ids)-float(Fraction(receipt['duration'])*FPS),
          'ids_sha256':hashlib.sha256(ids.tobytes()).hexdigest()}
        np.save(target/('ids-no-edit.npy' if ignore_edits else 'ids.npy'),ids)
        if ignore_edits:write(target/'validation-no-edit.json',result);return result
        data,_=self.run(['-v','error','-show_packets','-show_entries','packet=stream_index,pts_time,dts_time,duration_time','-of','json',file],target.name+'-packets',probe=True)
        packets=json.loads(data)['packets'];timestamp_checks={}
        for stream in [0,1]:
            stream_packets=[p for p in packets if p['stream_index']==stream]
            dts=[float(p['dts_time']) for p in stream_packets if 'dts_time' in p]
            pts=[float(p['pts_time']) for p in stream_packets if 'pts_time' in p]
            timestamp_checks[str(stream)]={'packets':len(stream_packets),'all_have_dts':len(dts)==len(stream_packets),'all_have_pts':len(pts)==len(stream_packets),'dts_strictly_increasing':all(b>a for a,b in zip(dts,dts[1:])),'pts_strictly_increasing':all(b>a for a,b in zip(pts,pts[1:]))}
            if stream==0:
                timestamp_checks[str(stream)]['maximum_frame_interval_error_seconds']=max((abs((b-a)-1/FPS) for a,b in zip(pts,pts[1:])),default=999)
        result['timestamps']=timestamp_checks
        audio,_=self.run(['-hide_banner','-v','error','-xerror','-i',file,'-map','0:a:0','-vn','-ac','1','-ar','48000','-c:a','pcm_f32le','-f','f32le','-'],target.name+'-audio')
        pcm=np.frombuffer(audio,dtype=np.float32);blocks=len(pcm)//48
        energy=np.sqrt(np.mean(pcm[:blocks*48].reshape(-1,48)**2,axis=1))
        active=energy>.08
        onsets=np.flatnonzero(active&~np.r_[False,active[:-1]])/1000
        # Combine codec ringing into a single physical pulse.
        pulses=[]
        for v in onsets:
            if not pulses or v-pulses[-1]>.100:pulses.append(float(v))
        intended_start=expected_first/FPS
        expected=[t-intended_start for t in range(math.ceil(intended_start),math.ceil(expected_last/FPS)) if .050<t-intended_start<float(Fraction(receipt['duration']))-.050]
        errors=[min((abs(v-t) for v in pulses),default=999) for t in expected]
        unexpected=[v for v in pulses if .050<v<float(Fraction(receipt['duration']))-.050 and min((abs(t-v) for t in expected),default=999)>.050]
        result['audio']={'samples':len(pcm),'decode_duration_seconds':len(pcm)/48000,'detected_pulses':len(pulses),'expected_interior_pulses':len(expected),'unexpected_interior_pulses':unexpected,'maximum_marker_error_seconds':max(errors,default=0),'first_pulse_seconds':pulses[0] if pulses else None}
        write(target/'audio-markers.json',{'detected':pulses,'expected':expected,'errors':errors})
        result['pass']=bool(result['all_ids_valid'] and contiguous and max(abs(result[k]) for k in ['start_error_frames','end_error_frames','duration_error_frames'])<=1.00001 and all(v['all_have_dts'] and v['all_have_pts'] and v['dts_strictly_increasing'] and v['pts_strictly_increasing'] for v in timestamp_checks.values()) and timestamp_checks['0']['maximum_frame_interval_error_seconds']<=1/90000+1e-6 and max(errors,default=999)<=.050 and not unexpected and abs(len(pcm)/48000-float(Fraction(receipt['duration'])))<=.050)
        result['validator_harness_sha256']=sha(Path(__file__))
        if (target/'validation.json').exists():
            history=target/'validation-history.json';old=json.loads(history.read_text()) if history.exists() else []
            old.append(json.loads((target/'validation.json').read_text()));write(history,old)
        write(target/'validation.json',result);event(f"validate {target.name}: {'PASS' if result['pass'] else 'FAIL'} IDs {result['first_id']}..{result['last_id']} count{len(ids)} maxaudio{max(errors,default=999):.3f}s")
        return result

    def qualification(self):
        out=self.root/'qualification'
        if not out.exists():self.generate('qualification',12)
        results=[]
        cases=[('idr',Fraction(2)),('after',Fraction(2)+Fraction(1,60)),('half',Fraction(5,2)),('before',Fraction(2)+Fraction(59,60)),('subframe',Fraction(2)+Fraction(43,120))]
        for method in ['current','full','edit','hybrid']:
            for label,start in cases:
                name=f'qual-{method}-{label}'
                row=self.export(out,start,Fraction(5),method,name)
                if row['status']=='committed':
                    try:row['validation']=self.validate(self.root/'exports'/name)
                    except Exception as ex:row['validation']={'pass':False,'error':str(ex)};event(str(ex))
                    if method=='edit':
                        try:row['physical_without_edits']=self.validate(self.root/'exports'/name,True)
                        except Exception as ex:row['physical_without_edits']={'error':str(ex)}
                results.append(row);write(self.root/'qualification-results.json',results)
        event('Qualification finished')

    def qualify_indexed(self):
        amendment={'reason':'Test independently proposed indexed-MP4 edit-list path; indexing included in export timing, retained indexed file counts toward storage budget','plan_sha256':sha(self.root/'plan.json'),'harness_sha256':sha(Path(__file__)),'new_method':'indexed-edit','phase':'before reference speed collection'}
        write(self.root/'indexed-amendment.json',amendment)
        results=[]
        for label,start in [('idr',Fraction(2)),('after',Fraction(2)+Fraction(1,60)),('half',Fraction(5,2)),('before',Fraction(2)+Fraction(59,60)),('subframe',Fraction(2)+Fraction(43,120))]:
            name='qual-indexed-'+label;row=self.export(self.root/'qualification',start,Fraction(5),'indexed-edit',name)
            if row['status']=='committed':
                row['validation']=self.validate(self.root/'exports'/name)
                row['physical_without_edits']=self.validate(self.root/'exports'/name,True)
            results.append(row);write(self.root/'indexed-qualification-results.json',results)

    def revalidate_short(self):
        for name in ['qualification-results.json','indexed-qualification-results.json']:
            path=self.root/name;rows=json.loads(path.read_text());backup=path.with_name(path.stem+'-initial.json')
            if not backup.exists():shutil.copyfile(path,backup)
            for row in rows:
                if row['status']=='committed':
                    try:row['validation']=self.validate(self.root/'exports'/row['label'])
                    except Exception as ex:row['validation']={'pass':False,'error':str(ex)}
            write(path,rows)

    def seed_from(self,other):
        other=other.resolve()
        if not other.is_relative_to((REPO/'artifacts').resolve()):raise RuntimeError('Seed must be retained experiment evidence')
        target=self.root/'reference'
        if target.exists() and any(target.iterdir()):raise RuntimeError('Seed target is not empty')
        target.mkdir(exist_ok=True)
        for p in (other/'reference').iterdir():
            if p.is_file():os.link(p,target/p.name)
        receipts=[]
        for name in ['qualification-results.json','indexed-qualification-results.json','direct-qualification-results.json','environment.json']:
            shutil.copyfile(other/name,self.root/name);receipts.append({'file':name,'sha256':sha(other/name)})
        if (other/'finalization-probe-results.json').exists():
            shutil.copyfile(other/'finalization-probe-results.json',self.root/'finalization-probe-results.json')
        if (other/'configuration-probe-results.json').exists():
            shutil.copyfile(other/'configuration-probe-results.json',self.root/'configuration-probe-results.json')
        write(self.root/'seed-provenance.json',{'source_experiment':str(other),'receipts':receipts,'source_inventory_sha256':sha(other/'reference-inputs.json'),'reason':'Reuse independently hashed generated inputs and short qualification evidence. New reference timing rows are never pooled with the seed experiment.'})

    def qualify_direct(self):
        write(self.root/'direct-bsf-amendment.json',{'reason':'Source review identifies post-admission setts BSF as a direct-TS path avoiding preliminary indexing; no ss, preceding IDR retained, shared A/V origin measured and charged','plan_sha256':sha(self.root/'plan.json'),'harness_sha256':sha(Path(__file__)),'phase':'before reference collection'})
        rows=[]
        for label,start in [('idr',Fraction(2)),('after',Fraction(2)+Fraction(1,60)),('half',Fraction(5,2)),('before',Fraction(2)+Fraction(59,60)),('subframe',Fraction(2)+Fraction(43,120))]:
            name='qual-direct-'+label;row=self.export(self.root/'qualification',start,Fraction(5),'direct-bsf',name)
            if row['status']=='committed':
                try:
                    row['validation']=self.validate(self.root/'exports'/name)
                    row['physical_without_edits']=self.validate(self.root/'exports'/name,True)
                except Exception as ex:row['validation']={'pass':False,'error':str(ex)};event(str(ex))
            rows.append(row);write(self.root/'direct-qualification-results.json',rows)

    def start_producer(self):
        name='concurrent-producer';index=1
        while (self.root/name).exists():
            index+=1;name=f'concurrent-producer-{index:02d}'
        out=self.root/name;out.mkdir(exist_ok=False);self.producer_path=out
        self.producer_lease=Lease(self.exe,PINS['ExecutableSha256']);self.producer_lease.__enter__()
        self.producer_err=open(out/'stderr.txt','wb')
        args=['-hide_banner','-loglevel','warning','-re']+self.source_args(900)+['-progress',out/'progress.txt','-stats_period','1','-f','segment','-segment_time','1','-segment_format','mpegts','-reset_timestamps','1','-segment_list',out/'segments.csv',out/'chunk-%06d.ts']
        self.producer=subprocess.Popen([str(self.exe)]+list(map(str,args)),cwd=self.root,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=self.producer_err,creationflags=0x08000000)
        write(out/'identity.json',{'pid':self.producer.pid,'args':list(map(str,args)),'sha256':PINS['ExecutableSha256'],'harness_sha256':sha(Path(__file__))})
        for _ in range(120):
            time.sleep(.25)
            if self.producer.poll() is not None:raise RuntimeError('Concurrent source failed to initialize')
            if (out/'progress.txt').exists():
                frames=re.findall(r'^frame=(\d+)',(out/'progress.txt').read_text(),re.M)
                if frames and int(frames[-1])>=300:event('Concurrent generated producer ready');return
        raise RuntimeError('Producer readiness timeout')
    def stop_producer(self):
        p=getattr(self,'producer',None)
        if p is not None:
            try:
                if p.poll() is None:p.communicate(input=b'q\n',timeout=15)
            except (subprocess.TimeoutExpired,BrokenPipeError):
                p.terminate();p.wait(timeout=10)
            self.producer_err.close();self.producer_lease.__exit__(None,None,None)
            write(self.producer_path/'stopped.json',{'pid':p.pid,'returncode':p.returncode,'confirmed_stopped':p.poll() is not None})
    def probe_finalization(self):
        amendment={'plan_sha256':sha(self.root/'plan.json'),'harness_sha256':sha(Path(__file__)),'hypothesis':'MP4 faststart extra read/write pass dominates local closed-file latency; test omission without changing source, codec or precision/quality thresholds','faststart':False,'rows':'One300s hybrid +one300s direct-BSF control at5+43/120s, then offline full validation. Only a prospectively declared fresh n20 batch may claim percentiles.','combined_evidence_budget_bytes':90_000_000_000,'minimum_free_bytes':40_000_000_000,'retention':'Keep B,C and all rejected outputs; no deletion to admit new work','gate':'Any subsequent n20 candidate must first pass full validation; no-faststart MP4 playback/compatibility remains explicit'}
        write(self.root/'finalization-amendment.json',amendment)
        write(self.root/'reference-inputs.json',[{'name':p.name,'bytes':p.stat().st_size,'sha256':sha(p)} for p in sorted((self.root/'reference').glob('*')) if p.is_file()])
        rows=[]
        try:
            self.start_producer()
            for method in ['hybrid','direct-bsf']:
                row=self.export(self.root/'reference',Fraction(5)+Fraction(43,120),Fraction(300),method,'probe-'+method)
                rows.append(row);write(self.root/'finalization-probe-results.json',rows)
        finally:self.stop_producer()
        for row in rows:
            row['validation']=self.validate(self.root/'exports'/row['label'])
            if row['method']=='direct-bsf':row['physical_without_edits']=self.validate(self.root/'exports'/row['label'],True)
            write(self.root/'finalization-probe-results.json',rows)
    def probe_configuration(self):
        write(self.root/'configuration-amendment.json',{'plan_sha256':sha(self.root/'plan.json'),'harness_sha256':sha(Path(__file__)),'hypothesis':'Repeated concat segment demux probing contributes large latency; constrain nested per-file probe work, never final output validation','probe_options':self.probe_options,'faststart':self.faststart,'rows':'one300s hybrid +one300s direct-BSF control at5+43/120s, then full offline validation','minimum_free_bytes':40_000_000_000,'combined_evidence_budget_bytes':90_000_000_000})
        write(self.root/'reference-inputs.json',[{'name':p.name,'bytes':p.stat().st_size,'sha256':sha(p)} for p in sorted((self.root/'reference').glob('*')) if p.is_file()])
        rows=[]
        try:
            self.start_producer()
            for method in ['hybrid','direct-bsf']:
                row=self.export(self.root/'reference',Fraction(5)+Fraction(43,120),Fraction(300),method,'probe-'+method)
                rows.append(row);write(self.root/'configuration-probe-results.json',rows)
        finally:self.stop_producer()
        for row in rows:
            row['validation']=self.validate(self.root/'exports'/row['label'])
            if row['method']=='direct-bsf':row['physical_without_edits']=self.validate(self.root/'exports'/row['label'],True)
            write(self.root/'configuration-probe-results.json',rows)
    def benchmark(self,only=None):
        initial=json.loads((self.root/'qualification-results.json').read_text())
        indexed=json.loads((self.root/'indexed-qualification-results.json').read_text())
        direct=json.loads((self.root/'direct-qualification-results.json').read_text())
        all_rows=initial+indexed+direct
        eligible=[m for m in ['full','hybrid','indexed-edit','direct-bsf'] if all(r.get('validation',{}).get('pass',False) for r in all_rows if r['method']==m) and any(r['method']==m for r in all_rows)]
        if only:
            if only not in eligible:raise RuntimeError('Requested candidate did not qualify')
            eligible=[only]
        if not self.faststart:
            probes=json.loads((self.root/'finalization-probe-results.json').read_text())
            if not all(any(p['method']==m and p.get('validation',{}).get('pass') for p in probes) for m in eligible):
                raise RuntimeError('No-faststart candidate must first pass five-minute probe validation')
        if self.probe_options:
            probes=json.loads((self.root/'configuration-probe-results.json').read_text())
            if not all(any(p['method']==m and p.get('probe_options')==self.probe_options and p.get('validation',{}).get('pass') for p in probes) for m in eligible):
                raise RuntimeError('Per-file probe configuration must first pass five-minute validation')
        schedule={'eligible':eligible,'source_sha256':sha(self.root/'reference'/'segments.csv'),'harness_sha256':sha(Path(__file__)),'plan_sha256':sha(self.root/'plan.json'),'trials':[]}
        write(self.root/'reference-inputs.json',[{'name':p.name,'bytes':p.stat().st_size,'sha256':sha(p)} for p in sorted((self.root/'reference').glob('*')) if p.is_file()])
        for m in ([] if only else ['current'])+eligible:
            offsets=[OFFSETS[6]] if m in ['current','full','indexed-edit'] else OFFSETS+OFFSETS
            for i,offset in enumerate(offsets):schedule['trials'].append({'method':m,'label':f'ref-{m}-{i:02d}','start':str(Fraction(5)+offset),'duration':300,'phase':'first-pass' if i<10 else 'repeat-pass'})
        schedule['sampling_note']='Current/full/indexed are n=1 controls with no percentile qualification. Hybrid and direct-BSF are the two main candidates, 20 each. Indexed is retained as a correctness/cost control because direct-BSF avoids its extra staging pass; this assignment is frozen before reference timing.'
        schedule['faststart']=self.faststart
        schedule['probe_options']=self.probe_options
        write(self.root/'frozen-reference-schedule.json',schedule)
        results=[]
        try:
            self.start_producer()
            for trial in schedule['trials']:
                row=self.export(self.root/'reference',Fraction(trial['start']),Fraction(300),trial['method'],trial['label'])
                row['phase']=trial['phase'];results.append(row);write(self.root/'reference-results.json',results)
                if self.producer.poll() is not None:raise RuntimeError('Concurrent producer stopped during collection')
                if row['status']!='committed':raise RuntimeError('Reference trial failed; retain and stop collection')
        finally:self.stop_producer()
        event('Reference timing finished; producer stopped, offline validation may begin')
    def validate_reference(self):
        rows=json.loads((self.root/'reference-results.json').read_text());results=[]
        for row in rows:
            target=self.root/'exports'/row['label']
            try:
                row['validation']=self.validate(target)
                if row['method']=='indexed-edit' and row['label'].endswith('00'):row['physical_without_edits']=self.validate(target,True)
            except Exception as ex:row['validation']={'pass':False,'error':str(ex)};event('validation failed: '+str(ex))
            results.append(row);write(self.root/'validated-reference-results.json',results)
        summary={}
        for method in sorted({r['method'] for r in results}):
            subset=[r for r in results if r['method']==method];times=sorted(r['seconds'] for r in subset)
            summary[method]={'n':len(times),'all_valid':all(r.get('validation',{}).get('pass',False) for r in subset),'min_seconds':min(times),'max_seconds':max(times),'median_seconds':statistics.median(times),'p95_nearest_rank_seconds':times[math.ceil(.95*len(times))-1], 'reference_20_sample_complete':len(times)>=20}
        write(self.root/'summary.json',summary);event(json.dumps(summary,indent=2))

def main():
    a=argparse.ArgumentParser(); a.add_argument('--root',type=Path,required=True)
    a.add_argument('--plan',action='store_true'); a.add_argument('--bootstrap',action='store_true'); a.add_argument('--prepare',action='store_true');a.add_argument('--qualify',action='store_true')
    a.add_argument('--qualify-indexed',action='store_true');a.add_argument('--benchmark',action='store_true');a.add_argument('--validate-reference',action='store_true')
    a.add_argument('--revalidate-short',action='store_true');a.add_argument('--qualify-direct',action='store_true')
    a.add_argument('--only',choices=['hybrid','direct-bsf'])
    a.add_argument('--seed-from',type=Path)
    a.add_argument('--without-faststart',action='store_true');a.add_argument('--probe-finalization',action='store_true')
    a.add_argument('--probe-configuration',action='store_true');a.add_argument('--input-probe',choices=['default','skip-duration','bounded'],default='default')
    a.add_argument('--source',default='qualification'); a.add_argument('--seconds',type=int,default=12);a.add_argument('--package',type=Path)
    args=a.parse_args(); e=Experiment(args.root)
    e.faststart=not args.without_faststart
    if args.input_probe!='default':e.probe_options=[['skip_estimate_duration_from_pts','1']]
    if args.input_probe=='bounded':e.probe_options += [['probesize','262144'],['analyzeduration','100000']]
    if args.plan: print(json.dumps(plan(),indent=2)); return
    if args.bootstrap: e.bootstrap(args.package);return
    if args.prepare: e.generate(args.source,args.seconds);return
    if args.qualify:e.qualification();return
    if args.qualify_indexed:e.qualify_indexed();return
    if args.benchmark:e.benchmark(args.only);return
    if args.validate_reference:e.validate_reference();return
    if args.revalidate_short:e.revalidate_short();return
    if args.qualify_direct:e.qualify_direct();return
    if args.seed_from:e.seed_from(args.seed_from);return
    if args.probe_finalization:e.probe_finalization();return
    if args.probe_configuration:e.probe_configuration();return
    a.error('Choose a stage')

if __name__=='__main__': main()
