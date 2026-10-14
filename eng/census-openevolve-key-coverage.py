"""Counts how many Mapped OpenEvolve config keys a C# test body actually names.

    python eng/census-openevolve-key-coverage.py

Run from the repository root. The output is an evidence measure, not a defect count: a key can be
exercised without its leaf name appearing in a test, and a leaf can appear without being asserted
on. What it does measure is where the "every supported key maps to ours" claim rests on the
catalog's own metadata rather than on a behavioural assertion.

That distinction is not academic. `database.feature_dimensions` was Mapped and catalogued, its
`score` member carried a comment saying it was the fitness rather than a metric, and the code beside
the comment passed the name through anyway - which would have ended 17 upstream runs with no
candidates. The catalog test could not catch that, because it checks that every key is listed with a
non-empty disposition, not that the mapping does what the disposition says.

The two families under examples/algotune and examples/alphaevolve_math_problems are covered
separately and verbatim by benchmarks/external/test_openevolve_configs.py, so real coverage is
higher than this number; it is still the right number to watch when adding a key.
"""
import collections
import io
import re

CATALOG = 'src/AiDotNet.Evolution.Cli/OpenEvolve/OpenEvolveConfigCatalog.cs'
TESTS = 'tests/AiDotNet.Evolution.Tests/UnitTests/OpenEvolveImportTests.cs'

catalog = io.open(CATALOG, encoding='utf-8').read()
entries = re.findall(r'new\("([a-z_.]+)",\s*OpenEvolveKeyDisposition\.(\w+)', catalog)
mapped = sorted({key for key, disposition in entries if disposition == 'Mapped'})

tests = io.open(TESTS, encoding='utf-8').read()
# The test file lists every upstream key in one array to check the catalog is complete. Exclude it,
# or every key would count as covered by that list alone.
start = tests.index('UpstreamKeys')
bodies = tests[:start] + tests[tests.index('[Fact]', start):]

named = [key for key in mapped if key.split('.')[-1] in bodies]
missing = [key for key in mapped if key not in named]

counts = collections.Counter(disposition for _, disposition in entries)
print('catalog dispositions:')
for disposition, count in counts.most_common():
    print('  %-22s %3d' % (disposition, count))

print()
print('Mapped keys: %d; named in a test body: %d; not named: %d'
      % (len(mapped), len(named), len(missing)))
print()
print('not named by any test body:')
for key in missing:
    print('  ' + key)
