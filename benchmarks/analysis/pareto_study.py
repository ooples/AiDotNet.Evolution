"""Independently validate and summarize the fixed US-09 development campaign."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics


def volume(points):
    """Exact axis-slab union; accepts raw minimizing objectives in the fixed [0,2] domain."""
    if not points:
        return 0.0
    if len(points[0]) == 1:
        return 1 - min(p[0] for p in points) / 2
    levels = sorted({p[-1] for p in points} | {2.0})
    return sum((b - a) / 2 * volume([p[:-1] for p in points if p[-1] <= a])
               for a, b in zip(levels, levels[1:]))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate(report):
    require(report['Protocol'] == 'pareto-development-v1' and report['Passed'] is True, 'Campaign did not pass')
    require(report['Seeds'] == 24 and report['Budget'] == 128 and report['Capacity'] == 64, 'Changed campaign plan')
    require(report['ObjectiveBounds'] == [0, 2] and report['NormalizedReference'] == '(1,...,1)' and report['Tolerance'] == 0,
            'Changed objective normalization')
    rows, replays = report['Rows'], report['Replays']
    require(len(rows) == len(replays) == 144, 'Incomplete campaign')
    expected = {(d, m, s) for d in (2, 3) for m in ('scalar-single', 'scalar-map64', 'pareto64') for s in range(24)}
    def key(row):
        return row['Dimensions'], row['Method'], row['Seed']
    require({key(r) for r in rows} == expected and {key(r) for r in replays} == expected, 'Missing/duplicate runs')
    replay_by_key = {key(r): r for r in replays}
    for row in rows + replays:
        d = row['Dimensions']
        require(row['Status'] == 'completed' and row['Error'] is None and row['Calls'] == row['Cost'] == row['Budget'] == 128,
                'Failure or unequal charged budget')
        require(len(row['Samples']) == row['Proposals'] == 128, 'Unaccounted proposals')
        require(len({s['Id'] for s in row['Samples']}) == 128, 'Duplicate sample identities')
        samples = {s['GenomeId']: s for s in row['Samples']}
        require(len(samples) == 128, 'Duplicate measured genomes')
        for sample in row['Samples']:
            require(sample['Attempts'] == sample['Cost'] == 1, 'Uncharged evaluation')
            require(len(sample['Objectives']) == d and all(math.isfinite(v) and 0 <= v <= 2 for v in sample['Objectives']), 'Bad vector')
            require(len(sample['Violations']) == 1 and math.isfinite(sample['Violations'][0]) and sample['Violations'][0] >= 0, 'Bad constraints')
            require(sample['Status'] == ('Completed' if sample['Violations'][0] == 0 else 'Rejected'), 'Constraint gate differs')
            require(math.isclose(sample['Quality'], -statistics.mean(sample['Objectives']), abs_tol=1e-12), 'Changed scalarization')
        require(row['FeasibleSamples'] == sum(s['Status'] == 'Completed' for s in row['Samples']), 'Wrong feasible count')
        elites = row['Elites']
        require(0 < len(elites) == row['RetainedCount'] <= (1 if row['Method'] == 'scalar-single' else 64), 'Invalid capacity')
        require(len({e['GenomeId'] for e in elites}) == len(elites), 'Duplicate elites')
        for elite in elites:
            sample = samples[elite['GenomeId']]
            require(sample['Status'] == 'Completed' and sample['Objectives'] == elite['Objectives'] and sample['Quality'] == elite['Quality'],
                    'Elite not backed by feasible measurement')
        vectors = [e['Objectives'] for e in elites]
        if row['Method'] == 'pareto64':
            for i, a in enumerate(vectors):
                for b in vectors[i + 1:]:
                    require(not all(x <= y for x, y in zip(a, b)) and not all(y <= x for x, y in zip(a, b)), 'Dominated/equivalent front')
        require(math.isfinite(row['Hypervolume']) and math.isclose(volume(vectors), row['Hypervolume'], abs_tol=1e-12), 'Hypervolume mismatch')
        require(row['ObjectiveMinima'] == [min(v[i] for v in vectors) for i in range(d)] and
                row['ObjectiveMaxima'] == [max(v[i] for v in vectors) for i in range(d)], 'Objective extrema mismatch')
    for row in rows:
        replay = replay_by_key[key(row)]
        require(row['ReplayMatched'] is True and all(row[field] == replay[field] for field in
            ('StateHash', 'InitialHash', 'Samples', 'Elites', 'Hypervolume', 'Calls', 'Cost')), 'Worker replay mismatch')
    for d in (2, 3):
        for s in range(24):
            paired = [r for r in rows if r['Dimensions'] == d and r['Seed'] == s]
            require(len({r['InitialHash'] for r in paired}) == 1, 'Unpaired initial populations')
            require(all([sample['GenomeId'] for sample in r['Samples'][:8]] ==
                        [sample['GenomeId'] for sample in paired[0]['Samples'][:8]] for r in paired), 'Changed initial seeds')
    return rows


def summarize(report):
    rows = validate(report)
    groups = []
    for d in (2, 3):
        for method in ('scalar-single', 'scalar-map64', 'pareto64'):
            selected = [r for r in rows if r['Dimensions'] == d and r['Method'] == method]
            groups.append({'Dimensions': d, 'Method': method, 'MedianHypervolume': statistics.median(r['Hypervolume'] for r in selected),
                           'MedianRetainedCount': statistics.median(r['RetainedCount'] for r in selected),
                           'MedianObjectiveMinima': [statistics.median(r['ObjectiveMinima'][i] for r in selected) for i in range(d)],
                           'MedianObjectiveMaxima': [statistics.median(r['ObjectiveMaxima'][i] for r in selected) for i in range(d)],
                           'MedianFeasibleSamples': statistics.median(r['FeasibleSamples'] for r in selected),
                           'MedianMillisecondsDescriptiveOnly': statistics.median(r['Milliseconds'] for r in selected)})
    contrasts = []
    for d in (2, 3):
        for baseline in ('scalar-single', 'scalar-map64'):
            differences = []
            for seed in range(24):
                def hv(method):
                    return next(r['Hypervolume'] for r in rows if (r['Dimensions'], r['Method'], r['Seed']) == (d, method, seed))
                differences.append(hv('pareto64') - hv(baseline))
            contrasts.append({'Dimensions': d, 'Baseline': baseline, 'PairedHypervolumeDifferences': differences,
                              'MedianDifference': statistics.median(differences), 'Wins': sum(v > 1e-12 for v in differences),
                              'Ties': sum(abs(v) <= 1e-12 for v in differences), 'Losses': sum(v < -1e-12 for v in differences)})
    return {'Protocol': report['Protocol'], 'SourceRevision': report['SourceRevision'], 'PrimaryRuns': 144, 'ReplayRuns': 144,
            'ChargedCallsIncludingReplays': sum(r['Calls'] for r in report['Rows'] + report['Replays']),
            'Groups': groups, 'Contrasts': contrasts,
            'Scope': 'Authored development fixtures; not runtime-speed, held-out, or OpenEvolve superiority evidence.'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('report', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    raw = args.report.read_bytes()
    summary = summarize(json.loads(raw))
    summary['RawSha256'] = hashlib.sha256(raw).hexdigest()
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(summary, stream, indent=2, allow_nan=False)
