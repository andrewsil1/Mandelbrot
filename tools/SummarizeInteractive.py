"""Summarize completed stages; export stored MPFR coordinates as exact decimals."""
import csv
import json
import re
import struct
from fractions import Fraction
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIRECTORY = ROOT / 'docs' / 'repair-validation'


def exact_decimal(parts):
    value = sum((Fraction.from_float(struct.unpack('>d', bytes.fromhex(p))[0]) for p in parts), Fraction())
    numerator, denominator = value.numerator, value.denominator
    places = denominator.bit_length() - 1
    assert denominator == 1 << places
    digits = str(abs(numerator) * 5 ** places).rjust(places + 1, '0')
    text = digits if not places else (digits[:-places] + '.' + digits[-places:]).rstrip('0').rstrip('.')
    return ('-' if numerator < 0 else '') + text


summaries, coordinates = [], []
for path in sorted(DIRECTORY.glob('stage-*.jsonl'), key=lambda p: p.name):
    if not re.fullmatch(r'stage-\d+\.jsonl', path.name):
        continue
    records = [json.loads(line) for line in path.read_text().splitlines()]
    frames = [r for r in records if r['phase'] == 'completed']
    assert len(frames) == 12 and all(r['passed'] for r in frames), path
    assert all(r['Timings']['RepairIterations'] <= 120_000_000 for r in frames), path
    gpu = [json.loads(line) for line in path.with_suffix('.gpu.jsonl').read_text(encoding='utf-8-sig').splitlines()]
    gpu_values = [next(csv.reader([r['adapterWide']])) for r in gpu]
    pid = gpu[0]['pid']
    journals = list((ROOT / 'tests/RendererChecks/bin/Release/net8.0-windows/dispatch-logs').glob(f'dispatch-{pid}-*.jsonl'))
    assert len(journals) == 1, journals
    pending_submissions, pending_groups = set(), set()
    submissions = groups = max_buffer_bytes = 0
    for line in journals[0].open():
        entry = json.loads(line)
        phase = entry['phase']
        if phase == 'group-begin':
            assert entry['group'] not in pending_groups
            pending_groups.add(entry['group'])
            groups += 1
        elif phase == 'begin':
            assert entry['submission'] not in pending_submissions
            if 'journalGroup' in entry:
                assert entry['journalGroup'] in pending_groups
            pending_submissions.add(entry['submission'])
            submissions += 1
            max_buffer_bytes = max(max_buffer_bytes, sum(entry.get('resources', {}).values()))
        elif phase == 'end':
            pending_submissions.remove(entry['submission'])
        elif phase == 'group-end':
            pending_groups.remove(entry['group'])
        elif phase != 'test':
            raise AssertionError((path, 'Unexpected journal phase', phase))
    assert not pending_submissions and not pending_groups, journals[0]
    summary = dict(stage=int(path.stem.split('-')[1]), frames=len(frames),
        maxWidth=max(r['width'] for r in frames), height=frames[0]['height'],
        maxRepairWork=max(r['Timings']['RepairIterations'] for r in frames),
        maxRepaired=max(r['RepairedCount'] for r in frames),
        maxReferencePasses=max(r['ReferencePasses'] for r in frames),
        maxDispatchMs=max(r['Timings']['MaxDispatchMilliseconds'] for r in frames),
        peakWorkingSetMiB=max(r['processLifetimePeakWorkingSet'] for r in frames) / 2**20,
        maxPrivateEndpointMiB=max(r['privateAfter'] for r in frames) / 2**20,
        adapterWideUtilizationPercent=[min(float(g[2]) for g in gpu_values), max(float(g[2]) for g in gpu_values)],
        adapterWideMemoryMiB=[min(float(g[3]) for g in gpu_values), max(float(g[3]) for g in gpu_values)],
        journal=str(journals[0].relative_to(ROOT)), submissions=submissions, groups=groups,
        maxDeclaredBufferSnapshotBytes=max_buffer_bytes)
    for fixture in ('transition', 'tip'):
        selected = [r for r in frames if r['fixture'] == fixture]
        summary[fixture] = dict(latencyMs=[min(r['latencyMs'] for r in selected), max(r['latencyMs'] for r in selected)],
            repairMs=[min(r['Timings']['RepairMilliseconds'] for r in selected), max(r['Timings']['RepairMilliseconds'] for r in selected)],
            cpuMs=[min(r['cpuMs'] for r in selected), max(r['cpuMs'] for r in selected)],
            fallbackPixels=[min(r['Float64GlitchCount'] for r in selected), max(r['Float64GlitchCount'] for r in selected)])
    summaries.append(summary)
    for frame in frames:
        c = frame['coordinates']
        coordinates.append(dict(stage=summary['stage'], fixture=frame['fixture'], action=frame['action'],
            width=frame['width'], height=frame['height'], iterationBudget=frame['budget'],
            centerX=exact_decimal(c['x']), centerY=exact_decimal(c['y']), verticalSpan=exact_decimal(c['height']),
            aspect=exact_decimal([c['aspectBits']])))

summaries.sort(key=lambda r: r['stage'])
(DIRECTORY / 'summary.json').write_text(json.dumps(summaries, indent=2) + '\n')
(DIRECTORY / 'exact-viewports.json').write_text(json.dumps(coordinates, indent=2) + '\n')
print(json.dumps(summaries, indent=2))
