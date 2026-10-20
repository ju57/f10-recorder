"""Validate a source fixture's explicit Present-call counters, offline.

This checks submission identity only. GetLastPresentCount is not display
acknowledgment and does not identify the image returned by Desktop Duplication.
"""
import argparse
import csv
import hashlib
import json
from pathlib import Path


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate(source):
    source = source.resolve(strict=True)
    paths = [source / name for name in (
        'pacing-ready.json', 'source-completed.json', 'render.csv',
        'dxgi-submission-count.csv')]
    ready, end = [json.loads(p.read_text('utf-8-sig')) for p in paths[:2]]
    with paths[2].open(newline='') as file:
        rendered = list(csv.DictReader(file))
    with paths[3].open(newline='') as file:
        submitted = list(csv.DictReader(file))
    require(end['run_nonce'] == ready['run_nonce'], 'Source run identity differs')
    require(end['controlled_stop'] is True, 'Source did not confirm controlled stop')
    require(rendered and len(rendered) == len(submitted) == end['frames_returned'],
            'Incomplete render/submission inventory')
    require(int(ready['qpc_frequency']) > 0, 'Invalid QPC frequency')
    swapchains = set()
    previous_count = previous_qpc = None
    for ordinal, (draw, query) in enumerate(zip(rendered, submitted), 1):
        require(int(draw['id']) == int(query['source_id']) == ordinal,
                'Missing, reordered or duplicated source ID')
        require(int(query['get_last_present_count_hr']) == 0,
                'GetLastPresentCount failed')
        qpc = int(query['present_return_qpc'])
        require(int(draw['render_begin_qpc']) <= qpc <=
                int(query['count_call_return_qpc']) <= int(draw['commit_return_qpc']),
                'Present/query lies outside the matching render bracket')
        require(qpc <= int(query['count_call_begin_qpc']) <=
                int(query['count_call_return_qpc']), 'Invalid query bracket')
        count = int(query['last_present_call_count'])
        require(0 <= count <= 0xffffffff, 'Invalid UINT submission count')
        if previous_count is not None:
            require(count == previous_count + 1,
                    'Counter gap, reset, wrap or duplicate requires separate qualification')
            require(qpc > previous_qpc, 'Present-return QPC did not advance')
        previous_count, previous_qpc = count, qpc
        swapchains.add(query['swapchain_hex'])
    require(len(swapchains) == 1, 'Swap-chain change requires separate qualification')
    elapsed = (int(submitted[-1]['present_return_qpc']) -
               int(submitted[0]['present_return_qpc'])) / int(ready['qpc_frequency'])
    require(elapsed > 0, 'No measurable submission interval')
    hashes = {}
    for path in paths:
        with path.open('rb') as file:
            hashes[path.name] = hashlib.file_digest(file, 'sha256').hexdigest()
    return {
        'passed': True, 'scope': 'Explicit source ID to Present-call count only',
        'source': str(source), 'frames': len(submitted),
        'first_count': int(submitted[0]['last_present_call_count']),
        'last_count': int(submitted[-1]['last_present_call_count']),
        'present_return_call_rate': (len(submitted) - 1) / elapsed,
        'requested_source_fps': ready['fps'], 'input_sha256': hashes,
        'limits': ['No displayed-picture acknowledgment or DDA image identity',
                   'No source-to-encoded join or observer-overhead acceptance',
                   'Wrap, reset and swap-chain changes are rejected, not repaired']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = validate(args.source)
    with args.output.open('x', encoding='utf-8') as file:
        json.dump(result, file, indent=2)
    print(json.dumps({'passed': True, 'frames': result['frames']}))


if __name__ == '__main__':
    main()
