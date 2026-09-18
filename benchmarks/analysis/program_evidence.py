"""Verify or restore a content-addressed tar.xz evidence archive into a new directory."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import tarfile

from analyze import require


def verify(archive, expected_sha256, output=None):
    archive = Path(archive)
    require(re.fullmatch(r"[0-9a-f]{64}", expected_sha256) is not None, "Invalid expected archive SHA-256.")
    with archive.open("rb") as stream:
        require(hashlib.file_digest(stream, "sha256").hexdigest() == expected_sha256, "Archive SHA-256 mismatch.")
    destination = Path(output).resolve() if output is not None else None
    if destination is not None:
        require(not destination.exists(), "Restore destination must be new.")
    with tarfile.open(archive, "r:xz") as source:
        members, expanded = [], 0
        for member in source:
            expanded += member.size
            require(len(members) < 50001 and member.isfile() and 0 <= member.size <= 32 * 1024**2 and
                    expanded <= 8 * 1024**3, "Invalid archive members.")
            members.append(member)
        by_name = {m.name: m for m in members}
        require(len(members) <= 50001 and len(by_name) == len(members) and
                all(m.isfile() and 0 <= m.size <= 32 * 1024**2 for m in members), "Invalid archive members.")
        require("index.json" in by_name, "Missing evidence index.")
        index = json.load(source.extractfile(by_name["index.json"]))
        require(index.get("schema") == "evolution-content-addressed-evidence-v1" and
                isinstance(index.get("files"), list) and len(index["files"]) <= 50000, "Invalid evidence index.")
        groups, paths, total = {}, set(), 0
        for row in index["files"]:
            name, identity, size = row["path"], row["sha256"], row["bytes"]
            require(isinstance(name, str) and 0 < len(name) <= 4096, "Invalid evidence path.")
            path = PurePosixPath(name)
            require(path.parts and not path.is_absolute() and path.as_posix() == name and ".." not in path.parts and
                    not any(c in name for c in '\\:<>"|?*') and not any(ord(c) < 32 for c in name) and
                    all(p.rstrip(" .") == p and not re.fullmatch(r"(?i)(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?", p) for p in path.parts) and
                    name.casefold() not in paths, "Unsafe or duplicate evidence path.")
            require(isinstance(identity, str) and re.fullmatch(r"[0-9a-f]{64}", identity) is not None and
                    type(size) is int and 0 <= size <= 32 * 1024**2, "Invalid evidence blob identity/size.")
            paths.add(name.casefold())
            total += size
            require(total <= 8 * 1024**3, "Restored evidence exceeds 8 GiB.")
            group = groups.setdefault(identity, dict(size=size, paths=[]))
            require(group["size"] == size, "Inconsistent blob size.")
            group["paths"].append(path)
        require(all(not any("/".join(PurePosixPath(name).parts[:i]).casefold() in paths
                            for i in range(1, len(PurePosixPath(name).parts))) for name in paths), "Conflicting evidence paths.")
        require(set(by_name) == {"index.json", *("blobs/" + h for h in groups)}, "Unexpected archive member.")
        # Iterate physical member order: arbitrary seeks into solid XZ are costly.
        blobs = [m for m in members if m.name != "index.json"]
        for member in blobs:
            identity = member.name.removeprefix("blobs/")
            require(member.size == groups[identity]["size"], "Blob size mismatch.")
            with source.extractfile(member) as stream:
                require(hashlib.file_digest(stream, "sha256").hexdigest() == identity, "Blob SHA-256 mismatch.")
        # Nothing is written before every blob and path has passed verification.
        if destination is not None:
            destination.mkdir(parents=True, exist_ok=False)
            for member in blobs:
                identity = member.name.removeprefix("blobs/")
                data = source.extractfile(member).read()
                require(hashlib.sha256(data).hexdigest() == identity, "Archive changed during restore.")
                for path in groups[identity]["paths"]:
                    target = destination.joinpath(*path.parts)
                    require(target.resolve().is_relative_to(destination), "Restore path escaped destination.")
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with target.open("xb") as stream:
                        stream.write(data)
    return dict(files=len(paths), verified_blobs=len(groups), restored_bytes=total if destination is not None else 0,
                sha256=expected_sha256)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.archive, args.sha256, args.output)))
