"""Regenerates docs/API-CLASSIFICATION.md from the declared public API, the tests, and the guides.

    python eng/generate-api-classification.py .

ApiClassificationTests enforces the same rules, so run this after adding, removing or reclassifying a public type.
A stable type with no test or no guide is printed; fix it rather than hand-editing the table.
"""

import os, pathlib, re, sys

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
type_line = re.compile(r"^(\[\w+\])?(?:(?:static|abstract|sealed|virtual|readonly|override)\s+)*([A-Za-z0-9_.`<>,]+)$")

def files(folder, patterns, exclude=()):
    for pattern in patterns:
        for path in sorted((root / folder).rglob(pattern)):
            if any(p in ("obj", "bin", "node_modules") for p in path.parts) or path.name in exclude:
                continue
            yield path, path.read_text(encoding="utf-8", errors="replace")

tests = list(files("tests", ["*.cs"]))
guides = [(p, t) for p, t in files("docs", ["*.md"], exclude={"API-CLASSIFICATION.md", "COMPETITIVE_ANALYSIS_AND_ROADMAP.md", "IMPLEMENTATION_STATUS.md", "USER_STORY_DELIVERY.md"}) if p.parent.name in ("docs", "migration")] + list(files("examples", ["*.cs", "*.md"]))

rows = []
for api in sorted((root / "src").rglob("PublicAPI.Unshipped.txt")):
    package = api.parent.name
    for line in api.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "->" in line or "(" in line:
            continue
        m = type_line.match(line)
        if not m:
            continue
        full = m.group(2)
        name = re.sub(r"<.*", "", full.split(".")[-1].split("`")[0])
        if not name or name[0].islower():
            continue
        word = re.compile(r"\b" + re.escape(name) + r"\b")
        test = next((p for p, t in tests if word.search(t)), None)
        guide = next((p for p, t in guides if word.search(t)), None)
        experimental = m.group(1)[1:-1] if m.group(1) else None
        rows.append((package, full, experimental, test, guide))

out = ["# Public API classification", "",
       "Every public type in the shipped packages, classified for 1.0. `ApiClassificationTests` checks this table",
       "against the declared public API, so a type added, removed or reclassified without updating it fails the build.",
       "",
       "- **Stable**: covered by semantic versioning from 1.0. It has XML documentation (the compiler enforces this in",
       "  every package), at least one test, and a guide or example that uses it. Evidence is linked.",
       "- **Experimental**: marked `[Experimental]` with the diagnostic ID shown, so consumers must opt in. It may change",
       "  or be removed in a minor release until the measurement named for its area exists.",
       "- **Internal**: not part of the public surface. No public type is currently classified internal.",
       "",
       "| Diagnostic | Area | Why it is experimental |",
       "| --- | --- | --- |",
       "| AIDEVO001 | Surrogate models | Screening savings are shown only under demonstration cost assumptions; no representative workload measures the effect on search quality. |",
       "| AIDEVO002 | Policy optimisation | Its development and held-out evidence comes from small mathematical landscapes that are not claimed to be representative. |",
       "| AIDEVO003 | Centroid archives | A caller-supplied Voronoi partition that has not been compared with the grid archive on search quality. |",
       "| AIDEVO004 | Experience memory | Its effect on proposals needs a dev-partition ablation with real model calls. |",
       ""]
for package in sorted({r[0] for r in rows}):
    out += [f"## {package}", "", "| Type | Class | Evidence |", "| --- | --- | --- |"]
    for _, full, experimental, test, guide in [r for r in rows if r[0] == package]:
        display = full.replace("<", "&lt;").replace(">", "&gt;")
        if experimental:
            out.append(f"| `{display}` | Experimental ({experimental}) | |")
        else:
            links = []
            if guide:
                links.append(f"[{guide.name}]({pathlib.Path(os.path.relpath(guide, root / "docs")).as_posix()})")
            if test:
                links.append(f"[{test.stem}]({pathlib.Path(os.path.relpath(test, root / "docs")).as_posix()})")
            out.append(f"| `{display}` | Stable | {', '.join(links)} |")
    out.append("")
(root / "docs" / "API-CLASSIFICATION.md").write_text("\n".join(out), encoding="utf-8")
missing = [(r[1], r[3] is None, r[4] is None) for r in rows if not r[2] and (r[3] is None or r[4] is None)]
print("rows", len(rows), "experimental", sum(1 for r in rows if r[2]), "stable missing evidence", len(missing))
for m in missing:
    print("  ", m)
