"""Fresh-consumer check for the PyPI package (E-5): the installed distribution is the expected version and imports."""
import sys
from importlib.metadata import version

import aidotnet_evolution

expected = sys.argv[1]
installed = version("aidotnet-evolution-worker")
if installed != expected:
    raise SystemExit(f"installed aidotnet-evolution-worker {installed}, expected {expected}")
missing = [name for name in aidotnet_evolution.__all__ if not hasattr(aidotnet_evolution, name)]
if missing:
    raise SystemExit(f"aidotnet_evolution does not export {missing}")
print(f"aidotnet-evolution-worker {installed}: {len(aidotnet_evolution.__all__)} exports")