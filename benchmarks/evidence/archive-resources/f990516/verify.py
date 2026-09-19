"""Offline consistency verification; bounded extraction only into a fresh owned temporary directory."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parent
sys.path.insert(0, str(root.parents[2] / "analysis"))
from analyze import require
from run_archive_resources import verify_directory

revision = "f990516a31c5e6c9f2e7f7110373581a9d460cfc"
integrity = json.loads((root / "integrity.json").read_text(encoding="utf-8"))
archive_path = root / "verification.zip"
require(integrity["SourceRevision"] == revision and archive_path.stat().st_size == integrity["ArchiveBytes"], "Archive identity/size mismatch.")
require(hashlib.sha256(archive_path.read_bytes()).hexdigest() == integrity["ArchiveSha256"], "Archive checksum mismatch.")
with zipfile.ZipFile(archive_path) as archive, tempfile.TemporaryDirectory(prefix="archive-resource-audit-") as directory:
    names = archive.namelist()
    require(len(names) == len(set(names)) == integrity["Entries"] and len(names) <= 3000, "Duplicate/excessive archive entries.")
    require(all(not PurePosixPath(name).is_absolute() and ".." not in PurePosixPath(name).parts and "\\" not in name and ":" not in name for name in names), "Unsafe archive path.")
    require(all(entry.file_size <= 20 * 1024 * 1024 for entry in archive.infolist()) and sum(entry.file_size for entry in archive.infolist()) <= 512 * 1024 * 1024, "Archive extraction exceeds bounds.")
    manifest = json.loads(archive.read("manifest.json"))
    require(manifest["SourceRevision"] == revision and set(manifest["Files"]) == set(names) - {"manifest.json"}, "Manifest differs from archive.")
    for name, digest in manifest["Files"].items():
        require(hashlib.sha256(archive.read(name)).hexdigest() == digest, "Artifact checksum mismatch: " + name)
    archive.extractall(directory)
    extracted = Path(directory)
    primary = verify_directory(extracted / "primary")
    require(primary["ScheduledCases"] == 512 and primary["FailedOrIncompleteCases"] == 0 and primary["QualityReplayEqual"], "Primary verification failed.")
    require(primary["MeasuredPhysicalEvaluationsByPhase"] == {"primary": 65536, "replay": 65536}, "Missing paid replay work.")
    smoke = verify_directory(extracted / "smoke/campaign")
    require(smoke["ScheduledCases"] == 32 and smoke["FailedOrIncompleteCases"] == 0 and smoke["QualityReplayEqual"], "Smoke verification failed.")
    for method in ("SparseGrid", "FixedCentroid"):
        low = json.loads((extracted / f"smoke/low-memory-{method}.json").read_bytes())
        require(low["Measurement"]["MemoryBudgetExceeded"] and low["Runs"][0]["EvaluatorCalls"] == 0 and low["Runs"][0]["ReferenceUtility"] == 0, "Low-memory admission failed.")
        high = json.loads((extracted / f"smoke/64d-{method}.json").read_bytes())["Runs"][0]
        require((high["Status"], high["EvaluatorCalls"]) == (("configuration-failed", 0) if method == "SparseGrid" else ("completed", 8)), "High-dimensional support contract changed.")
    interrupted = json.loads((extracted / "interrupted/interruption.json").read_bytes())
    require(interrupted["Status"] == "interrupted-instrumentation-defect" and interrupted["PhysicalCallsForUnreportedCases"] is None, "Interrupted physical work must remain unknown.")
    require(len(list((extracted / "interrupted").glob("*.record.json"))) == interrupted["RetainedCaseRecords"] == 20, "Interrupted records omitted.")
    failed_records = [json.loads(path.read_bytes()) for path in (extracted / "interrupted").glob("*.record.json")]
    require(sum(item["Report"] is None for item in failed_records) == 2, "Unreported interrupted-campaign workers changed.")
    for item in failed_records:
        if item["Report"] is not None:
            raw = (extracted / "interrupted" / item["ReportFile"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == item["ReportSha256"] and json.loads(raw) == item["Report"], "Interrupted raw report changed.")
    old_plan = json.loads((extracted / "interrupted/plan.json").read_bytes())
    new_plan = json.loads((extracted / "primary/plan.json").read_bytes())
    excluded = {"SourceRevision", "Protocol", "SchemaVersion", "MemoryObservationStride"}
    require({key: value for key, value in old_plan.items() if key not in excluded} ==
            {key: value for key, value in new_plan.items() if key not in excluded}, "Instrumentation correction changed the experiment design.")
    proof = json.loads((extracted / "package-proof.json").read_text(encoding="utf-8-sig"))
    require(proof["SourceRevision"] == revision and {item["Framework"] for item in proof["Assets"]} == {"net10.0", "net8.0", "net471"}, "Package source/assets mismatch.")
    require(all(item["PackedSha256"] == item["BuiltSha256"] for item in proof["Assets"]), "Stale packaged asset.")
    for framework in ("net10.0", "net8.0", "net471"):
        trx = ET.parse(extracted / f"tests/{framework}.trx")
        counters = trx.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
        require(counters is not None and counters.attrib["passed"] == counters.attrib["total"] == "664", "Core test failures or skips.")
    print(json.dumps({key: value for key, value in primary.items() if key not in ("ContextPairs", "SeedEffects", "ResourceComparisons")}, indent=2))
    print("Raw primary/replay, failed instrumentation, smoke contracts, test receipts and package evidence verified; hashes are integrity, not authenticity.")
