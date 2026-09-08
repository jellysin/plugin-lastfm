"""GitHub release operations; authentication comes only from GH_TOKEN."""

import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import time

import release

CATALOG_BRANCH = "automation/plugin-catalog"


def api(endpoint, method="GET", data=None, binary=False):
    command = ["gh", "api", endpoint, "--method", method]
    if binary:
        command += ["-H", "Accept: application/octet-stream"]
    if data is not None:
        command += ["--input", "-"]
    output = subprocess.check_output(command, input=json.dumps(data).encode() if data is not None else None)
    return output if binary else json.loads(output) if output else None


def repo_api(path, **kwargs):
    return api(f"repos/{release.REPOSITORY}/{path}", **kwargs)


def pages(path):
    for page in range(1, 1001):
        separator = "&" if "?" in path else "?"
        values = repo_api(f"{path}{separator}per_page=100&page={page}")
        yield from values
        if len(values) < 100:
            return
    raise ValueError("GitHub pagination limit exceeded")


def asset_bytes(asset):
    return repo_api(f"releases/assets/{asset['id']}", binary=True)


def find_release(tag):
    # The tag lookup endpoint is documented for published releases. Authenticated
    # listing includes drafts for push-capable tokens, which publication needs.
    matches = [item for item in pages("releases") if item["tag_name"] == tag]
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one visible release for {tag}")
    return matches[0]


def release_entries():
    entries = []
    for published in pages("releases"):
        if published["draft"] or published["prerelease"]:
            continue
        tag = published["tag_name"]
        try:
            release.validate_tag(tag)
        except ValueError:
            # Historical four-part tags remain exclusively in the existing catalog.
            continue
        assets = {item["name"]: item for item in published["assets"]}
        metadata_asset = assets.get("manifest-entry.json")
        if metadata_asset is None:
            raise ValueError(f"Published release {tag} lacks its catalog metadata")
        metadata_bytes = asset_bytes(metadata_asset)
        metadata = json.loads(metadata_bytes)
        name = f"lastfm_{tag[1:]}.0.zip"
        if name not in assets:
            raise ValueError(f"Published release {tag} lacks {name}")
        archive = asset_bytes(assets[name])
        if "SHA256SUMS" not in assets or asset_bytes(assets["SHA256SUMS"]).decode("utf-8") != release.checksums_for_assets({"manifest-entry.json": metadata_bytes, name: archive}):
            raise ValueError(f"Published release {tag} has missing or inconsistent SHA256SUMS")
        entries.append(release.validate_release_metadata(metadata, tag, archive))
    return entries


def dispatch_ci(number):
    pull = repo_api(f"pulls/{number}")
    if pull["head"]["repo"]["full_name"] != release.REPOSITORY or pull["state"] != "open":
        raise ValueError("Only open, same-repository bot PRs can be dispatched")
    branch = pull["head"]["ref"]
    if branch != CATALOG_BRANCH and not branch.startswith("release-please--"):
        raise ValueError("CI dispatch is limited to release/catalog automation branches")
    repo_api("actions/workflows/ci.yml/dispatches", method="POST", data={
        "ref": branch,
        "inputs": {"expected_sha": pull["head"]["sha"]},
    })
    return pull["head"]["sha"]


def dispatch_release_prs(value):
    for pull in json.loads(value or "[]"):
        dispatch_ci(pull["number"])


def wait_for_ci(number, sha, timeout=2100, interval=15):
    """Do not rely on auto-merge waiting when branch protection is misconfigured."""
    deadline = time.monotonic() + timeout
    print(f"Waiting for the required CI check on catalog PR #{number}.", flush=True)
    while time.monotonic() < deadline:
        pull = repo_api(f"pulls/{number}")
        if pull["state"] != "open" or pull["head"]["sha"] != sha:
            raise ValueError("Catalog PR changed while awaiting validation")
        checks = repo_api(f"commits/{sha}/check-runs?filter=latest&per_page=100")["check_runs"]
        matching = [check for check in checks if check["name"] == "CI" and check["app"]["slug"] == "github-actions"]
        if matching:
            check = max(matching, key=lambda item: item["id"])
            if check["status"] == "completed":
                if check["conclusion"] != "success":
                    raise ValueError(f"Catalog CI did not pass: {check['conclusion']}")
                return
        time.sleep(interval)
    raise ValueError("Timed out awaiting catalog CI; the PR remains open for recovery")


def verify_tag(tag, expected_sha=None):
    version = release.validate_tag(tag)
    if release.read_json(release.ROOT / ".release-please-manifest.json").get(".") != version:
        raise ValueError("The tag must match release-please's recorded release version")
    commit = release.run("git", "rev-parse", f"refs/tags/{tag}^{{commit}}")
    if release.run("git", "rev-parse", "HEAD") != commit or (expected_sha and commit != expected_sha):
        raise ValueError("Checkout, tag and release event must identify the same commit")
    branch = "release/10.11" if version.startswith("10.11.") else "main"
    release.run("git", "merge-base", "--is-ancestor", commit, f"refs/remotes/origin/{branch}")
    release.validate_properties(release.properties(), version, tag)
    return commit


def verify_package(tag, directory, expected_sha):
    verify_tag(tag, expected_sha)
    metadata_path = directory / "manifest-entry.json"
    archive_path = directory / f"lastfm_{tag[1:]}.0.zip"
    release.validate_release_metadata(release.read_json(metadata_path), tag, archive_path.read_bytes())
    release.validate_checksums(directory, tag[1:] + ".0")
    return (archive_path, metadata_path, directory / "SHA256SUMS")


def provenance(tag, sha):
    """Record source and workflow revisions separately, including on recovery runs."""
    release.validate_tag(tag)
    if os.environ["GITHUB_REPOSITORY"] != release.REPOSITORY:
        raise ValueError("Provenance must be generated in the publishing repository")
    workflow_ref = os.environ["GITHUB_WORKFLOW_REF"]
    workflow_path, workflow_branch = workflow_ref.removeprefix(release.REPOSITORY + "/").split("@", 1)
    repository_url = f"https://github.com/{release.REPOSITORY}"
    return {
        "buildDefinition": {
            "buildType": "https://actions.github.io/buildtypes/workflow/v1",
            "externalParameters": {
                "workflow": {"repository": repository_url, "path": workflow_path, "ref": workflow_branch},
                "release": {"tag": tag, "commit": sha},
            },
            "internalParameters": {"github": {"event_name": os.environ["GITHUB_EVENT_NAME"]}},
            "resolvedDependencies": [
                {"uri": f"git+{repository_url}@refs/tags/{tag}", "digest": {"gitCommit": sha}},
                {"uri": f"git+{repository_url}@{workflow_branch}", "digest": {"gitCommit": os.environ["GITHUB_WORKFLOW_SHA"]}},
            ],
        },
        "runDetails": {
            "builder": {"id": "https://github.com/" + workflow_ref},
            "metadata": {"invocationId": f"{repository_url}/actions/runs/{os.environ['GITHUB_RUN_ID']}/attempts/{os.environ['GITHUB_RUN_ATTEMPT']}"},
        },
    }


def publish(tag, directory, expected_sha):
    paths = verify_package(tag, directory, expected_sha)
    published = find_release(tag)
    if published["prerelease"]:
        raise ValueError("Stable plugin publication cannot use a prerelease")
    assets = {item["name"]: item for item in published["assets"]}
    for path in paths:
        if path.name in assets:
            if asset_bytes(assets[path.name]) != path.read_bytes():
                raise ValueError(f"Refusing to replace different published bytes: {path.name}")
        else:
            subprocess.run(["gh", "release", "upload", tag, str(path), "--repo", release.REPOSITORY], check=True)
    if published["draft"]:
        subprocess.run(["gh", "release", "edit", tag, "--repo", release.REPOSITORY, "--draft=false", "--latest=" + ("false" if tag.startswith("v10.11.") else "true")], check=True)


def validate_catalog_against_published(path):
    catalog = release.read_json(path)
    release.validate_catalog(catalog)
    content = repo_api("contents/manifest.json?ref=main")
    current = json.loads(base64.b64decode(content["content"]))
    if {key: value for key, value in catalog[0].items() if key != "versions"} != {key: value for key, value in current[0].items() if key != "versions"}:
        raise ValueError("Catalog automation must preserve plugin metadata")
    proposed = {entry["version"]: entry for entry in catalog[0]["versions"]}
    release.validate_catalog(current)
    for entry in current[0]["versions"]:
        if proposed.get(entry["version"]) != entry:
            raise ValueError("Catalog PR modifies or removes an existing published entry")
    known = {entry["version"]: entry for entry in release_entries()}
    previous = {entry["version"] for entry in current[0]["versions"]}
    for version, entry in proposed.items():
        if version not in previous and known.get(version) != entry:
            raise ValueError("Catalog entry is not backed by a verified published release")


def catalog():
    # Read main immediately before generation and union every published new release,
    # including releases whose earlier catalog workflow failed or was superseded.
    base = repo_api("git/ref/heads/main")["object"]["sha"]
    commit = repo_api(f"git/commits/{base}")
    content = repo_api(f"contents/manifest.json?ref={base}")
    original = json.loads(base64.b64decode(content["content"]))
    updated = release.merge_catalog(original, release_entries())
    if original == updated:
        print("The catalog already contains every published release.")
        return
    tree = repo_api("git/trees", method="POST", data={
        "base_tree": commit["tree"]["sha"],
        "tree": [{"path": "manifest.json", "mode": "100644", "type": "blob", "content": json.dumps(updated, indent=2, ensure_ascii=False) + "\n"}],
    })
    created = repo_api("git/commits", method="POST", data={
        "message": "chore: update plugin catalog",
        "tree": tree["sha"],
        "parents": [base],
    })
    refs = repo_api(f"git/matching-refs/heads/{CATALOG_BRANCH}")
    exact = f"refs/heads/{CATALOG_BRANCH}"
    if any(ref["ref"] == exact for ref in refs):
        repo_api(f"git/refs/heads/{CATALOG_BRANCH}", method="PATCH", data={"sha": created["sha"], "force": True})
    else:
        repo_api("git/refs", method="POST", data={"ref": exact, "sha": created["sha"]})
    pulls = repo_api(f"pulls?state=open&base=main&head=lusoris:{CATALOG_BRANCH}")
    pull = pulls[0] if pulls else repo_api("pulls", method="POST", data={
        "title": "chore: update plugin catalog",
        "head": CATALOG_BRANCH,
        "base": "main",
        "body": "Add verified published Last.fm releases to the Jellyfin catalog. Existing versions are preserved; CI verifies the release metadata and archive checksums.",
    })
    number = pull["number"]
    files = list(pages(f"pulls/{number}/files"))
    if [item["filename"] for item in files] != ["manifest.json"]:
        raise ValueError("Automated catalog PR must change only manifest.json")
    sha = dispatch_ci(number)
    if sha != created["sha"]:
        raise ValueError("Catalog PR changed during generation")
    wait_for_ci(number, sha)
    subprocess.run(["gh", "pr", "merge", str(number), "--repo", release.REPOSITORY, "--auto", "--squash", "--match-head-commit", sha], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    dispatch = commands.add_parser("dispatch-ci")
    dispatch.add_argument("--prs", required=True)
    verify = commands.add_parser("verify-tag")
    verify.add_argument("--tag", required=True)
    verify.add_argument("--sha")
    for command in ("publish", "verify-package"):
        publication = commands.add_parser(command)
        publication.add_argument("--tag", required=True)
        publication.add_argument("--sha", required=True)
        publication.add_argument("--directory", type=Path, default=release.ROOT / "build/release")
    attestation = commands.add_parser("provenance")
    attestation.add_argument("--tag", required=True)
    attestation.add_argument("--sha", required=True)
    attestation.add_argument("--output", type=Path, default=release.ROOT / "build/provenance.json")
    commands.add_parser("catalog")
    validation = commands.add_parser("validate-catalog")
    validation.add_argument("--manifest", type=Path, default=release.ROOT / "manifest.json")
    args = parser.parse_args()
    if args.command == "dispatch-ci":
        dispatch_release_prs(args.prs)
    elif args.command == "verify-tag":
        print(verify_tag(args.tag, args.sha))
    elif args.command == "publish":
        publish(args.tag, args.directory, args.sha)
    elif args.command == "verify-package":
        verify_package(args.tag, args.directory, args.sha)
    elif args.command == "provenance":
        verify_tag(args.tag, args.sha)
        release.write_json(args.output, provenance(args.tag, args.sha))
    elif args.command == "catalog":
        catalog()
    else:
        validate_catalog_against_published(args.manifest)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
