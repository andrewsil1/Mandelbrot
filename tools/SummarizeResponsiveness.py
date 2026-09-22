"""Summarize quiet runs; compare exact output hashes across renderer revisions."""
import json
import statistics
from pathlib import Path

root = Path(__file__).resolve().parent.parent / 'docs' / 'responsiveness'
rows = []
for path in sorted(root.glob('*-*.jsonl')):
    if path.stem.split('-')[0] not in ('before', 'after'):
        continue
    records = [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines()]
    assert records[-1]['phase'] == 'passed', path
    for baseline in (r for r in records if r['phase'] == 'baseline'):
        quiet = [r for r in records if r['phase'] == 'completed' and r['fixture'] == baseline['fixture']]
        assert len(quiet) == 3 and all(r['hash'] == baseline['hash'] for r in quiet)
        rows.append(dict(revision=path.stem.split('-')[0], width=baseline['width'], height=baseline['height'],
            fixture=baseline['fixture'], quietMs=[r['milliseconds'] for r in quiet],
            medianMs=statistics.median(r['milliseconds'] for r in quiet), hash=baseline['hash'],
            refs=baseline['ReferencePasses'], repaired=baseline['RepairedCount'],
            fallback=baseline['Float64GlitchCount'], repairWork=baseline['Timings']['RepairIterations'],
            instrumentedOtherHostMs=baseline['Timings']['OtherHostMilliseconds']))
for after in (r for r in rows if r['revision'] == 'after'):
    before = next(r for r in rows if r['revision'] == 'before' and r['width'] == after['width'] and r['fixture'] == after['fixture'])
    for key in ('hash', 'refs', 'repaired', 'fallback', 'repairWork'):
        assert before[key] == after[key], (after['width'], after['fixture'], key)
    after['medianReductionPercent'] = 100 * (1 - after['medianMs'] / before['medianMs'])
(root / 'summary.json').write_text(json.dumps(rows, indent=2))
for r in rows:
    print(f"{r['revision']:6} {r['width']:4} {r['fixture']:10}: {r['quietMs']} median={r['medianMs']:.1f} ms"
          + (f" reduction={r['medianReductionPercent']:.1f}%" if 'medianReductionPercent' in r else ''))
