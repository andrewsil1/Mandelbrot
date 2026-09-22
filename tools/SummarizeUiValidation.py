"""Validate a finished live UI session and export exact coordinates and metrics."""
import json
import struct
import sys
from fractions import Fraction
from pathlib import Path


def read(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]


def exact(parts):
    value = sum((Fraction.from_float(struct.unpack('>d', bytes.fromhex(p))[0]) for p in parts), Fraction())
    n, d = value.numerator, value.denominator
    places = d.bit_length() - 1
    assert d == 1 << places
    digits = str(abs(n) * 5 ** places).rjust(places + 1, '0')
    result = digits if not places else (digits[:-places] + '.' + digits[-places:]).rstrip('0').rstrip('.')
    return ('-' if n < 0 else '') + result


directory = Path(sys.argv[1])
records = read(directory / 'frames.jsonl')
ui = read(directory / 'frames.jsonl.ui.jsonl')
starts = [r for r in records if r['phase'] == 'started']
ends = [r for r in records if r['phase'] == 'completed']
ui_starts = [r for r in ui if r['phase'] == 'render-start']
ui_ends = [r for r in ui if r['phase'] == 'render-complete']
assert len(starts) == len(ends) == len(ui_starts) == len(ui_ends) > 0
assert not any(r['phase'] in ('failed', 'error', 'suspended') for r in records + ui)
active = None
for r in records:
    if r['phase'] == 'started':
        assert active is None, 'Overlapping renderer frames'
        active = r['id']
    else:
        assert r['phase'] == 'completed' and active == r['id']
        active = None
assert active is None
rows = []
for index, (s, e, us, ue) in enumerate(zip(starts, ends, ui_starts, ui_ends), 1):
    assert s['id'] == e['id']
    for key in ('width', 'height', 'x', 'y', 'span', 'aspectBits'):
        assert s[key] == us[key] == ue[key], (index, key)
    assert e['UnresolvedGlitchCount'] == 0
    assert e['Validation'] == {'Samples': 64, 'Mismatches': 0, 'Unresolved': 0}
    assert e['Timings']['RepairIterations'] <= 120_000_000
    rows.append(dict(frame=index, fixture=us['fixture'], width=s['width'], height=s['height'], budget=s['budget'],
        centerX=exact(s['x']), centerY=exact(s['y']), verticalSpan=exact(s['span']), aspect=exact([s['aspectBits']]),
        rendererMs=e['rendererLatencyMs'], uiRenderMs=ue['detail']['elapsedMs'],
        applied=ue['detail']['applied'], latest=ue['detail']['latestView'], queued=ue['detail']['queued'],
        fallbackPixels=e['Float64GlitchCount'], fallback=e['UsedDoubleDoubleFallback'], refs=e['ReferencePasses'],
        repaired=e['RepairedCount'], repairWork=e['Timings']['RepairIterations'], repairMs=e['Timings']['RepairMilliseconds'],
        journalMs=e['Timings']['JournalMilliseconds'], cpuMs=e['cpuMs'], privateMiB=e['privateBytes']/2**20,
        workingSetMiB=e['workingSet']/2**20, maxDispatchMs=e['Timings']['MaxDispatchMilliseconds']))
last_request = [r for r in ui if r['phase'] == 'request'][-1]
assert ui_ends[-1]['detail']['applied'] and ui_ends[-1]['detail']['latestView'] and not ui_ends[-1]['detail']['queued']
for key in ('width', 'height', 'x', 'y', 'span', 'aspectBits'):
    assert last_request[key] == ui_ends[-1][key], 'Final display differs from last requested viewport'

# Dispatch journal must close every submission and group before session shutdown.
submissions, groups = set(), set()
journal_count = 0
for path in (directory / 'dispatch').glob('*.jsonl'):
    for r in read(path):
        journal_count += 1
        if r['phase'] == 'begin':
            assert r['submission'] not in submissions
            submissions.add(r['submission'])
        elif r['phase'] == 'end':
            submissions.remove(r['submission'])
        elif r['phase'] == 'group-begin':
            assert r['group'] not in groups
            groups.add(r['group'])
        elif r['phase'] == 'group-end':
            groups.remove(r['group'])
        else:
            raise AssertionError(r['phase'])
assert journal_count and not submissions and not groups
health = json.loads((directory / 'health.json').read_text(encoding='utf-8-sig'))
assert not health, health
resources = read(directory / 'resources.jsonl')
summary = dict(frames=len(rows), requests=sum(r['phase'] == 'request' for r in ui),
    obsoleteSizeFramesDiscarded=sum(not r['applied'] for r in rows),
    olderViewFramesWithFollowup=sum(r['applied'] and not r['latest'] and r['queued'] for r in rows),
    maxRepairWork=max(r['repairWork'] for r in rows), maxRepairPixels=max(r['repaired'] for r in rows),
    maxDispatchMs=max(r['maxDispatchMs'] for r in rows),
    peakSamplePrivateMiB=max(r['privateBytes'] for r in resources)/2**20,
    peakSampleWorkingSetMiB=max(r['workingSet'] for r in resources)/2**20,
    minHandles=min(r['handles'] for r in resources), maxHandles=max(r['handles'] for r in resources),
    journalRecords=journal_count, finalViewportMatchesLastRequest=True, adverseHealthEvents=health)
replay = next(r for r in read(directory.parent / 'stage-3804.jsonl')
              if r['phase'] == 'completed' and r['fixture'] == 'transition' and r['action'] == 'base')
matched = []
for index, (s, e) in enumerate(zip(starts, ends), 1):
    c = replay['coordinates']
    if (s['width'], s['height'], s['budget'], s['x'], s['y'], s['span'], s['aspectBits']) == (
            replay['width'], replay['height'], replay['budget'], c['x'], c['y'], c['height'], c['aspectBits']):
        for key in ('Float64GlitchCount', 'ReferencePasses', 'RepairedCount', 'UnresolvedGlitchCount'):
            assert e[key] == replay[key]
        assert e['Timings']['RepairIterations'] == replay['Timings']['RepairIterations']
        matched.append(index)
assert matched, 'No exact live/replay viewport and iteration-budget comparison'
summary['exactReplayMatches'] = matched
summary['replayTransitionMs'] = replay['latencyMs']
(directory / 'exact-frames.json').write_text(json.dumps(rows, indent=2))
(directory / 'summary.json').write_text(json.dumps(summary, indent=2))
print(json.dumps(summary, indent=2))
for r in rows:
    print(f"{r['frame']:2} {r['fixture']:10} {r['width']}x{r['height']} {r['rendererMs']:.1f} ms, "
          f"repaired={r['repaired']} work={r['repairWork']} applied={r['applied']} latest={r['latest']}")
