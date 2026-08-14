#!/usr/bin/env python3
"""Line-coverage gate for the CI test run.

Coverage was collected but never checked: the Cobertura reports went straight into an
artifact nobody opens. This turns them into a gate.

It is a *relative* gate, not an absolute target. An absolute number (say 80 %) is a
number someone made up, and it punishes honest work — a PR that adds a large,
well-tested subsystem can still move the total down. What actually matters is whether a
change makes the situation worse, so the baseline is checked in and the gate fires when
coverage drops more than the tolerance below it. Same shape as the repo's other gates:
the architecture baselines may only shrink, and the perf gate asserts a floor rather
than a machine-bound baseline.

Pion (webrtc) gates the same way — `threshold: 2%` on the project, no project-wide
target. SIPSorcery collects no coverage at all.

Two placement notes, both load-bearing: this lives under .github/ because .gitignore
excludes `scripts/*` wholesale (only one file there is checked in), and the baseline is
called `line-coverage-baseline.json` rather than `coverage-baseline.json` because the same
file ignores `coverage*.json`. Renaming either to something tidier removes it from the
repository without a word of warning.

Aggregation note: one test run produces many Cobertura reports (several test projects
times several target frameworks), and they overlap — the same source line is reported by
more than one of them. Summing their totals would count those lines repeatedly and
inflate the result, so lines are merged by (file, line number) first and a line counts as
covered when *any* report saw it hit.
"""

from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_BASELINE = REPO_ROOT / ".github" / "line-coverage-baseline.json"
DEFAULT_RESULTS = REPO_ROOT / "TestResults"


def parse_report(report: Path) -> ET.Element:
    """Parses one Cobertura report, refusing anything carrying a DTD.

    These files are produced by coverlet in the same run that reads them, so they are not
    hostile input. But stdlib ElementTree expands internal entities, so a report that ever
    came from somewhere else could carry a billion-laughs bomb — and refusing a construct
    Cobertura never emits costs nothing and needs no extra dependency.
    """
    text = report.read_text(encoding="utf-8")
    if "<!DOCTYPE" in text or "<!ENTITY" in text:
        raise ValueError(f"{report}: coverage reports must not declare a DTD or entities.")
    return ET.fromstring(text)


def normalise(source: str, filename: str) -> str | None:
    """Repo-relative path of one covered file, or None if it is not product code.

    Necessary because the reports do not agree on a base: a single run emits
    <source>…/voip/src/</source>, <source>…/voip/</source> and <source>/home/user/</source>,
    so the same file appears as "Core/Foo.cs", "src/Core/Foo.cs" and
    "Projekte/voip/src/Core/Foo.cs". Keying on the raw filename would treat those as three
    different files and inflate the total several-fold. Anchoring on the "src/" segment of
    the joined path collapses them and, as a side effect, drops everything outside src/ —
    which is exactly the code the gate should not be measuring.
    """
    parts = Path(source.replace("\\", "/")).joinpath(filename.replace("\\", "/")).parts
    if "src" not in parts:
        return None
    return "/".join(parts[len(parts) - 1 - parts[::-1].index("src"):])


def merge_reports(reports: list[Path]) -> dict[tuple[str, int], int]:
    """Union of every report's line hits, keyed by (repo-relative file, line)."""
    hits: dict[tuple[str, int], int] = {}
    for report in reports:
        root = parse_report(report)
        sources = [s.text or "" for s in root.iter("source")] or [""]
        for cls in root.iter("class"):
            filename = cls.get("filename")
            if not filename:
                continue
            path = next((p for p in (normalise(s, filename) for s in sources) if p), None)
            if path is None:
                continue
            for line in cls.iter("line"):
                number, count = line.get("number"), line.get("hits")
                if number is None or count is None:
                    continue
                key = (path, int(number))
                hits[key] = max(hits.get(key, 0), int(count))
    return hits


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-dir", type=Path, default=DEFAULT_RESULTS,
                        help="directory holding coverage.cobertura.xml files (default: TestResults)")
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--tolerance", type=float, default=None,
                        help="allowed drop in percentage points; default: the baseline's own value")
    parser.add_argument("--update", action="store_true",
                        help="write the measured value as the new baseline instead of checking it")
    args = parser.parse_args()

    reports = sorted(args.results_dir.rglob("coverage.cobertura.xml"))
    if not reports:
        print(f"ERROR: no coverage.cobertura.xml under {args.results_dir}.", file=sys.stderr)
        print("A silent pass here would hide the collection breaking, so this is a failure.", file=sys.stderr)
        return 1

    hits = merge_reports(reports)
    total = len(hits)
    if total == 0:
        print(f"ERROR: {len(reports)} report(s) parsed but they contain no lines.", file=sys.stderr)
        return 1

    covered = sum(1 for count in hits.values() if count > 0)
    rate = 100.0 * covered / total

    print(f"Coverage: {rate:.2f}% ({covered:,} of {total:,} lines, merged from {len(reports)} report(s))")

    if args.update:
        args.baseline.parent.mkdir(parents=True, exist_ok=True)
        previous = json.loads(args.baseline.read_text()) if args.baseline.exists() else {}
        args.baseline.write_text(json.dumps({
            "lineRatePercent": round(rate, 2),
            "tolerancePercentagePoints": previous.get("tolerancePercentagePoints", 2.0),
            "coveredLines": covered,
            "totalLines": total,
        }, indent=2) + "\n")
        print(f"Baseline written to {args.baseline.relative_to(REPO_ROOT)}.")
        return 0

    if not args.baseline.exists():
        print(f"ERROR: no baseline at {args.baseline}. Create it with --update.", file=sys.stderr)
        return 1

    baseline = json.loads(args.baseline.read_text())
    expected = float(baseline["lineRatePercent"])
    tolerance = args.tolerance if args.tolerance is not None else float(
        baseline.get("tolerancePercentagePoints", 2.0))
    floor = expected - tolerance

    if rate < floor:
        print(
            f"\nERROR: coverage {rate:.2f}% is below the floor {floor:.2f}% "
            f"(baseline {expected:.2f}% minus {tolerance:.2f} points).\n"
            "Add tests for the new code, or — if the drop is intended and understood — "
            "update the baseline in the same commit and say why in the PR.",
            file=sys.stderr)
        return 1

    direction = "above" if rate >= expected else "below"
    print(f"OK — {abs(rate - expected):.2f} points {direction} the {expected:.2f}% baseline "
          f"(floor {floor:.2f}%).")

    # Ratcheting is manual on purpose: CI must not rewrite a checked-in baseline, and a
    # genuine improvement deserves a visible line in the diff.
    if rate >= expected + tolerance:
        print(f"NOTE: coverage improved by {rate - expected:.2f} points. "
              f"Consider running with --update to lock it in.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
