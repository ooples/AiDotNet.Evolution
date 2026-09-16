import hashlib
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

from program_evidence import verify


class ProgramEvidenceTests(unittest.TestCase):
    def fixture(self, root, names=("one/data.json", "two/data.json"), bad_blob=False, duplicate=False):
        archive = root / "evidence.tar.xz"
        identity = hashlib.sha256(b"{}").hexdigest()
        index = dict(schema="evolution-content-addressed-evidence-v1", files=[dict(path=n, sha256=identity, bytes=2) for n in names])
        with tarfile.open(archive, "w:xz") as output:
            entries = [("blobs/" + identity, b"xx" if bad_blob else b"{}"), ("index.json", json.dumps(index).encode())]
            if duplicate:
                entries.append(entries[0])
            for name, body in entries:
                member = tarfile.TarInfo(name)
                member.size = len(body)
                output.addfile(member, io.BytesIO(body))
        return archive, hashlib.sha256(archive.read_bytes()).hexdigest()

    def test_verified_restore_reconstructs_duplicates_without_shared_mutable_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive, digest = self.fixture(root)
            target = root / "restored"
            result = verify(archive, digest, target)
            self.assertEqual(2, result["files"])
            self.assertEqual(1, result["verified_blobs"])
            (target / "one/data.json").write_bytes(b"changed")
            self.assertEqual(b"{}", (target / "two/data.json").read_bytes())
            with self.assertRaisesRegex(ValueError, "must be new"):
                verify(archive, digest, target)

    def test_bad_outer_or_blob_hash_writes_nothing(self):
        for bad_blob in (False, True):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                archive, digest = self.fixture(root, bad_blob=bad_blob)
                target = root / "restored"
                with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
                    verify(archive, digest if bad_blob else "0" * 64, target)
                self.assertFalse(target.exists())

    def test_traversal_windows_aliases_and_case_collisions_are_rejected(self):
        for names in (("../outside",), ("/outside",), ("C:/outside",), ("a\\outside",), ("NUL.txt",), ("a.",), ("a", "A"), (".",)):
            with self.subTest(names=names), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                archive, digest = self.fixture(root, names=names)
                with self.assertRaisesRegex(ValueError, "Unsafe"):
                    verify(archive, digest, root / "restored")
                self.assertFalse((root / "restored").exists())

    def test_file_directory_collision_is_rejected_before_restore(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive, digest = self.fixture(root, names=("a", "a/b"))
            with self.assertRaisesRegex(ValueError, "Conflicting"):
                verify(archive, digest, root / "restored")
            self.assertFalse((root / "restored").exists())

    def test_duplicate_archive_members_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive, digest = self.fixture(root, duplicate=True)
            with self.assertRaisesRegex(ValueError, "archive members"):
                verify(archive, digest)
