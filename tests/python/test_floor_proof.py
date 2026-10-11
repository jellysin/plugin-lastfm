"""The proof of the release helper's floor rule fails on a helper that does anything but refuse for the floor's sake."""

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

from tools.floor_proof import REFERENCE, REFUSAL, SOURCES, ProofError, cases, main, moved, referenced

FRAMEWORK = "net10.0"
# A stand-in for the helper's tools.py. "honest" packages the floor only and names the floor when it refuses.
STUB = '''
import json, sys
from pathlib import Path
options = dict(zip(sys.argv[2::2], sys.argv[3::2]))
mode = (Path(__file__).parent / "mode.txt").read_text()
runtime = json.loads(Path(options["--runtime-dependencies-path"]).read_text())
lock = json.loads(Path(options["--nuget-lock-path"]).read_text())
assets = json.loads(Path(options["--nuget-assets-path"]).read_text())
version = runtime["runtimeDependencies"][0]["version"]
key = "Jellyfin.Controller/" + version
agree = lock["dependencies"]["net10.0"]["Jellyfin.Controller"]["resolved"] == version and key in assets["targets"]["net10.0"] and key in assets["libraries"]
def write():
    out = Path(options["--output-directory"])
    out.mkdir(parents=True)
    for name in ("plugin.zip", "release.json", "sbom.spdx.json", "checksums.txt"):
        (out / name).write_text("x")
def refuse(words):
    print(words, file=sys.stderr)
    sys.exit(1)
if not agree:
    refuse("Runtime dependency version differs from lock")
at_floor = version == "12.0.0"
if mode == "accepts everything" or (at_floor and mode != "refuses the floor too"):
    write()
elif mode == "honest" or mode == "refuses the floor too":
    refuse(REFUSAL)
elif mode == "refuses for another reason":
    refuse("Invalid NuGet version")
elif mode == "writes before it refuses":
    write()
    refuse(REFUSAL)
elif mode == "names the floor and succeeds":
    print(REFUSAL, file=sys.stderr)
'''.replace("REFUSAL", repr(REFUSAL))


def documents(version="12.0.0"):
    key = f"{REFERENCE}/{version}"
    return {
        "nuget-lock-path": {"version": 1, "dependencies": {FRAMEWORK: {REFERENCE: {"type": "Direct", "requested": f"[{version}, )", "resolved": version}}}},
        "nuget-assets-path": {"version": 4, "targets": {FRAMEWORK: {key: {"type": "package"}}}, "libraries": {key: {"type": "package"}}},
        "runtime-dependencies-path": {"schemaVersion": 1, "targetFramework": FRAMEWORK,
                                      "runtimeDependencies": [{"id": REFERENCE, "version": version, "providedBy": "jellyfin"}]},
    }


class FloorProofTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name) / "repository"
        self.tooling = Path(scratch.name) / "tooling"
        self.tooling.mkdir()
        (self.tooling / "tools.py").write_text(STUB, encoding="utf-8")
        for option, document in documents().items():
            (self.root / SOURCES[option]).parent.mkdir(parents=True, exist_ok=True)
            (self.root / SOURCES[option]).write_text(json.dumps(document), encoding="utf-8")

    def proof(self, mode):
        (self.tooling / "mode.txt").write_text(mode, encoding="utf-8")
        said, complained = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(said), contextlib.redirect_stderr(complained):
            status = main(["--tooling-directory", str(self.tooling), "--directory", str(self.root)])
        return status, said.getvalue(), complained.getvalue()

    def test_a_helper_that_holds_the_floor_passes(self):
        status, said, _ = self.proof("honest")
        self.assertEqual(0, status, said)
        self.assertEqual((3, 0), (said.count(": as required"), said.count("NOT as required")))

    def test_a_helper_that_does_not_refuse_for_the_floor_fails(self):
        both = "above the floor, below the floor"
        expected = {
            "accepts everything": both,
            "refuses for another reason": both,
            "writes before it refuses": both,
            "names the floor and succeeds": both,
            "refuses the floor too": "at the floor",
        }
        for mode, named in expected.items():
            with self.subTest(mode):
                status, _, complained = self.proof(mode)
                self.assertEqual(1, status)
                self.assertTrue(complained.strip().endswith(": " + named), complained)

    def test_a_proof_that_cannot_run_is_an_error_not_a_pass(self):
        (self.root / SOURCES["nuget-assets-path"]).unlink()
        self.assertEqual(2, self.proof("honest")[0])
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, main(["--tooling-directory", str(self.tooling / "absent")]))

    def test_planted_references_move_all_three_documents_and_leave_the_originals(self):
        original = documents()
        changed = moved(original, "12.1.0")
        self.assertEqual(documents("12.1.0"), changed)
        self.assertEqual(documents(), original)
        self.assertEqual({"at the floor": "12.0.0", "above the floor": "12.1.0", "below the floor": "11.0.0"}, cases(original))

    def test_refuses_a_runtime_list_without_one_plain_release_of_the_reference(self):
        refused = {
            "absent": [],
            "twice": [{"id": REFERENCE, "version": "12.0.0"}] * 2,
            "not a release": [{"id": REFERENCE, "version": "12.0"}],
        }
        for label, entries in refused.items():
            with self.subTest(label), self.assertRaises(ProofError):
                referenced({"runtime-dependencies-path": {"runtimeDependencies": entries}})


if __name__ == "__main__":
    unittest.main()
