"""A canary is main only, carries the application, stays below every release and is never published."""

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import console
from tools.canary import WORKFLOW, CanaryError, main, package, plan, require_credentials, workflow_violations

ROOT = Path(__file__).resolve().parents[2]
COMMIT = "0123456789abcdef0123456789abcdef01234567"
NAME = "plugin-lastfm-canary-0.0.7-0123456"
VERSION = "0.0.7-canary"
ASSEMBLY = b"MZ" + VERSION.encode() + b"+" + COMMIT.encode() + b" JellySinLastfmApiKey JellySinLastfmSharedSecret"
KEYS = {"LASTFM_API_KEY": "a" * 32, "LASTFM_SHARED_SECRET": "B" * 32}


def run(arguments):
    return console.run(main, arguments)


class PlanTests(unittest.TestCase):
    def test_main_gets_names_below_every_release(self):
        expected = {"version": VERSION, "assembly-version": "0.0.7.0", "name": NAME}
        self.assertEqual(expected, plan("refs/heads/main", COMMIT, "7"))
        code, output, _ = run(["plan", "--ref", "refs/heads/main", "--commit", COMMIT, "--run", "7"])
        self.assertEqual((0, f"version={VERSION}\nassembly-version=0.0.7.0\nname={NAME}\n"), (code, output))

    def test_every_other_ref_is_refused(self):
        for ref in ("refs/heads/feature", "refs/tags/v1.0.0", "refs/pull/7/merge", "refs/heads/main2", "main", ""):
            with self.subTest(ref=ref), self.assertRaisesRegex(CanaryError, "refs/heads/main only"):
                plan(ref, COMMIT, "7")
        code, output, errors = run(["plan", "--ref", "refs/tags/v1.0.0", "--commit", COMMIT, "--run", "7"])
        self.assertEqual((2, ""), (code, output))
        self.assertIn("Canary refused", errors)

    def test_unusable_commit_or_run_number_is_refused(self):
        for commit, number in ((COMMIT[:7], "7"), (COMMIT.upper(), "7"), (COMMIT, "0"), (COMMIT, "07"), (COMMIT, "65535"), (COMMIT, "1.0")):
            with self.subTest(commit=commit, number=number), self.assertRaises(CanaryError):
                plan("refs/heads/main", commit, number)
        self.assertEqual("0.0.65534.0", plan("refs/heads/main", COMMIT, "65534")["assembly-version"])


class CredentialTests(unittest.TestCase):
    def test_well_formed_application_passes(self):
        require_credentials(KEYS)

    def test_missing_or_malformed_application_is_refused_without_its_value(self):
        for changed in ({"LASTFM_API_KEY": ""}, {"LASTFM_SHARED_SECRET": "c" * 31}, {"LASTFM_API_KEY": "g" * 32}):
            with self.subTest(changed=changed), self.assertRaises(CanaryError) as refused:
                require_credentials({**KEYS, **changed})
            self.assertNotIn("ccc", str(refused.exception))
        with self.assertRaises(CanaryError):
            require_credentials({})


class PackageTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name)
        self.built = self.root / "bin"
        self.built.mkdir()
        (self.root / "plugin.json").write_text(json.dumps({"assemblyFile": "Plugin.dll", "packageFiles": ["LICENSE"]}), encoding="utf-8")
        (self.built / "Plugin.dll").write_bytes(ASSEMBLY)
        (self.built / "Plugin.pdb").write_bytes(b"symbols")
        (self.built / "LICENSE").write_bytes(b"licence")

    def packaged(self, name=NAME, version=VERSION):
        return package(self.root, self.built, name, version, self.root / "dist")

    def test_archive_holds_the_assembly_and_the_listed_files_only(self):
        sums = dict(self.packaged())
        archive = self.root / "dist" / f"{NAME}.zip"
        with zipfile.ZipFile(archive) as stored:
            self.assertEqual(["Plugin.dll", "LICENSE"], stored.namelist())
            self.assertEqual(ASSEMBLY, stored.read("Plugin.dll"))
            self.assertEqual({(1980, 1, 1, 0, 0, 0)}, {entry.date_time for entry in stored.infolist()})
        self.assertEqual({f"{NAME}.zip": hashlib.sha256(archive.read_bytes()).hexdigest(), "Plugin.dll": hashlib.sha256(ASSEMBLY).hexdigest()}, sums)
        listed = (self.root / "dist" / "checksums.txt").read_text(encoding="utf-8").splitlines()
        self.assertEqual([f"{digest}  {file}" for file, digest in sums.items()], listed)

    def test_build_without_the_application_is_refused(self):
        (self.built / "Plugin.dll").write_bytes(ASSEMBLY.replace(b"JellySinLastfmSharedSecret", b""))
        with self.assertRaisesRegex(CanaryError, "no Last.fm application"):
            self.packaged()
        self.assertFalse((self.root / "dist").exists())

    def test_build_under_a_release_version_is_refused(self):
        (self.built / "Plugin.dll").write_bytes(ASSEMBLY.replace(VERSION.encode(), b"1.0.0"))
        with self.assertRaisesRegex(CanaryError, "does not carry the version"):
            self.packaged()

    def test_release_like_name_or_version_is_refused(self):
        for name, version in (("plugin-lastfm-1.0.0", VERSION), (NAME, "1.0.0"), (NAME, "1.0.7-canary"), ("../" + NAME, VERSION)):
            with self.subTest(name=name, version=version), self.assertRaisesRegex(CanaryError, "not a canary name"):
                self.packaged(name, version)

    def test_file_outside_the_build_output_and_a_second_run_are_refused(self):
        self.packaged()
        with self.assertRaises(FileExistsError):
            self.packaged()
        (self.root / "plugin.json").write_text(json.dumps({"assemblyFile": "Plugin.dll", "packageFiles": ["../plugin.json"]}), encoding="utf-8")
        with self.assertRaisesRegex(CanaryError, "outside the build output"):
            package(self.root, self.built, NAME, VERSION, self.root / "other")


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = (ROOT / WORKFLOW).read_text(encoding="utf-8")

    def changed(self, old, new):
        self.assertIn(old, self.text)
        return workflow_violations(self.text.replace(old, new))

    def test_repository_workflow_keeps_every_limit(self):
        self.assertEqual([], workflow_violations(self.text))

    def test_another_trigger_is_reported(self):
        expected = ["the only trigger is a manual dispatch without inputs"]
        self.assertEqual(expected, self.changed("on:\n  workflow_dispatch:\n", "on:\n  workflow_dispatch:\n  push:\n    branches: [main]\n"))
        self.assertEqual(expected, self.changed("on:\n  workflow_dispatch:\n", "on:\n  workflow_dispatch:\n    inputs:\n      ref:\n        type: string\n"))
        self.assertEqual(expected, self.changed("on:\n  workflow_dispatch:\n", "on:\n  schedule:\n    - cron: '0 3 * * *'\n"))

    def test_write_permission_is_reported(self):
        expected = ["the token reads contents and writes nothing"]
        self.assertEqual(expected, self.changed("permissions:\n  contents: read\n", "permissions:\n  contents: write\n"))
        self.assertEqual(expected, self.changed("    timeout-minutes: 20\n", "    timeout-minutes: 20\n    permissions:\n      id-token: write\n"))
        self.assertEqual(expected, self.changed("permissions:\n  contents: read\n", "permissions:\n  contents: read\n  actions: read\n"))

    def test_longer_or_default_retention_is_reported(self):
        expected = ["the artefact is kept for one day"]
        self.assertEqual(expected, self.changed("retention-days: 1\n", "retention-days: 7\n"))
        self.assertEqual(expected, self.changed("          retention-days: 1\n", ""))

    def test_publication_and_tracing_are_reported(self):
        self.assertEqual(["the workflow does not name 'git tag'"], self.changed("npm run build\n", "npm run build\n          git tag v1.0.0\n"))
        self.assertEqual(["the workflow does not name 'set -x'"], self.changed("npm run build\n", "set -x\n          npm run build\n"))
        self.assertEqual(["the workflow does not name 'actions/attest'"], self.changed("actions/upload-artifact@", "actions/attest@"))

    def test_application_in_a_script_or_another_secret_is_reported(self):
        expected = ["the application reaches a step only as its two environment values"]
        self.assertEqual(expected, self.changed("npm run build\n", "npm run build\n          echo \"$LASTFM_API_KEY\"\n"))
        self.assertEqual(expected, self.changed("CANARY_NAME: ${{ steps.plan.outputs.name }}", "CANARY_NAME: ${{ secrets.DEPLOY_KEY }}"))
        self.assertEqual(expected, self.changed("LASTFM_API_KEY: ${{ secrets.LASTFM_API_KEY }}", "LASTFM_API_KEY: ${{ secrets.LASTFM_SHARED_SECRET }}"))

    def test_missing_or_late_ref_check_is_reported(self):
        expected = ["the ref is checked before a step receives the application"]
        self.assertEqual(expected, self.changed("python tools/canary.py plan --ref \"$GITHUB_REF\"", "python tools/canary.py plan --ref refs/heads/main"))
        credentials = self.text[self.text.index("      - name: Require the project-owned"):self.text.index("      - name: Build the plugin")]
        guard = self.text[self.text.index("      # Any other ref fails here"):self.text.index("      - uses: actions/setup-node")]
        self.assertEqual(expected, workflow_violations(self.text.replace(credentials, "").replace(guard, credentials + guard)))


if __name__ == "__main__":
    unittest.main()
