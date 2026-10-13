"""Counts every database.feature_dimensions value declared across an OpenEvolve checkout.

    python eng/census-openevolve-feature-dimensions.py <checkout>

The counts back the coverage table in docs/parity/OPENEVOLVE_EXAMPLES.md. Re-run it after bumping
the pinned upstream ref: a dimension nobody has mapped is passed through as an evaluator metric, and
an evaluator that does not report it on every completed evaluation ends the run with no candidates.

Inline comments after a quoted list item are not stripped, so a value may appear with its comment
attached; the importer reads the same files with a YAML parser and is unaffected.
"""
import collections
import os
import re
import sys

ROOT = os.path.join(sys.argv[1] if len(sys.argv) > 1 else ".", "examples")

dims = collections.Counter()
where = collections.defaultdict(set)

for dirpath, _, names in os.walk(ROOT):
    for name in names:
        if not name.endswith(('.yaml', '.yml')):
            continue
        path = os.path.join(dirpath, name)
        text = open(path, encoding='utf-8', errors='replace').read().replace('\r\n', '\n')
        family = os.path.relpath(path, ROOT).replace(os.sep, '/').split('/')[0]

        for match in re.finditer(r'^[ \t]*feature_dimensions:[ \t]*(.*)$', text, re.M):
            inline = match.group(1).split('#')[0].strip()
            found = []
            if inline.startswith('['):
                found = re.findall(r'["\']?([A-Za-z_][A-Za-z0-9_]*)["\']?', inline)
            else:
                for line in text[match.end():].split('\n'):
                    stripped = line.strip()
                    if stripped.startswith('-'):
                        found.append(stripped.lstrip('- ').strip().strip('"\''))
                    elif stripped and not stripped.startswith('#'):
                        break
            for dimension in found:
                dims[dimension] += 1
                where[dimension].add(family)

print('declared feature dimensions across the corpus:')
for dimension, count in dims.most_common():
    print('  %-16s %3d  families: %s' % (dimension, count, ','.join(sorted(where[dimension]))))
