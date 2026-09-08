"""Regression tests for the published Jellyfin catalog and immutable assets."""

import copy
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import github_release
import release


def entry(version="12.0.0.0", abi="12.0.0.0"):
    tag = "v" + version.removesuffix(".0")
    return {
        "version": version,
        "changelog": "Fix playback handling.",
        "targetAbi": abi,
        "sourceUrl": f"https://github.com/{release.REPOSITORY}/releases/download/{tag}/lastfm_{version}.zip",
        "checksum": release.md5(release.make_zip(b"MZtest assembly")),
        "timestamp": "2026-09-08T01:38:39Z",
    }


def catalog(entries=None):
    return [{"guid": release.GUID, "name": release.NAME, "imageUrl": "https://example.com/image.png", "versions": entries or []}]


class CatalogTests(unittest.TestCase):
    def test_actual_historical_catalog_is_compatible(self):
        release.validate_catalog(release.read_json(release.ROOT / "manifest.json"))

    def test_release_lines_merge_numerically_and_preserve_history(self):
        legacy = entry("10.11.9.0", "10.11.9.0")
        original = catalog([legacy])
        snapshot = copy.deepcopy(original)
        merged = release.merge_catalog(original, [entry("10.11.11.0", "10.11.11.0"), entry()])
        self.assertEqual(["12.0.0.0", "10.11.11.0", "10.11.9.0"], [v["version"] for v in merged[0]["versions"]])
        self.assertEqual(legacy, merged[0]["versions"][-1])
        self.assertEqual(original[0]["imageUrl"], merged[0]["imageUrl"])
        self.assertEqual(snapshot, original)

    def test_equal_retry_is_idempotent(self):
        original = catalog([entry()])
        self.assertEqual(original, release.merge_catalog(original, [entry()]))

    def test_conflicting_republication_fails(self):
        altered = entry()
        altered["checksum"] = "a" * 32
        with self.assertRaisesRegex(ValueError, "conflicting"):
            release.merge_catalog(catalog([entry()]), [altered])

    def test_wrong_identity_and_duplicate_versions_fail(self):
        wrong = catalog()
        wrong[0]["guid"] = "00000000-0000-0000-0000-000000000000"
        for invalid in (wrong, catalog([entry(), entry()])):
            with self.assertRaises(ValueError):
                release.validate_catalog(invalid)

    def test_legacy_server_does_not_select_v12(self):
        versions = release.merge_catalog(catalog(), [entry(), entry("10.11.11.0", "10.11.11.0")])[0]["versions"]
        # Jellyfin compares numeric targetAbi to the running server version.
        compatible = [item for item in versions if release.version_tuple(item["targetAbi"]) <= (10, 11, 11, 0)]
        self.assertEqual(["10.11.11.0"], [item["version"] for item in compatible])

    def test_invalid_versions_and_timestamps_are_rejected(self):
        for key, value in (("version", "dev-deadbeef"), ("version", "v12.0.0.0"), ("targetAbi", "12.0"), ("checksum", "broken"), ("timestamp", "2026-09-08T01:38:39+00:00Z"), ("timestamp", "2026-09-08T01:38:39")):
            with self.subTest(key=key, value=value):
                invalid = entry()
                invalid[key] = value
                with self.assertRaises(ValueError):
                    release.validate_entry(invalid)


class PackageTests(unittest.TestCase):
    def test_sha256_covers_archive_and_metadata_and_detects_tampering(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            names = ["manifest-entry.json", "lastfm_12.0.0.0.zip"]
            (directory / names[0]).write_text("{}", encoding="utf-8")
            (directory / names[1]).write_bytes(release.make_zip(b"MZtest assembly"))
            text = release.checksum_lines(directory, names)
            self.assertEqual(sorted(names), [line.split("  ")[1] for line in text.splitlines()])
            (directory / "SHA256SUMS").write_text(text, encoding="utf-8")
            release.validate_checksums(directory, "12.0.0.0")
            (directory / names[0]).write_text('{"changed": true}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "SHA256SUMS"):
                release.validate_checksums(directory, "12.0.0.0")

    def test_archive_is_reproducible_flat_and_contains_only_plugin(self):
        first = release.make_zip(b"MZtest assembly")
        self.assertEqual(first, release.make_zip(b"MZtest assembly"))
        with zipfile.ZipFile(io.BytesIO(first)) as archive:
            self.assertEqual([release.ARTIFACT], archive.namelist())
            self.assertEqual((1980, 1, 1, 0, 0, 0), archive.infolist()[0].date_time)
            self.assertEqual(b"MZtest assembly", archive.read(release.ARTIFACT))

    def test_host_dll_nested_artifact_or_nonassembly_fails(self):
        for name, data in (("Jellyfin.Controller.dll", b"MZhost"), ("bin/" + release.ARTIFACT, b"MZplugin"), (release.ARTIFACT, b"not an assembly")):
            buffer = io.BytesIO()
            with zipfile.ZipFile(buffer, "w") as archive:
                archive.writestr(name, data)
            with self.assertRaises(ValueError):
                release.validate_zip(buffer.getvalue())

    def test_published_metadata_is_bound_to_tag_and_zip_bytes(self):
        metadata = {"guid": release.GUID, "name": release.NAME, "entry": entry()}
        self.assertEqual(entry(), release.validate_release_metadata(metadata, "v12.0.0", release.make_zip(b"MZtest assembly")))
        with self.assertRaisesRegex(ValueError, "checksum"):
            release.validate_release_metadata(metadata, "v12.0.0", release.make_zip(b"MZchanged"))
        with self.assertRaisesRegex(ValueError, "tag/asset"):
            release.validate_release_metadata(metadata, "v12.0.1", release.make_zip(b"MZtest assembly"))

    def test_version_and_abi_are_independent_but_consistent(self):
        props = {"Version": "10.11.12", "AssemblyVersion": "10.11.12.0", "FileVersion": "10.11.12.0", "JellyfinVersion": "10.11.11", "TargetAbi": "10.11.11.0", "TargetFramework": "net9.0"}
        release.validate_properties(props, "10.11.12", "v10.11.12")
        for name, value in (("Version", "10.11.11"), ("AssemblyVersion", "10.11.12"), ("TargetAbi", "12.0.0.0"), ("TargetFramework", "net10.0")):
            with self.subTest(name=name):
                changed = {**props, name: value}
                with self.assertRaises(ValueError):
                    release.validate_properties(changed, "10.11.12", "v10.11.12")
        unsupported = {"Version": "13.0.0", "AssemblyVersion": "13.0.0.0", "FileVersion": "13.0.0.0", "JellyfinVersion": "13.0.0", "TargetAbi": "13.0.0.0", "TargetFramework": "net10.0"}
        with self.assertRaisesRegex(ValueError, "Unsupported Jellyfin"):
            release.validate_properties(unsupported, "13.0.0")

    def test_tags_reject_shell_input_prereleases_and_old_four_part_tags(self):
        for tag in ("12.0.0", "v12.0.0.0", "v12.0.0-beta", "v12.0.0;echo secret", "v01.2.3", "v65535.0.0"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.validate_tag(tag)


class GitHubTests(unittest.TestCase):
    def test_publication_lookup_includes_draft_releases(self):
        draft = {"tag_name": "v12.0.0", "draft": True}
        with patch.object(github_release, "pages", return_value=[draft]):
            self.assertEqual(draft, github_release.find_release("v12.0.0"))
        with patch.object(github_release, "pages", return_value=[draft, draft]), self.assertRaises(ValueError):
            github_release.find_release("v12.0.0")

    def test_recovery_provenance_distinguishes_release_source_from_workflow_revision(self):
        environment = {
            "GITHUB_REPOSITORY": release.REPOSITORY,
            "GITHUB_WORKFLOW_REF": release.REPOSITORY + "/.github/workflows/release.yml@refs/heads/main",
            "GITHUB_WORKFLOW_SHA": "b" * 40,
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "2",
        }
        with patch.dict(github_release.os.environ, environment):
            predicate = github_release.provenance("v12.0.0", "a" * 40)
        dependencies = predicate["buildDefinition"]["resolvedDependencies"]
        self.assertEqual("a" * 40, dependencies[0]["digest"]["gitCommit"])
        self.assertEqual("b" * 40, dependencies[1]["digest"]["gitCommit"])
        self.assertIn("refs/tags/v12.0.0", dependencies[0]["uri"])

    def test_catalog_wait_requires_success_on_unchanged_head(self):
        pull = {"state": "open", "head": {"sha": "abc"}}
        for conclusion in ("failure", "cancelled", "skipped"):
            checks = {"check_runs": [{"name": "CI", "app": {"slug": "github-actions"}, "id": 1, "status": "completed", "conclusion": conclusion}]}
            with patch.object(github_release, "repo_api", side_effect=[pull, checks]), self.assertRaisesRegex(ValueError, "did not pass"):
                github_release.wait_for_ci(42, "abc", timeout=1)
        checks["check_runs"][0]["conclusion"] = "success"
        with patch.object(github_release, "repo_api", side_effect=[pull, checks]):
            github_release.wait_for_ci(42, "abc", timeout=1)
        with patch.object(github_release, "repo_api", return_value={"state": "open", "head": {"sha": "changed"}}), self.assertRaisesRegex(ValueError, "changed"):
            github_release.wait_for_ci(42, "abc", timeout=1)

    def test_pr_dispatch_uses_actual_head_branch_and_sha(self):
        pull = {"state": "open", "head": {"repo": {"full_name": release.REPOSITORY}, "ref": "release-please--branches--main", "sha": "abc"}}
        with patch.object(github_release, "repo_api", side_effect=[pull, None]) as api:
            self.assertEqual("abc", github_release.dispatch_ci(42))
        self.assertEqual({"ref": pull["head"]["ref"], "inputs": {"expected_sha": "abc"}}, api.call_args.kwargs["data"])

    def test_external_or_nonautomation_pr_cannot_be_dispatched(self):
        for repository, branch in (("attacker/fork", "release-please--main"), (release.REPOSITORY, "main")):
            pull = {"state": "open", "head": {"repo": {"full_name": repository}, "ref": branch, "sha": "abc"}}
            with patch.object(github_release, "repo_api", return_value=pull) as api, self.assertRaises(ValueError):
                github_release.dispatch_ci(42)
            self.assertEqual(1, api.call_count)

    def test_publish_never_overwrites_changed_bytes(self):
        archive = release.make_zip(b"MZtest assembly")
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "lastfm_12.0.0.0.zip").write_bytes(archive)
            release.write_json(directory / "manifest-entry.json", {"guid": release.GUID, "name": release.NAME, "entry": entry()})
            (directory / "SHA256SUMS").write_text(release.checksum_lines(directory, ["lastfm_12.0.0.0.zip", "manifest-entry.json"]), encoding="utf-8")
            existing = {"prerelease": False, "draft": False, "assets": [{"name": "lastfm_12.0.0.0.zip", "id": 1}]}
            with patch.object(github_release, "verify_tag"), patch.object(github_release, "find_release", return_value=existing), patch.object(github_release, "asset_bytes", return_value=b"different"), patch.object(subprocess, "run") as mutation:
                with self.assertRaisesRegex(ValueError, "Refusing to replace"):
                    github_release.publish("v12.0.0", directory, "abc")
                mutation.assert_not_called()

    def test_all_releases_are_reconciled_including_previous_pending_version(self):
        metadata = {"guid": release.GUID, "name": release.NAME, "entry": entry()}
        published = {"draft": False, "prerelease": False, "tag_name": "v12.0.0", "assets": [{"id": 1, "name": "manifest-entry.json"}, {"id": 2, "name": "lastfm_12.0.0.0.zip"}, {"id": 3, "name": "SHA256SUMS"}]}
        metadata_bytes = json.dumps(metadata).encode()
        archive = release.make_zip(b"MZtest assembly")
        checksums = release.checksums_for_assets({"manifest-entry.json": metadata_bytes, "lastfm_12.0.0.0.zip": archive}).encode()
        with patch.object(github_release, "pages", return_value=[published]), patch.object(github_release, "asset_bytes", side_effect=[metadata_bytes, archive, checksums]):
            self.assertEqual([entry()], github_release.release_entries())

    def test_published_release_without_metadata_fails_closed(self):
        published = {"draft": False, "prerelease": False, "tag_name": "v12.0.0", "assets": []}
        with patch.object(github_release, "pages", return_value=[published]), self.assertRaisesRegex(ValueError, "lacks its catalog metadata"):
            github_release.release_entries()

    def test_drafts_and_historical_tags_are_not_new_catalog_candidates(self):
        values = [{"draft": True, "prerelease": False, "tag_name": "v12.0.0"}, {"draft": False, "prerelease": False, "tag_name": "10.11.10.0"}]
        with patch.object(github_release, "pages", return_value=values):
            self.assertEqual([], github_release.release_entries())


class SecurityReportTests(unittest.TestCase):
    def test_codeql_extension_rules_are_checked(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            report = {"runs": [{"tool": {"driver": {"rules": []}, "extensions": [{"rules": [{"id": "cs/extension", "properties": {"security-severity": "9.0"}}]}]}, "results": [{"ruleId": "cs/extension"}]}]}
            release.write_json(directory / "csharp.sarif", report)
            with self.assertRaisesRegex(ValueError, "cs/extension"):
                release.audit_sarif(directory)

    def test_missing_or_incomplete_vulnerability_reports_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "audit.json"
            release.write_json(report, {"problems": [{"message": "Source unavailable"}]})
            with self.assertRaises(ValueError):
                release.audit(report)

    def test_transitive_vulnerability_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "audit.json"
            release.write_json(report, {"version": 1, "sources": ["https://api.nuget.org/v3/index.json"], "projects": [{"frameworks": [{"transitivePackages": [{"id": "Vulnerable.Dependency", "vulnerabilities": [{"severity": "High", "advisoryurl": "https://example.com/advisory"}]}]}]}]})
            with self.assertRaisesRegex(ValueError, "Vulnerable.Dependency"):
                release.audit(report)

    def test_codeql_high_findings_fail_and_empty_reports_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            with self.assertRaisesRegex(ValueError, "no SARIF"):
                release.audit_sarif(directory)
            report = {"runs": [{"tool": {"driver": {"rules": [{"id": "cs/security", "properties": {"security-severity": "8.1"}}]}}, "results": [{"ruleId": "cs/security"}]}]}
            release.write_json(directory / "csharp.sarif", report)
            with self.assertRaisesRegex(ValueError, "cs/security"):
                release.audit_sarif(directory)
            report["runs"][0]["results"] = []
            release.write_json(directory / "csharp.sarif", report)
            release.audit_sarif(directory)


if __name__ == "__main__":
    unittest.main()
