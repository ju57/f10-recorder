"""Tiny bounded live-file guard control; no media, GPU, capture or recorder."""
from __future__ import annotations
import sys

# Child is deliberately stdlib-only, finite even if the parent guard fails.
if len(sys.argv)==3 and sys.argv[1]=='--produce':
    import time
    from pathlib import Path
    destination=Path(sys.argv[2])
    with open(destination,'xb',buffering=0) as stream:
        print('writer-open',flush=True)
        for _ in range(32):
            stream.write(bytes(1048576));time.sleep(.05)
        time.sleep(2)
    print('writer-closed',flush=True)
    raise SystemExit(0)

import argparse,contextlib,datetime as dt,importlib.util,json,os,time
from pathlib import Path
HERE=Path(__file__).resolve();REPO=HERE.parents[1]
s=importlib.util.spec_from_file_location('reference',HERE.with_name('replay-indexed-reference.py'))
r=importlib.util.module_from_spec(s);s.loader.exec_module(r);h=r.h
sha,write=h.sha,h.write


def boxes(file,start,end):
    result=[]
    with open(file,'rb') as stream:
        pos=start
        while pos<end:
            stream.seek(pos);head=stream.read(16)
            if len(head)<8:raise ValueError('Incomplete box header')
            size=int.from_bytes(head[:4],'big');kind=head[4:8].decode('ascii');header=8
            if size==1:size=int.from_bytes(head[8:16],'big');header=16
            elif size==0:size=end-pos
            if size<header or pos+size>end:raise ValueError('Incomplete box extent')
            result.append(dict(kind=kind,start=pos,header=header,bytes=size));pos+=size
    return result


def mp4_metadata(file):
    """Read only bounded MP4 metadata, never decode or qualify this failed output."""
    top=boxes(file,0,file.stat().st_size);result=dict(bytes=file.stat().st_size,boxes=top,tracks=[])
    movie=next((x for x in top if x['kind']=='moov'),None)
    if movie is None:result['complete_moov']=False;return result
    if movie['bytes']>2000000:raise ValueError('Metadata inspection cap')
    result['complete_moov']=True
    def children(box):return boxes(file,box['start']+box['header'],box['start']+box['bytes'])
    with open(file,'rb') as stream:
        for track in (x for x in children(movie) if x['kind']=='trak'):
            mdia=next(x for x in children(track) if x['kind']=='mdia');info=children(mdia)
            handler=next(x for x in info if x['kind']=='hdlr');stream.seek(handler['start']+handler['header']+8);kind=stream.read(4).decode('ascii')
            header=next(x for x in info if x['kind']=='mdhd');stream.seek(header['start']+header['header']);raw=stream.read(32)
            if raw[0]!=0:raise ValueError('Unqualified version1 mdhd in this metadata control')
            timescale=int.from_bytes(raw[12:16],'big');duration=int.from_bytes(raw[16:20],'big')/timescale
            minf=next(x for x in info if x['kind']=='minf');stbl=next(x for x in children(minf) if x['kind']=='stbl')
            size_box=next(x for x in children(stbl) if x['kind']=='stsz');stream.seek(size_box['start']+size_box['header']);raw=stream.read(12)
            fixed=int.from_bytes(raw[4:8],'big');count=int.from_bytes(raw[8:12],'big')
            if count>20000:raise ValueError('Metadata sample cap')
            if fixed:payload=fixed*count
            else:
                raw=stream.read(count*4)
                if len(raw)!=count*4:raise ValueError('Incomplete sample size table')
                payload=sum(int.from_bytes(raw[n:n+4],'big') for n in range(0,len(raw),4))
            result['tracks'].append(dict(kind=kind,samples=count,payload_bytes=payload,duration_seconds=duration,average_payload_mbps=payload*8/duration/1000000))
    return result


def main():
    parser=argparse.ArgumentParser();mode=parser.add_mutually_exclusive_group(required=True);mode.add_argument('--prepare',action='store_true');mode.add_argument('--run',action='store_true')
    parser.add_argument('--root',type=Path);parser.add_argument('--plan-file',type=Path,required=True);parser.add_argument('--expected-sha256');args=parser.parse_args()
    if args.prepare:
        root=args.root.resolve()
        if not root.is_relative_to(REPO/'artifacts/issue83') or root.exists():raise ValueError('Fresh owned control root required')
        previous=REPO/'artifacts/issue83/plans/intra-structure-02.json';old=json.loads(previous.read_text());failed=Path(old['root'])/'gop1.mp4'
        try:metadata=mp4_metadata(failed)
        except (ValueError,UnicodeError,StopIteration) as error:
            with open(failed,'rb') as source:prefix=source.read(64).hex()
            metadata=dict(bytes=failed.stat().st_size,complete_moov=False,inspection_error=str(error),prefix_hex=prefix,
                limitation='Interrupted output is not a complete admitted MP4; no frame count, bitrate or decoding/quality result can be inferred.')
        metadata_file=REPO/'artifacts/issue83/hybrid/intra-reports-02/failed-gop1-metadata.json';write(metadata_file,metadata)
        bindings=[HERE,HERE.with_name('replay-indexed-reference.py'),HERE.with_name('replay-hybrid-precision.py'),HERE.with_name('replay-hybrid-precision.ps1'),
            HERE.with_name('indexed-reader-prototype.py'),HERE.with_name('precision-export-prototype.py'),Path(sys.executable),previous,metadata_file]
        bindings+=list(Path(h.np.__file__).parent.rglob('*.pyd'))+list((Path(h.np.__file__).parent.parent/'numpy.libs').glob('*.dll'))
        bounds=dict(old['bounds']);bounds.update(children=1,seconds=20,child_seconds=10,child_memory_bytes=134217728,outer_seconds=30,
            outer_memory_bytes=536870912,output_bytes=67108864,early_stop_bytes=50331648,stage_stdout_bytes=65536,
            child_log_bytes=65536,stage_derivative_bytes=8388608,closure_reserve_bytes=16777216,
            cumulative_helpers_before=279,metadata_helpers=5,cumulative_helpers_ceiling=286)
        now=dt.datetime.now(dt.timezone.utc)
        plan=dict(kind='live-file-guard-control-v1',root=str(root),runner=str(HERE),python=sys.executable,engine=sys.executable,probe=sys.executable,
            bounds=bounds,files={str(p.resolve()):sha(p) for p in bindings},retained_roots=old['retained_roots'],
            created_utc=now.isoformat(),latest_start_utc=(now+dt.timedelta(hours=1)).isoformat(),
            effects='One stdlib Python writer; no media/encoder/decoder/GPU/capture/playback/UI/worker/profile/startup/NVIDIA/catalog/install. Worst producer32MiB plus logs/16MiB closure independently fits64MiBcap.',
            producer_worst_bytes=33554432,expected_stop_maximum_bytes=16777216,
            decision='Missing-file native query must reject without fallback; unexpected segment name must reject; WRITER native maxEOF/allocation must exceed8MiB before guard kills its open producer; final writer EOF/allocation must stay<=16MiB. Directory metadata alone is not authoritative for active writers. A failed control prevents any further media run.',
            claim='Qualify experimental live-file observation only; no production storage-model transfer, no media performance result. Failed GOP1 MP4 is inspected as metadata only, never decoded/published.')
        root.mkdir(parents=True);write(args.plan_file,plan);print(sha(args.plan_file));print(json.dumps(metadata));return
    if sha(args.plan_file).upper()!=args.expected_sha256.upper():raise ValueError('Reviewed plan changed')
    plan=json.loads(args.plan_file.read_text());root=Path(plan['root'])
    if plan['kind']!='live-file-guard-control-v1' or any(root.iterdir()) or dt.datetime.now(dt.timezone.utc)>dt.datetime.fromisoformat(plan['latest_start_utc']):raise ValueError('Fresh current control required')
    pilot=r.Reference(plan);result=dict(status='failed');target=root/'writer.bin'
    with contextlib.ExitStack() as leases:
        for path,digest in plan['files'].items():leases.enter_context(h.ts.fixture.Lease(Path(path),digest))
        write(root/'admitted-plan.json',plan)
        try:
            with pilot.track_live([root/'missing.bin']):
                try:pilot.live_length(root/'missing.bin',0)
                except OSError:result['query_failure_rejected']=True
                else:raise ValueError('Native query failure silently fell back')
            scope=root/'segment-scope';scope.mkdir();(scope/'unexpected.bin').write_bytes(b'x')
            with pilot.track_live(scopes=[(scope,'chunk-%06d.ts')]):
                try:pilot.used_bytes(scope)
                except ValueError as error:
                    if str(error)!='Unexpected producer output in declared segment scope':raise
                    result['unexpected_output_rejected']=True
                else:raise ValueError('Unexpected producer namespace accepted')
            pilot.reserve(plan['producer_worst_bytes'])
            try:pilot.run([HERE,'--produce',target],'writer',tool=plan['python'])
            except RuntimeError as error:result['guard_rejection']=str(error)
            payload=target.stat().st_size if target.exists() else 0
            output=(root/'writer.stdout.bin').read_bytes()
            if result.get('guard_rejection')!='Child derivative bound' or b'writer-open' not in output or b'writer-closed' in output:
                raise ValueError('Did not reject live open-file growth as expected')
            key=os.path.normcase(os.path.abspath(target))
            writer=pilot.live_observations['files'].get(key)
            before_kill=pilot.rows[-1].get('guard_stop',{}).get('live',{}).get('files',{}).get(key)
            if writer is None or before_kill is None:raise ValueError('Explicit native writer-size observation missing')
            charged_before=max(before_kill['last_eof_bytes'],before_kill['last_allocation_bytes'])
            charged_final=max(writer['last_eof_bytes'],writer['last_allocation_bytes'])
            if not plan['bounds']['stage_derivative_bytes']<charged_before<=plan['expected_stop_maximum_bytes']:raise ValueError('Unexpected native producer stop allocation')
            if payload>plan['expected_stop_maximum_bytes'] or charged_final>plan['expected_stop_maximum_bytes']:raise ValueError('Final writer extent exceeds admitted control limit')
            if not all(x['stopped'] for x in pilot.rows):raise ValueError('Owned producer absence proof incomplete')
            result.update(status='passed-live-file-growth-control',payload_bytes=payload,charged_before_kill=charged_before,charged_final=charged_final,
                fresh_local_bytes_after_stop=pilot.rows[-1]['fresh_local_bytes_after_stop'],payload_sha256=sha(target))
        except Exception as error:result['error']=str(error);raise
        finally:
            result.update(seconds=time.perf_counter()-pilot.started,children=len(pilot.rows),all_children_stopped=all(x['stopped'] for x in pilot.rows),live=pilot.live_observations,observer=pilot.observer)
            write(root/'result.json',result)


if __name__=='__main__':main()
