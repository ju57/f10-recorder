"""One matched GOP60/GOP1 rejection experiment, not a capture qualification.

The all-IDR arm prospectively groups60 independent pictures per decoder work
unit, avoiding18,000 scheduler/reset/drain groups. Every picture is still fully
freshly decoded. No capture settings or installed binaries are modified.
"""
from __future__ import annotations
import argparse, contextlib, datetime as dt, importlib.util, json, re, sys, time
from pathlib import Path

HERE=Path(__file__).resolve();REPO=HERE.parents[1]
s=importlib.util.spec_from_file_location('mux',HERE.with_name('replay-mux-counterfactual.py'))
m=importlib.util.module_from_spec(s);s.loader.exec_module(m);r=m.r;h=m.h
sha,write=h.sha,h.write


def prepare(args):
    root=args.root.resolve()
    if not root.is_relative_to(REPO/'artifacts/issue83') or root.exists():raise ValueError('Fresh owned experiment root required')
    predecessor=REPO/'artifacts/issue83/native-api/slice-reports-01/admitted-plan.json'
    previous=json.loads(predecessor.read_text());movie=Path(previous['movie'])
    mux_plan=REPO/'artifacts/issue83/plans/mux-stage-01.json';mux=json.loads(mux_plan.read_text())
    native_root=REPO/'artifacts/issue83/native-api/intra-private-01/host';native=native_root/'NativeReplayPrototype.dll'
    lib=REPO/'artifacts/issue83/native-api/private-runtime-04/lib';dotnet=REPO/'.tools/dotnet/dotnet.exe'
    runtime=[p for p in native_root.glob('*') if p.is_file()]+list(lib.glob('*.dll'))
    bindings=[HERE,HERE.with_name('replay-mux-counterfactual.py'),HERE.with_name('replay-indexed-reference.py'),HERE.with_name('replay-hybrid-precision.py'),HERE.with_name('replay-hybrid-precision.ps1'),
        HERE.with_name('indexed-reader-prototype.py'),HERE.with_name('precision-export-prototype.py'),Path(sys.executable),dotnet,movie,predecessor,mux_plan,
        REPO/'tests/NativeReplayPrototype/Program.cs',REPO/'tests/NativeReplayPrototype/NativeReplayPrototype.csproj',REPO/'tests/NativeReplayPrototype/packages.lock.json',
        REPO/'artifacts/issue83/native-api/intra-private-staging-01.json',REPO/'artifacts/issue83/plans/retire-mux01-derivatives-01.json.receipt.json',
        Path(mux['engine']),Path(mux['probe']),*runtime]
    bindings+=list(Path(h.np.__file__).parent.rglob('*.pyd'))+list((Path(h.np.__file__).parent.parent/'numpy.libs').glob('*.dll'))
    retained=sum(h.logical_bytes(x) for x in mux['retained_roots'])
    if retained+3250000000>20000000000:raise ValueError('Whole cohort/closure and retained evidence cannot fit')
    bounds=dict(mux['bounds']);bounds.update(children=9,seconds=600,child_seconds=90,outer_seconds=620,
        output_bytes=3250000000,early_stop_bytes=2750000000,stage_stdout_bytes=2000000,
        cumulative_helpers_before=273,metadata_helpers=2,cumulative_helpers_ceiling=285)
    now=dt.datetime.now(dt.timezone.utc)
    plan=dict(kind='intra-structure-counterfactual-v1',root=str(root),runner=str(HERE),python=sys.executable,
        engine=mux['engine'],probe=mux['probe'],bounds=bounds,files={str(p.resolve()):sha(p) for p in bindings},movie=str(movie),movie_sha256=sha(movie),
        native=str(native),dotnet=str(dotnet),library_root=str(lib),runtime_files={str(p):sha(p).upper() for p in runtime},retained_roots=mux['retained_roots'],
        retained_logical_bytes_before=retained,order=['gop60-encode','gop60-full','gop1-encode','gop1-full','gop60-ssim','gop1-ssim','source-ids','gop60-ids','gop1-ids'],
        decoder_rejection_seconds=3.0,maximum_ssim_loss=.001,created_utc=now.isoformat(),latest_start_utc=(now+dt.timedelta(hours=1)).isoformat(),
        effects='Offline2serial NVENC encodes,2fresh complete software H264/AAC decoders; only if decoder necessary bound passes,2fullpairedSSIM and3fullIDdecodes. No capture/playback/worker/UI/settings/NVIDIA/startup/catalog/install/publication.',
        decision='GOP1 must contain18k actualIDRs/one VCL per AU, coalesced prospectively into300x60 decoder work units. Reject this mechanism if complete native PROCESS>=3seconds. If faster, pairedSSIM must finish18kframes/progress=end without repeating exhausted input, be within.001 of freshGOP60 control and every original physicalID must match. Stop first failure or rejection; no alternate grouping/preset/GOP/worker variants.',
        claim='Falsify one structural decoder/cut-encoding hypothesis before a product port. Samep3/ll/CBR20Mbps/BF0/CABAC/slice1/1080p60/closed300s source/audioCOPY; only GOP changes. Full fresh decoding and configuration/clock/frame/audio/error/drain checks remain. Report actualIDRs separately from decoderGroups and flush/drain/queue costs.',
        exclusions='Not an exact-cut export: source is the retained aligned300s diagnostic and audio is COPIED. No audio-time saving is transferable to an arbitrary save. SSIM is exploratory, not a production quality standard; no real-game/capture GPU/quality/storage/latency qualification. Even<3decoder needs complete remaining-stage budget; current measured export alone exceeds5seconds. No claim of a universal decoder floor.')
    root.mkdir(parents=True);write(args.plan_file,plan);print(sha(args.plan_file))


def main():
    p=argparse.ArgumentParser();mode=p.add_mutually_exclusive_group(required=True);mode.add_argument('--prepare',action='store_true');mode.add_argument('--run',action='store_true')
    p.add_argument('--root',type=Path);p.add_argument('--plan-file',type=Path,required=True);p.add_argument('--expected-sha256');args=p.parse_args()
    if args.prepare:prepare(args);return
    if sha(args.plan_file).upper()!=args.expected_sha256.upper():raise ValueError('Reviewed plan changed')
    plan=json.loads(args.plan_file.read_text());root=Path(plan['root'])
    if plan['kind']!='intra-structure-counterfactual-v1' or any(root.iterdir()) or dt.datetime.now(dt.timezone.utc)>dt.datetime.fromisoformat(plan['latest_start_utc']):raise ValueError('Fresh current reviewed admission required')
    pilot=r.Reference(plan);outcome=dict(status='failed',results=[],quality={});outputs={}
    with contextlib.ExitStack() as locks:
        for path,digest in plan['files'].items():locks.enter_context(h.ts.fixture.Lease(Path(path),digest))
        write(root/'admitted-plan.json',plan)
        try:
            for gop in [60,1]:
                label=f'gop{gop}';output=root/(label+'.mp4');outputs[label]=output
                pilot.run(['-hide_banner','-v','warning','-xerror','-nostdin','-copyts','-i',plan['movie'],
                    '-map','0:v:0','-map','0:a:0','-fps_mode','passthrough','-enc_time_base:v','1:48000','-video_track_timescale','48000',
                    '-c:v','h264_nvenc','-preset','p3','-tune','ll','-rc','cbr','-b:v','20M','-maxrate','20M','-bufsize','40M',
                    '-g',str(gop),'-bf','0','-forced-idr','1','-force_key_frames',f'expr:gte(n,n_forced*{gop})','-flags','+cgop',
                    '-coder','cabac','-slices','1','-pix_fmt','yuv420p','-c:a','copy','-avoid_negative_ts','disabled','-movflags','+faststart',output],label+'-encode')
                encode=pilot.rows[-1];digest=sha(output);locks.enter_context(h.ts.fixture.Lease(output,digest))
                decode_root=root/(label+'-decode');decode_root.mkdir();native_plan=decode_root/'plan.json';report=decode_root/'native-32.json'
                write(native_plan,dict(kind='native-full-decode-pilot-v1',root=str(decode_root),movie=str(output),runtimeFiles=plan['runtime_files'],libraryRoot=plan['library_root'],decoderThreads=1,coalesceIndependentPictures=gop==1))
                pilot.run([plan['native'],'--decode',native_plan,sha(native_plan).upper(),'32',report],label+'-full',tool=plan['dotnet'])
                process=pilot.rows[-1];native=json.loads(report.read_text());details=native['details']
                if native['result']!='native-full-decode-complete' or native['errors'] or details['decodedFrames']!=18000 or details['videoPackets']!=18000 or details['decoderGroups']!=300:
                    raise ValueError('Complete fresh decode/group count failed')
                if details['actualIdrs']!=(18000 if gop==1 else 300) or details['minimumVclNals']!=1 or details['maximumVclNals']!=1:raise ValueError('Actual coded structure differs from frozen comparison')
                outcome['results'].append(dict(label=label,sha256=digest,bytes=output.stat().st_size,encode_seconds=encode['seconds'],native_complete_process_seconds=process['seconds'],details=details))
                write(root/'outcome.json',outcome)
                if gop==1 and process['seconds']>=plan['decoder_rejection_seconds']:
                    outcome['status']='rejected-complete-decoder-cost';return
            for label,output in outputs.items():
                progress=pilot.run(['-hide_banner','-v','info','-xerror','-err_detect','explode','-i',plan['movie'],'-err_detect','explode','-i',output,
                    '-filter_complex','[0:v:0][1:v:0]ssim=shortest=1:eof_action=endall:repeatlast=0[paired]',
                    '-map','[paired]','-an','-fps_mode','passthrough','-progress','pipe:1','-nostats','-f','null','-'],label+'-ssim')
                log=Path(pilot.rows[-1]['stderr']).read_text(errors='replace');values=re.findall(r'All:([0-9.]+)',log)
                counts=re.findall(rb'(?m)^frame=(\d+)\s*$',progress)
                if len(values)!=1 or not counts or int(counts[-1])!=18000 or b'progress=end' not in progress:
                    raise ValueError('Full18k-frame terminal paired SSIM missing/ambiguous')
                outcome['quality'][label]=float(values[0]);write(root/'outcome.json',outcome)
            if outcome['quality']['gop1']<outcome['quality']['gop60']-plan['maximum_ssim_loss']:
                outcome['status']='rejected-exploratory-quality';return
            reference=pilot.ids(pilot.run(['-v','error','-xerror','-err_detect','explode','-ignore_editlist','1','-i',plan['movie'],'-an','-vf',r.CROP,'-fps_mode','passthrough','-f','rawvideo','-'],'source-ids'))
            for label,output in outputs.items():
                ids=pilot.ids(pilot.run(['-v','error','-xerror','-err_detect','explode','-ignore_editlist','1','-i',output,'-an','-vf',r.CROP,'-fps_mode','passthrough','-f','rawvideo','-'],label+'-ids'))
                if len(ids)!=18000 or ids!=reference:raise ValueError('Physical frame source identity differs')
            if sha(Path(plan['movie']))!=plan['movie_sha256']:raise ValueError('Protected source changed')
            outcome['status']='lead-only-not-qualified-recipe'
        except Exception as ex:outcome['error']=str(ex);raise
        finally:
            outcome.update(seconds=time.perf_counter()-pilot.started,children=len(pilot.rows),all_children_stopped=all(x['stopped'] for x in pilot.rows),observer=pilot.observer,
                parent_resources=r.parent_resources(),publication=False,capture=False,source_sha256_after=sha(Path(plan['movie'])))
            write(root/'outcome.json',outcome)


if __name__=='__main__':main()
