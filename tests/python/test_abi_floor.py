"""Every Jellyfin reference stays at the ABI floor, and a check that finds nothing refuses to pass."""

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

from tools.abi_floor import FloorError, floor, main, mismatches, references

ROOT = Path(__file__).resolve().parents[2]
PROJECT = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{}</ItemGroup></Project>'
REFERENCE = '<PackageReference Include="{}" Version="{}" PrivateAssets="all" />'


def planted(root, compiled="12.0.0", runtime="12.0.0", target="12.0.0.0", package="Jellyfin.Controller"):
    """Write the smallest tree the check reads: a descriptor, one project and the runtime list."""
    root = Path(root)
    (root / "src").mkdir()
    (root / "obj").mkdir()
    (root / "plugin.json").write_text(json.dumps({"targetAbi": target}), encoding="utf-8")
    other = REFERENCE.format("Moq", "4.20.72")
    (root / "src" / "Plugin.csproj").write_text(PROJECT.format(REFERENCE.format(package, compiled) + other), encoding="utf-8")
    (root / "obj" / "Restored.csproj").write_text(PROJECT.format(REFERENCE.format("Jellyfin.Controller", "9.9.9")), encoding="utf-8")
    listed = {"runtimeDependencies": [{"id": "Jellyfin.Controller", "version": runtime, "providedBy": "jellyfin"}]}
    (root / "runtime-dependencies.json").write_text(json.dumps(listed), encoding="utf-8")
    return root


class FloorTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = scratch.name

    def test_repository_references_equal_its_floor(self):
        self.assertEqual("12.0.0", floor(ROOT))
        self.assertEqual([], mismatches(ROOT))
        self.assertEqual(5, len(references(ROOT)))

    def test_matching_tree_passes_and_ignores_build_output(self):
        self.assertEqual([], mismatches(planted(self.root)))

    def test_newer_compile_reference_is_reported(self):
        found = mismatches(planted(self.root, compiled="12.2.0"))
        self.assertEqual(["src/Plugin.csproj: Jellyfin.Controller 12.2.0, the floor is 12.0.0"], found)

    def test_newer_runtime_entry_is_reported(self):
        found = mismatches(planted(self.root, runtime="12.2.0"))
        self.assertEqual(["runtime-dependencies.json: Jellyfin.Controller 12.2.0, the floor is 12.0.0"], found)

    def test_raising_the_floor_alone_is_reported_twice(self):
        self.assertEqual(2, len(mismatches(planted(self.root, target="12.2.0.0"))))

    def test_refuses_a_tree_it_cannot_compare(self):
        refused = {
            "no Jellyfin reference in any project": {"package": "MediaBrowser.Controller"},
            "descriptor without a target": {"target": None},
            "target with a revision": {"target": "12.0.0.1"},
            "target without a revision": {"target": "12.0.0"},
        }
        for label, changes in refused.items():
            with self.subTest(label), tempfile.TemporaryDirectory() as root, self.assertRaises(FloorError):
                mismatches(planted(root, **changes))

    def test_refuses_an_unreadable_project(self):
        root = planted(self.root)
        (root / "src" / "Plugin.csproj").write_text("<Project>", encoding="utf-8")
        with self.assertRaises(FloorError):
            mismatches(root)

    def test_exit_status_separates_findings_from_errors(self):
        statuses = {}
        for label, changes in {"clean": {}, "finding": {"compiled": "12.2.0"}, "error": {"target": None}}.items():
            with tempfile.TemporaryDirectory() as root, contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                statuses[label] = main(["--directory", str(planted(root, **changes))])
        self.assertEqual({"clean": 0, "finding": 1, "error": 2}, statuses)


if __name__ == "__main__":
    unittest.main()
