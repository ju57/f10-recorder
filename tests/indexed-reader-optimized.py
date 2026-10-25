"""THROWAWAY follow-up: persistent managed reader and combined boundary process."""
import argparse, ctypes, importlib.util, json, math, msvcrt, os, shutil, subprocess, threading, time
from pathlib import Path
from fractions import Fraction

HERE=Path(__file__).resolve()
spec=importlib.util.spec_from_file_location('indexed_base',HERE.with_name('indexed-reader-prototype.py'))
base=importlib.util.module_from_spec(spec);spec.loader.exec_module(base)
fixture=base.fixture;REPO=base.REPO

def plan():
    return {**base.plan(),'kind':'THROWAWAY persistent managed reader follow-up',
        'methods':['original Python reader/sequential boundaries','managed reader/sequential boundaries','managed reader/combined boundary process'],
        'preparation':'Reuse verified immutable source/index; record helper build and startup outside per-save timer. No warm encoded payloads or prebuilt MP4.',
        'reader':'Owned .NET10 helper; metadata loaded once, input bytes read within save. Duplicate only the owned mux stdin handle into owned helper; no public listener.',
        'boundary':'Combined mode creates two NVENC encoders within one FFmpeg process per save; not persistent NVENC contexts across saves.',
        'pilot':'Five-minute original/managed/combined controls in fixed order; offline full validation, chosen candidate then fresh20 rows. No pooling.',
        'progress_gate':'If best qualified pilot improves <0.20s versus same-run Python control, notify user before further optimization/expanded tracing.',
        'targets':base.plan()['targets'],'storage':base.plan()['storage']}

class Worker:
    def __init__(self,root):
        self.root=root;self.leases=[]
        num=1
        while (root/f'managed-reader-{num:02d}.json').exists():num+=1
        self.record=root/f'managed-reader-{num:02d}.json'
        self.log=open(root/f'managed-reader-{num:02d}.stderr.txt','wb')
        self.host=REPO/'.tools/dotnet/dotnet.exe';self.dll=root/'reader-build/IndexedReaderPrototype.dll'
        self.hashes={str(p):fixture.sha(p) for p in [self.host,*sorted((root/'reader-build').glob('*.dll')),
            *sorted((root/'reader-build').glob('*.json'))]}
        for path,digest in self.hashes.items():
            lease=fixture.Lease(Path(path),digest);lease.__enter__();self.leases.append(lease)
        tick=time.perf_counter()
        env=os.environ.copy();env['DOTNET_TieredCompilation']='0'
        args=[str(self.host),'exec',str(self.dll),str(root/'index.json'),str(root)]
        self.process=subprocess.Popen(args,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.log,
            creationflags=0x08000000,env=env)
        timer=threading.Timer(30,lambda:self.process.kill() if self.process.poll() is None else None);timer.start()
        try:self.ready=json.loads(self.process.stdout.readline())
        finally:timer.cancel()
        if self.ready['status']!='ready':raise RuntimeError('Reader helper did not initialize')
        base.write(self.record,{'pid':self.process.pid,'args':args,'hashes':self.hashes,
            'ready':self.ready,'startup_wall_seconds':time.perf_counter()-tick,'tiered_compilation':False})

    def feed(self,target,kind,index,first,last,output):
        tick=time.perf_counter();cpu=time.thread_time();boundaries={}
        for path in target.glob('boundary-*.ts'):
            boundaries[path.name]=base.index_chunk(path,video_only=True)
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.GetCurrentProcess.restype=ctypes.c_void_p
        kernel.DuplicateHandle.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_void_p),ctypes.c_uint32,ctypes.c_int,ctypes.c_uint32]
        kernel.DuplicateHandle.restype=ctypes.c_int
        duplicate=ctypes.c_void_p()
        if not kernel.DuplicateHandle(kernel.GetCurrentProcess(),msvcrt.get_osfhandle(output.fileno()),
            int(self.process._handle),ctypes.byref(duplicate),0,False,2):
            raise ctypes.WinError(ctypes.get_last_error())
        request={'target':str(target),'kind':kind,'first':first,'last':last,
            'handle':duplicate.value,'boundaries':boundaries}
        self.process.stdin.write((json.dumps(request)+'\n').encode());self.process.stdin.flush()
        timer=threading.Timer(120,lambda:self.process.kill() if self.process.poll() is None else None);timer.start()
        try:result=json.loads(self.process.stdout.readline())
        finally:timer.cancel()
        if result['status']!='done':raise RuntimeError('Reader job failed')
        result.update(reader_thread_cpu_seconds=time.thread_time()-cpu,
            parent_wall_seconds=time.perf_counter()-tick,reader_mode='persistent-managed')
        return result

    def close(self):
        try:
            if self.process.poll() is None:
                self.process.stdin.write(b'quit\n');self.process.stdin.flush();self.process.wait(timeout=15)
        finally:
            if self.process.poll() is None:self.process.kill();self.process.wait(timeout=15)
            self.log.close()
            record=json.loads(self.record.read_text());record.update(returncode=self.process.returncode,confirmed_stopped=self.process.poll() is not None)
            base.write(self.record,record)
            for lease in reversed(self.leases):lease.__exit__(None,None,None)

class Optimized(base.ReaderExperiment):
    def __init__(self,root):
        super().__init__(root);self.worker=None;self.use_managed=True;self.combine=True;self.pending=None

    def prepare_from(self,seed):
        self.root.mkdir(parents=True,exist_ok=False);base.write(self.root/'plan.json',plan())
        (self.root/'logs').mkdir();(self.root/'vendor').mkdir();(self.root/'harness-history').mkdir()
        index=json.loads((seed/'index.json').read_text());tick=time.perf_counter()
        for kind,entries in index.items():
            (self.root/kind).mkdir()
            for name,entry in entries.items():
                src=seed/kind/name
                if fixture.sha(src)!=entry['sha256']:raise RuntimeError('Source changed: '+str(src))
                os.link(src,self.root/kind/name)
        shutil.copyfile(seed/'index.json',self.root/'index.json')
        for name,key in [('ffmpeg.exe','ExecutableSha256'),('ffprobe.exe','ProbeSha256')]:
            src=seed/'vendor'/name
            if fixture.sha(src)!=fixture.PINS[key]:raise RuntimeError('Tool identity changed')
            shutil.copyfile(src,self.root/'vendor'/name)
        base.write(self.root/'preparation.json',{'seed':str(seed),'verified_clone_seconds':time.perf_counter()-tick,
            'index_sha256':fixture.sha(self.root/'index.json'),'original_preparation':json.loads((seed/'preparation.json').read_text())})
        print('Prepared verified existing index/footage',flush=True)

    def freeze(self,phase):
        hashes={str(p.relative_to(REPO)):fixture.sha(p) for p in [HERE,HERE.with_name('indexed-reader-prototype.py'),
            HERE.with_name('precision-export-prototype.py'),REPO/'tests/IndexedReaderPrototype/Program.cs',
            REPO/'tests/IndexedReaderPrototype/IndexedReaderPrototype.csproj']}
        for name,digest in hashes.items():
            src=REPO/name;shutil.copyfile(src,self.root/'harness-history'/(digest+src.suffix))
        base.write(self.root/(phase+'-optimization-plan.json'),{'phase':phase,'hashes':hashes,
            'helper_hashes':self.worker.hashes,'use_managed':self.use_managed,'combined_boundaries':self.combine,
            'original_plan_sha256':fixture.sha(self.root/'plan.json'),'index_sha256':fixture.sha(self.root/'index.json')})

    def run(self,args,label,**kwargs):
        if not (self.combine and label.endswith('-boundary')):return super().run(args,label,**kwargs)
        if self.pending is None:self.pending=(list(args),label);return b'',0.0
        first,old_label=self.pending;self.pending=None
        batches=[first,list(args)];command=['-hide_banner','-v','warning','-nostdin']
        for part in batches:command+=['-i',part[part.index('-i')+1]]
        for number,part in enumerate(batches):
            tail=list(part[part.index('-map'):]);tail[tail.index('-map')+1]=f'{number}:v:0';command+=tail
        return super().run(command,label+'-combined',**kwargs)

    def feed(self,target,source,index,first,last,output):
        if self.pending is not None:
            args,label=self.pending;self.pending=None;super().run(args,label+'-single')
        if self.use_managed:return self.worker.feed(target,source.name,index,first,last,output)
        return super().feed(target,source,index,first,last,output)

    def export_indexed(self,source,start,duration,label):
        if self.pending is not None:raise RuntimeError('Unflushed boundary request')
        row=super().export_indexed(source,start,duration,label)
        row.update(optimized_harness_sha256=fixture.sha(HERE),managed_reader=self.use_managed,combined_boundaries=self.combine)
        base.write(self.root/'exports'/label/'receipt.json',row)
        return row

    def pilot(self):
        self.freeze('pilot');rows=[]
        try:
            self.begin_observed_producer()
            for label,managed,combined in [('pilot-python',False,False),('pilot-managed',True,False),('pilot-combined',True,True)]:
                self.use_managed=managed;self.combine=combined;before=time.perf_counter()
                row=self.export_indexed(self.root/'reference',Fraction(5)+Fraction(43,120),Fraction(300),label)
                after=time.perf_counter();time.sleep(.35);row['producer_progress']=self.source_health(before,after)
                rows.append(row);base.write(self.root/'pilot-results.json',rows)
                if row['status']!='committed' or not row['producer_progress']['pass']:raise RuntimeError('Pilot failed; preserve and stop')
        finally:
            if hasattr(self,'monitor'):self.end_observed_producer()
            else:self.stop_producer()
        for row in rows:
            row['validation']=self.validate_one(row);base.write(self.root/'validated-pilot-results.json',rows)
            if not row['validation']['pass']:raise RuntimeError('Pilot precision failed')
        winner=min(rows[1:],key=lambda x:x['seconds'])
        decision={'winner':winner['label'],'gain_seconds':rows[0]['seconds']-winner['seconds'],
            'managed_reader':winner['managed_reader'],'combined_boundaries':winner['combined_boundaries'],
            'substantial_progress':rows[0]['seconds']-winner['seconds']>=.20,
            'limit':'n1 per variant; fresh20 required, no percentile pass from pilots'}
        base.write(self.root/'pilot-decision.json',decision);print(json.dumps(decision),flush=True)

    def optimized_benchmark(self):
        decision=json.loads((self.root/'pilot-decision.json').read_text())
        if not decision['substantial_progress']:raise RuntimeError('Progress gate: involve user before expanding')
        self.use_managed=decision['managed_reader'];self.combine=decision['combined_boundaries']
        self.freeze('reference')
        shutil.copyfile(self.root/'validated-pilot-results.json',self.root/'validated-probe-results.json')
        super().benchmark()

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--root',type=Path,required=True)
    parser.add_argument('--seed',type=Path,default=REPO/'artifacts/indexed-reader-20261024-a')
    for action in ['plan','prepare','qualify','pilot','benchmark','validate']:parser.add_argument('--'+action,action='store_true')
    args=parser.parse_args()
    if args.plan:print(json.dumps(plan(),indent=2));return
    exp=Optimized(args.root)
    if args.prepare:exp.prepare_from(args.seed.resolve());return
    exp.load_index()
    if args.validate:exp.validate_reference();return
    exp.worker=Worker(exp.root)
    try:
        if args.qualify:exp.freeze('qualification');exp.qualify()
        elif args.pilot:exp.pilot()
        elif args.benchmark:exp.optimized_benchmark()
        else:raise ValueError('Choose an action')
    finally:exp.worker.close()

if __name__=='__main__':main()
