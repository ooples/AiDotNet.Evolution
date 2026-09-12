"""Offline evidence consistency checks; never opens/mutates a live coordinator store."""
import hashlib
import json
from pathlib import Path
import struct
import zipfile

REVISION = "8aaef1e40dbbad7b2f3f37addc810b959c7bc96c"
MAGIC = 0x314B524F57564541


def verify(archive):
    names = archive.namelist()
    assert len(names) == len(set(names))
    assert all(not name.startswith("/") and ".." not in name.split("/") for name in names)
    assert all(entry.file_size <= 16 * 1024 * 1024 for entry in archive.infolist())

    def document(name):
        return json.loads(archive.read(name))

    def journal(name):
        raw = archive.read(name)
        magic, revision, length = struct.unpack("<qqi", raw[:20])
        assert magic == MAGIC and revision > 0 and length == len(raw) - 52
        assert hashlib.sha256(raw[:20] + raw[52:]).digest() == raw[20:52]
        assert archive.read(name.rsplit("/", 1)[0] + "/work.owner") == struct.pack("<q", MAGIC)
        state = json.loads(raw[52:])
        assert state["Schema"] == 2
        ledger = json.loads(state["Ledger"])
        work = list(state["Work"].values())
        leases = [lease for job in work for lease in job["Leases"]]
        operations = ledger["Operations"]
        assert {item["Id"] for item in operations} == {lease["LeaseId"] for lease in leases}
        for lease in leases:
            operation = next(item for item in operations if item["Id"] == lease["LeaseId"])
            assert operation["Charged"] == lease["Actual"]
            if lease["Accepted"]:
                assert lease["Result"] == "49" and not lease["Expired"] and not lease["CancelRequested"]
        return state, operations

    manifest = document("manifest.json")
    assert manifest["SourceRevision"] == REVISION
    binaries = {item["Path"]: item["Sha256"] for item in manifest["NativeBinaries"]}
    for mode in ("managed", "native"):
        report = document(f"{mode}-recovery/recovery.json")
        assert report["coreInformationalVersion"].endswith("+" + REVISION)
        assert report["verified"] and report["physicalEvaluations"] == 3
        assert len(set(report["killedProcessIds"])) == 3
        assert report["coordinatorRecoverySpent"] == report["workerRecoverySpent"] == 2
        assert report["coordinatorRecoveryReserved"] == 0 and report["workerRecoveryReserved"] == 5
        stores = [name for name in names if name.startswith(f"{mode}-recovery/") and name.endswith("/work.current")]
        assert len(stores) == 2
        for name in stores:
            state, operations = journal(name)
            assert state["SourceSessionId"] is None
            assert sum((item["Charged"] or {}).get("cost_units", 0) for item in operations) == 2
            reserved = sum(item["Maximum"]["cost_units"] for item in operations if item["Charged"] is None)
            assert reserved == (5 if "/worker-" in name else 0)
        if mode == "native":
            assert report["executableSha256"] == binaries["DurableWork-native/DurableWork.exe"]

        session = document(f"{mode}-session/session.json")
        assert session["CoreInformationalVersion"].endswith("+" + REVISION)
        assert session["RootSeed"] == "18446744073709551615"
        assert session["PhysicalEvaluations"] == 2 and session["Quality"] == 49
        assert session["Spent"] == "2" and session["Reserved"] == "0" and session["Settled"] == 1
        for field in ("CoordinatorReopenedWhileEngineAlive", "DifferentEngineRequiresExplicitFork",
                      "ExplicitStructOwnershipRequired", "PlainStructRejected", "CompletedBoundaryCheckpointRestored"):
            assert session[field]
        assert hashlib.sha256(archive.read(f"{mode}-session/engine-checkpoint.json")).hexdigest() == session["EngineCheckpointSha256"]
        stores = [name for name in names if name.startswith(f"{mode}-session/") and name.endswith("/work.current")]
        assert len(stores) == 1
        state, operations = journal(stores[0])
        assert state["SourceSessionId"] == session["SourceSessionId"]
        job = next(iter(state["Work"].values()))
        assert job["SourceLeaseId"] == session["SourceLeaseId"] and job["SourceTellAccepted"] is True
        assert job["Leases"][0]["LeaseId"] == session["WorkerLeaseId"]
        assert len(operations) == 1 and operations[0]["Charged"] == {"cost_units": 2}
        if mode == "native":
            assert session["ExecutableSha256"] == binaries["DurableSession-native/DurableSession.exe"]
    package = document("package-dll-proof.json")
    assert {item["Framework"] for item in package} == {"net10.0", "net8.0", "net471"}
    assert all(item["PackageMatches"] and len(item["Sha256"]) == 64 for item in package)


if __name__ == "__main__":
    with zipfile.ZipFile(Path(__file__).with_name("verification.zip")) as archive:
        verify(archive)
    print("Verified source/binary identities, six schema-2 journals, receipts/liabilities, session fences and checkpoint hashes.")
