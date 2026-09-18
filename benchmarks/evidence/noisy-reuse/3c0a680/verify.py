"""Offline raw-evidence verification directly inside a bounded ZIP; no extraction or execution."""
from dataclasses import dataclass, field
from fnmatch import fnmatchcase
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import sys
from types import SimpleNamespace
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parent
sys.path.insert(0, str(root.parents[2] / "analysis"))
from analyze import load_json, require
from analyze_reuse import analyze


@dataclass(frozen=True, order=True)
class ZipPath:
    # Only used within this verifier's single, already bounded archive.
    archive: object = field(compare=False, repr=False)
    children: object = field(compare=False, repr=False)
    at: str

    def __truediv__(self, other):
        return ZipPath(self.archive, self.children, self.at + "/" + str(other))

    @property
    def name(self):
        return PurePosixPath(self.at).name

    @property
    def stem(self):
        return PurePosixPath(self.at).stem

    def stat(self):
        return SimpleNamespace(st_size=self.archive.getinfo(self.at).file_size)

    def read_bytes(self):
        return self.archive.read(self.at)

    def glob(self, pattern):
        return [self / name for name in self.children.get(self.at, ()) if fnmatchcase(name, pattern)]


revision = "3c0a6803d02bd0b6848a02a47137dec6db703ac4"
integrity, _ = load_json(root / "integrity.json", 4096)
require(integrity["SourceRevision"] == revision and 0 < integrity["ArchiveBytes"] <= 160 * 1024 * 1024 and
        1 <= len(integrity["Parts"]) <= 4, "Archive identity/size mismatch.")
parts = []
for index, part in enumerate(integrity["Parts"], 1):
    require(part["File"] == f"verification.zip.{index:03}", "Unsafe part name.")
    path = root / part["File"]
    require(path.stat().st_size == part["Bytes"] <= 40 * 1024 * 1024, "Part size mismatch.")
    raw = path.read_bytes()
    require(hashlib.sha256(raw).hexdigest() == part["Sha256"], "Part checksum mismatch.")
    parts.append(raw)
archive_bytes = b"".join(parts)
del parts, raw
require(len(archive_bytes) == integrity["ArchiveBytes"] and hashlib.sha256(archive_bytes).hexdigest() == integrity["ArchiveSha256"], "Archive checksum mismatch.")
with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
    names = archive.namelist()
    require(len(names) == len(set(names)) == integrity["Entries"] <= 250000, "Duplicate/excessive archive entries.")
    require(all(not PurePosixPath(name).is_absolute() and ".." not in PurePosixPath(name).parts and "\\" not in name and ":" not in name for name in names), "Unsafe archive path.")
    require(all(entry.file_size <= 64 * 1024 * 1024 for entry in archive.infolist()) and sum(entry.file_size for entry in archive.infolist()) <= 2 * 1024 ** 3, "Archive exceeds audit bounds.")
    children = {}
    for name in names:
        path = PurePosixPath(name)
        children.setdefault(str(path.parent), []).append(path.name)
    manifest, _ = load_json(ZipPath(archive, children, "manifest.json"), 64 * 1024 * 1024)
    require(manifest["SourceRevision"] == revision and set(manifest["Files"]) == set(names) - {"manifest.json"}, "Manifest differs from archive.")
    for name, checksum in manifest["Files"].items():
        require(hashlib.sha256(archive.read(name)).hexdigest() == checksum, "Artifact checksum mismatch: " + name)
    primary = analyze(ZipPath(archive, children, "primary"))
    recorded, _ = load_json(ZipPath(archive, children, "primary/summary-hardened.json"), 65536)
    original, _ = load_json(ZipPath(archive, children, "primary/summary.json"), 65536)
    require(primary == recorded == original and primary["RetainedRunsIncludingPrior"] == 1152 and
            primary["TotalPhysicalObservations"] == primary["DistinctPhysicalSampleIds"] == 358400 and
            primary["ReplayEqualExcludingAcquisitionIdentity"], "Primary audit differs or loses work.")
    smoke = analyze(ZipPath(archive, children, "smoke"))
    require(smoke["RetainedRunsIncludingPrior"] == 72 and smoke["TotalPhysicalObservations"] == smoke["DistinctPhysicalSampleIds"] == 22400 and
            smoke["ReplayEqualExcludingAcquisitionIdentity"], "Smoke audit differs or loses work.")
    for framework in ("net10.0", "net8.0", "net471"):
        counters = ET.fromstring(archive.read(f"tests/{framework}.trx")).find(".//{*}Counters")
        require(counters is not None and all(counters.attrib[key] == "661" for key in ("total", "executed", "passed")) and counters.attrib["failed"] == "0", "Test receipt mismatch.")
    coverage_name = next(name for name in names if name.startswith("tests/coverage/") and name.endswith("/coverage.cobertura.xml"))
    coverage = ET.fromstring(archive.read(coverage_name))
    baseline, _ = load_json(ZipPath(archive, children, "coverage-baseline.json"), 4096)
    require(float(coverage.attrib["line-rate"]) * 100 >= baseline["line"] - 1 and float(coverage.attrib["branch-rate"]) * 100 >= baseline["branch"] - 1, "Coverage ratchet failed.")
    proof, _ = load_json(ZipPath(archive, children, "tests/package-proof.json"), 65536)
    package = archive.read("tests/package/" + proof["Package"])
    require(proof["SourceRevision"] == revision and hashlib.sha256(package).hexdigest() == proof["PackageSha256"], "Package identity mismatch.")
    require({asset["Framework"] for asset in proof["Assets"]} == {"net10.0", "net8.0", "net471"}, "Missing package target.")
    with zipfile.ZipFile(io.BytesIO(package)) as packed:
        for asset in proof["Assets"]:
            checksum = hashlib.sha256(packed.read(f"lib/{asset['Framework']}/AiDotNet.Evolution.dll")).hexdigest()
            require(checksum == asset["PackedSha256"] == asset["BuiltSha256"], "Stale package asset.")
print(json.dumps({"Verified": True, "SourceRevision": revision, "PrimaryRuns": 1152, "PrimaryPhysicalObservations": 358400,
    "SmokePhysicalObservations": 22400, "TestsPerFramework": 661, "ReplayEqual": True}))
