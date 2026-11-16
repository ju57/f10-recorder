"""Finite closed-input mux/audio comparison. No capture or product adoption.

All outputs remain unpublished derivatives. Every output is freshly fully
decoded; compressed payload/clock checks isolate the header-placement change.
"""
from __future__ import annotations
import argparse, contextlib, datetime as dt, importlib.util, json, math, os, sys, time
from pathlib import Path
import numpy as np

HERE=Path(__file__).resolve();REPO=HERE.parents[1]
s=importlib.util.spec_from_file_location('reference',HERE.with_name('replay-indexed-reference.py'))
r=importlib.util.module_from_spec(s);s.loader.exec_module(r);h=r.h
sha,write=h.sha,h.write


def atom_order(path):
    size=path.stat().st_size;boxes=[]
    with open(path,'rb') as f:
        offset=0
        while offset<size:
            f.seek(offset);header=f.read(16)
            if len(header)<8:raise ValueError('Truncated MP4 box')
            length=int.from_bytes(header[:4],'big');kind=header[4:8].decode('ascii')
            if length==1:length=int.from_bytes(header[8:16],'big')
            elif length==0:length=size-offset
            if length<8 or offset+length>size:raise ValueError('Invalid MP4 box size')
            boxes.append(dict(kind=kind,offset=offset,bytes=length));offset+=length
    kinds=[x['kind'] for x in boxes]
    if kinds.count('moov')!=1 or kinds.count('mdat')!=1 or kinds.index('moov')>kinds.index('mdat') or 'moof' in kinds:
        raise ValueError('Expected ordinary nonfragmented MP4 with moov before mdat')
    return boxes


def records(probe,stream=None):
    return [tuple(p.get(k) for k in ['stream_index','pts','dts','duration','data_hash']) for p in probe['packets'] if stream is None or p['stream_index']==stream]


def snr(source_path,candidate_path):
    a=np.memmap(source_path,np.float32,'r').reshape(-1,2);b=np.memmap(candidate_path,np.float32,'r').reshape(-1,2)
    if abs(len(a)-len(b))>800:raise ValueError('Audio sample endpoint discrepancy')
    samples=min(len(a),len(b));signal=0.;error=0.
    for n in range(0,samples,8192):
        x=np.asarray(a[n:min(n+8192,samples)],np.float64);y=np.asarray(b[n:min(n+8192,samples)],np.float64)
        signal+=float(np.sum(x*x));error+=float(np.sum((x-y)**2))
    if signal<=0 or error<=0:raise ValueError('No nonzero audio quality evidence')
    checks=[]
    for position in [0,48000,samples//2-2048,samples-8192]:
        reference=np.asarray(a[position:position+4096],np.float64);matches=[]
        for lag in range(-16,17):
            if position+lag<0:continue
            actual=np.asarray(b[position+lag:position+lag+4096],np.float64)
            if actual.shape!=reference.shape:continue
            denominator=np.sqrt(np.sum(reference*reference)*np.sum(actual*actual))
            if denominator>0:matches.append((float(np.sum(reference*actual)/denominator),lag))
        if not matches:raise ValueError('Missing audio phase evidence')
        correlation,lag=max(matches);checks.append(dict(position=position,correlation=correlation,lag_samples=lag))
        if correlation<.97 or abs(lag)>4:raise ValueError('Audio phase rejected: '+str(checks[-1]))
    return dict(source_samples=len(a),output_samples=len(b),snr_db=10*math.log10(signal/error),phase=checks,
        limitation='Repeating baseline chirp supplies phase and source-specific quality only; no arbitrary-content/offset/channel-sensitivity qualification.')


def prepare(args):
    root=args.root.resolve()
    if not root.is_relative_to(REPO/'artifacts/issue83') or root.exists():raise ValueError('Fresh owned output root required')
    prior=REPO/'artifacts/issue83/hybrid/reference-03';receipt=prior/'original-request/receipt.json'
    completed=json.loads((prior/'outcome.json').read_text())
    if completed['status']!='feasibility-stopped-after-valid-case' or len(completed['results'])!=1:raise ValueError('Valid retained indexed construction required')
    original=prior/'original-request/input.ts';known=prior/'original-request/result.mp4';a=json.loads(receipt.read_text())
    if sha(known)!=a['sha256']:raise ValueError('Qualified diagnostic exemplar changed')
    previous=json.loads((prior/'admitted-plan.json').read_text());bounds=dict(previous['bounds'])
    bounds.update(children=16,seconds=300,child_seconds=40,outer_seconds=320,output_bytes=4500000000,
        early_stop_bytes=4000000000,stage_stdout_bytes=150000000,stage_derivative_bytes=1000000000,
        cumulative_helpers_before=258,metadata_helpers=1,cumulative_helpers_ceiling=276)
    runtime=REPO/'artifacts/issue83/native-api/private-runtime-04';native=runtime/'host/NativeReplayPrototype.dll';dotnet=REPO/'.tools/dotnet/dotnet.exe'
    runtime_files=list((runtime/'host').glob('*'))+list((runtime/'lib').glob('*.dll'))
    runtime_files=[p for p in runtime_files if p.is_file()]
    bindings=[HERE,HERE.with_name('replay-indexed-reference.py'),HERE.with_name('replay-hybrid-precision.py'),HERE.with_name('replay-hybrid-precision.ps1'),
        HERE.with_name('indexed-reader-prototype.py'),HERE.with_name('precision-export-prototype.py'),Path(sys.executable),dotnet,original,known,receipt,prior/'outcome.json',prior/'admitted-plan.json',
        REPO/'artifacts/issue83/native-api/private-staging-04.json',REPO/'artifacts/issue83/native-api/slice-reports-01/NativeProgram.cs',Path(previous['engine']),Path(previous['probe']),*runtime_files]
    bindings+=list(Path(np.__file__).parent.rglob('*.pyd'))+list((Path(np.__file__).parent.parent/'numpy.libs').glob('*.dll'))
    retained=sum(h.logical_bytes(x) for x in previous['retained_roots'])
    if retained+4500000000>20000000000:raise ValueError('Whole forward budget plus retained evidence cannot fit')
    now=dt.datetime.now(dt.timezone.utc)
    plan=dict(kind='mux-stage-counterfactual-v1',root=str(root),runner=str(HERE),python=sys.executable,
        engine=previous['engine'],probe=previous['probe'],bounds=bounds,files={str(x.resolve()):sha(x) for x in bindings},
        source=str(original),source_sha256=sha(original),control_sha256=a['sha256'],dotnet=str(dotnet),native=str(native),library_root=str(runtime/'lib'),
        runtime_files={str(p):sha(p).upper() for p in runtime_files},retained_roots=previous['retained_roots'],retained_logical_bytes_before=retained,
        order=['A-faststart-twoloop','B-front-reserve-twoloop','C-front-reserve-fast'],moov_reservation_bytes=1048576,
        created_utc=now.isoformat(),latest_start_utc=(now+dt.timedelta(hours=1)).isoformat(),
        effects='Offline only, no NVENC/capture/playback/worker/UI/preferences/startup/NVIDIA/catalog/install.3serial video-COPY/audio-encode muxes;3fresh full software H264/AAC decoders;3packet probes;4audio decodes. All outputs unpublished.',
        decision='A must reproduce valid reference03 exactly. B must preserve every coded packet/configuration/clock and PCM sample, moov precedes mdat and full decode succeeds. C video/configuration/clocks unchanged; audio phase>.97/lag<=4samples and whole-sample SNR no worse than A by0.5dB. Quality threshold is exploratory, not product adoption. Stop first failure; no larger reservation/coder variants.',
        claim='Isolate matched mux/faststart/AAC stage costs, including output placement and complete fresh decoder process costs. No overall Saved/median/p95/native-validator-equivalence claim; no storage model transfer.',
        exclusions='Preassembled TS preparation is outside these isolated stage timers and is separately measured in reference03. Native exploratory decoder remains unqualified for product corrupt/drift equivalence. Baseline audio is periodic; broader held-out/audio negatives remain required.')
    root.mkdir(parents=True);write(args.plan_file,plan);print(sha(args.plan_file))


def main():
    p=argparse.ArgumentParser();m=p.add_mutually_exclusive_group(required=True);m.add_argument('--prepare',action='store_true');m.add_argument('--run',action='store_true')
    p.add_argument('--root',type=Path);p.add_argument('--plan-file',type=Path,required=True);p.add_argument('--expected-sha256');args=p.parse_args()
    if args.prepare:prepare(args);return
    if sha(args.plan_file).upper()!=args.expected_sha256.upper():raise ValueError('Reviewed plan changed')
    plan=json.loads(args.plan_file.read_text());root=Path(plan['root'])
    if plan['kind']!='mux-stage-counterfactual-v1' or any(root.iterdir()) or dt.datetime.now(dt.timezone.utc)>dt.datetime.fromisoformat(plan['latest_start_utc']):raise ValueError('Fresh current admission required')
    pilot=r.Reference(plan);outcome=dict(status='failed',results=[]);original_packets=None;original_pcm=None
    with contextlib.ExitStack() as locks:
        for path,digest in plan['files'].items():locks.enter_context(h.ts.fixture.Lease(Path(path),digest))
        write(root/'admitted-plan.json',plan)
        try:
            pilot.run(['-v','error','-xerror','-err_detect','explode','-copyts','-i',plan['source'],'-map','0:a:0','-vn',
                '-af','atrim=start=10:end=310,asetpts=PTS-10/TB','-ar','48000','-ac','2','-f','f32le','-'],'source-audio')
            for index,label in enumerate(plan['order']):
                output=root/(label+'.mp4');clock=time.perf_counter()
                prefix=['-hide_banner','-v','warning','-nostdin','-xerror','-copyts','-i',plan['source'],'-map','0:v:0','-map','0:a:0',
                    '-c:v','copy','-bsf:v','setts=pts=PTS-10.0/TB:dts=DTS-10.0/TB','-af','atrim=start=10.0:end=310.000000000,asetpts=PTS-10.0/TB',
                    '-c:a','aac','-b:a','192k','-ar','48000','-ac','2']
                if index==2:prefix+=['-aac_coder','fast']
                placement=['-movflags','+faststart'] if index==0 else ['-moov_size',str(plan['moov_reservation_bytes'])]
                pilot.run(prefix+['-avoid_negative_ts','disabled',*placement,'-video_track_timescale','48000',output],label+'-mux')
                mux_seconds=pilot.rows[-1]['seconds'];digest=sha(output)
                if index==0 and digest!=plan['control_sha256']:raise ValueError('Matched control differs from exact valid reference03')
                locks.enter_context(h.ts.fixture.Lease(output,digest))
                boxes=atom_order(output);write(root/(label+'-atoms.json'),boxes)
                decode_root=root/(label+'-decode');decode_root.mkdir();report=decode_root/'native-32.json';native_plan=decode_root/'plan.json'
                write(native_plan,dict(kind='native-full-decode-pilot-v1',root=str(decode_root),movie=str(output),runtimeFiles=plan['runtime_files'],libraryRoot=plan['library_root'],decoderThreads=1))
                pilot.run([plan['native'],'--decode',native_plan,sha(native_plan).upper(),'32',report],label+'-full',tool=plan['dotnet'])
                full_seconds=pilot.rows[-1]['seconds'];decoded=json.loads(report.read_text())
                if decoded['result']!='native-full-decode-complete' or decoded['details']['decodedFrames']!=18000 or decoded['errors']:raise ValueError('Fresh full output decoder incomplete')
                packet=json.loads(pilot.run(['-v','error','-show_streams','-show_packets','-show_data_hash','sha256','-of','json',output],label+'-packets',probe=True))
                streams=packet['streams']
                if len(streams)!=2 or [(x['codec_type'],x['codec_name']) for x in streams]!=[('video','h264'),('audio','aac')]:raise ValueError('Unexpected tracks')
                if (streams[0]['width'],streams[0]['height'],streams[0]['time_base'],streams[1]['sample_rate'],streams[1]['channels'])!=(1920,1080,'1/48000','48000',2):raise ValueError('Unexpected output schema')
                if index==0:original_packets=packet
                elif index==1 and records(packet)!=records(original_packets):raise ValueError('Header-only change altered coded samples or clocks')
                elif index==2:
                    if records(packet,0)!=records(original_packets,0):raise ValueError('AAC option changed video')
                    if [x[:-1] for x in records(packet,1)]!=[x[:-1] for x in records(original_packets,1)]:raise ValueError('AAC option changed audio packet clock')
                if [x.get('extradata_hash') for x in streams]!=[x.get('extradata_hash') for x in original_packets['streams']]:raise ValueError('Codec configuration differs')
                pilot.run(['-v','error','-xerror','-err_detect','explode','-i',output,'-vn','-map','0:a:0','-ar','48000','-ac','2','-f','f32le','-'],label+'-audio')
                pcm=Path(pilot.rows[-1]['stdout']);quality=snr(root/'source-audio.stdout.bin',pcm)
                write(root/(label+'-quality.json'),quality)
                if index==0:original_pcm=pcm;control_quality=quality
                if index==1 and sha(pcm)!=sha(original_pcm):raise ValueError('Header-only change altered full decoded PCM')
                if index==2 and quality['snr_db']<control_quality['snr_db']-.5:raise ValueError('Fast AAC failed conservative exploratory SNR gate')
                result=dict(label=label,bytes=output.stat().st_size,sha256=digest,mux_seconds=mux_seconds,fresh_full_decoder_process_seconds=full_seconds,
                    audio_quality=quality,decoded_frames=decoded['details']['decodedFrames'],audio_samples=decoded['details']['audioSamples'],gops=decoded['details']['gops'],
                    seconds_including_all_stage_oracles=time.perf_counter()-clock)
                outcome['results'].append(result);write(root/'outcome.json',outcome)
            if sha(Path(plan['source']))!=plan['source_sha256']:raise ValueError('Protected transport input changed')
            outcome['status']='passed-offline-mux-comparison'
        except Exception as ex:outcome['error']=str(ex);raise
        finally:
            outcome.update(seconds=time.perf_counter()-pilot.started,children=len(pilot.rows),all_children_stopped=all(x['stopped'] for x in pilot.rows),observer=pilot.observer,
                parent_resources=r.parent_resources(),publication=False,capture=False)
            write(root/'outcome.json',outcome)


if __name__=='__main__':main()
