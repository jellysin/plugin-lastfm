"""Build and validate Jellyfin packages without third-party Python dependencies."""

import argparse
import copy
import datetime as dt
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
GUID = "5e7fe7f0-b048-429e-a431-b1a7e69c930d"
NAME = "Last.fm"
REPOSITORY = "lusoris/jellyfin-plugin-lastfm"
ARTIFACT = "Jellyfin.Plugin.Lastfm.dll"
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
FOUR_PART = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")


def run(*args, cwd=ROOT):
    return subprocess.check_output(args, cwd=cwd, text=True, encoding="utf-8").strip()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def version_tuple(value, parts=4):
    pattern = FOUR_PART if parts == 4 else SEMVER
    if not isinstance(value, str) or not pattern.fullmatch(value):
        raise ValueError(f"Expected numeric {parts}-part version, got {value!r}")
    numbers = tuple(map(int, value.split(".")))
    if any(number > 65534 for number in numbers):
        raise ValueError("Assembly version components must not exceed 65534")
    return numbers


def validate_tag(tag):
    if not tag.startswith("v"):
        raise ValueError("New release tags must use vMAJOR.MINOR.PATCH")
    version_tuple(tag[1:], 3)
    return tag[1:]


def md5(data):
    # Jellyfin's repository protocol requires MD5, not a security signature.
    return hashlib.md5(data, usedforsecurity=False).hexdigest()


def timestamp(value):
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("Release timestamps must include a timezone")
    return parsed.astimezone(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def validate_entry(entry):
    for key in ("version", "targetAbi"):
        version_tuple(entry[key])
    if not re.fullmatch(r"[a-f0-9]{32}", entry["checksum"]):
        raise ValueError("Invalid MD5 checksum")
    if timestamp(entry["timestamp"]) != entry["timestamp"]:
        raise ValueError("Timestamp must use canonical UTC ...Z format")
    if not isinstance(entry["changelog"], str) or not entry["changelog"].strip():
        raise ValueError("A release requires a changelog")
    if not isinstance(entry["sourceUrl"], str) or not entry["sourceUrl"].startswith("https://"):
        raise ValueError("Release archives require an HTTPS source URL")


def validate_catalog(catalog):
    if not isinstance(catalog, list) or len(catalog) != 1:
        raise ValueError("This catalog must contain exactly one plugin")
    plugin = catalog[0]
    if plugin.get("guid") != GUID or plugin.get("name") != NAME:
        raise ValueError("Plugin identity must remain unchanged")
    seen = set()
    for entry in plugin["versions"]:
        validate_entry(entry)
        if entry["version"] in seen:
            raise ValueError("Duplicate catalog version")
        seen.add(entry["version"])
    return plugin


def merge_catalog(catalog, entries):
    validate_catalog(catalog)
    result = copy.deepcopy(catalog)
    versions = {entry["version"]: entry for entry in result[0]["versions"]}
    for entry in entries:
        validate_entry(entry)
        previous = versions.get(entry["version"])
        if previous is not None and previous != entry:
            raise ValueError(f"Published version {entry['version']} has conflicting metadata")
        versions[entry["version"]] = copy.deepcopy(entry)
    result[0]["versions"] = sorted(versions.values(), key=lambda item: version_tuple(item["version"]), reverse=True)
    return result


def validate_zip(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if archive.namelist() != [ARTIFACT]:
            raise ValueError("The archive must contain only the plugin DLL at its root")
        if archive.testzip() is not None or not archive.read(ARTIFACT).startswith(b"MZ"):
            raise ValueError("Invalid plugin assembly or archive")


def make_zip(assembly):
    if not assembly.startswith(b"MZ"):
        raise ValueError("The plugin artifact must be a PE assembly")
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
        info = zipfile.ZipInfo(ARTIFACT, date_time=(1980, 1, 1, 0, 0, 0))
        info.create_system = 3
        info.external_attr = 0o100644 << 16
        archive.writestr(info, assembly)
    data = buffer.getvalue()
    validate_zip(data)
    return data


def checksum_lines(directory, names):
    return checksums_for_assets({name: (directory / name).read_bytes() for name in names})


def checksums_for_assets(assets):
    return "".join(f"{hashlib.sha256(assets[name]).hexdigest()}  {name}\n" for name in sorted(assets))


def validate_checksums(directory, version):
    names = ["manifest-entry.json", f"lastfm_{version}.zip"]
    if (directory / "SHA256SUMS").read_text(encoding="utf-8") != checksum_lines(directory, names):
        raise ValueError("SHA256SUMS does not match the complete release asset set")


def properties():
    names = "Version,AssemblyVersion,FileVersion,JellyfinVersion,TargetAbi,TargetFramework,TargetPath"
    output = run("dotnet", "msbuild", "Jellyfin.Plugin.Lastfm/Jellyfin.Plugin.Lastfm.csproj", "-nologo", "-property:Configuration=Release", f"-getProperty:{names}")
    return json.loads(output)["Properties"]


def validate_properties(props, version, tag=None):
    version_tuple(version, 3)
    if props["Version"] != version or props["AssemblyVersion"] != version + ".0" or props["FileVersion"] != version + ".0":
        raise ValueError("version.txt and evaluated assembly versions disagree")
    version_tuple(props["JellyfinVersion"], 3)
    if props["TargetAbi"] != props["JellyfinVersion"] + ".0":
        raise ValueError("TargetAbi must match the pinned Jellyfin package")
    legacy = props["JellyfinVersion"].startswith("10.11.")
    if not legacy and not props["JellyfinVersion"].startswith("12."):
        raise ValueError("Unsupported Jellyfin release line")
    if legacy != version.startswith("10.11."):
        raise ValueError("Plugin and Jellyfin release lines disagree")
    expected_framework = "net9.0" if props["JellyfinVersion"].startswith("10.11.") else "net10.0"
    if props["TargetFramework"] != expected_framework:
        raise ValueError("Target framework does not match the supported Jellyfin line")
    if tag and validate_tag(tag) != version:
        raise ValueError("Release tag does not match version.txt")


def release_notes(version):
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(r"^##\s+(?:\[)?" + re.escape(version) + r"(?:\]|\s|$).*?\n(.*?)(?=^##\s|\Z)", text, re.M | re.S)
    if match and match.group(1).strip():
        return match.group(1).strip()
    return f"Last.fm {version}; see the release notes for details."


def package(output, tag=None):
    version = (ROOT / "version.txt").read_text(encoding="utf-8").strip()
    props = properties()
    validate_properties(props, version, tag)
    validate_catalog(read_json(ROOT / "manifest.json"))
    if GUID not in (ROOT / "Jellyfin.Plugin.Lastfm/Plugin.cs").read_text(encoding="utf-8"):
        raise ValueError("Plugin GUID differs from the catalog")
    assembly = Path(props["TargetPath"])
    if not assembly.is_file():
        raise ValueError("Build the Release configuration before packaging")
    data = make_zip(assembly.read_bytes())
    archive_name = f"lastfm_{version}.0.zip"
    entry = {
        "version": version + ".0",
        "changelog": release_notes(version),
        "targetAbi": props["TargetAbi"],
        "sourceUrl": f"https://github.com/{REPOSITORY}/releases/download/v{version}/{archive_name}",
        "checksum": md5(data),
        "timestamp": timestamp(run("git", "show", "-s", "--format=%cI", "HEAD")),
    }
    validate_entry(entry)
    output.mkdir(parents=True, exist_ok=True)
    (output / archive_name).write_bytes(data)
    write_json(output / "manifest-entry.json", {"guid": GUID, "name": NAME, "entry": entry})
    (output / "SHA256SUMS").write_text(checksum_lines(output, [archive_name, "manifest-entry.json"]), encoding="utf-8")
    print(output / archive_name)


def validate_release_metadata(metadata, tag, archive):
    if metadata.get("guid") != GUID or metadata.get("name") != NAME:
        raise ValueError("Release metadata plugin identity mismatch")
    entry = metadata["entry"]
    validate_entry(entry)
    version = validate_tag(tag) + ".0"
    expected_url = f"https://github.com/{REPOSITORY}/releases/download/{tag}/lastfm_{version}.zip"
    if entry["version"] != version or entry["sourceUrl"] != expected_url:
        raise ValueError("Release metadata does not match its tag/asset")
    validate_zip(archive)
    if md5(archive) != entry["checksum"]:
        raise ValueError("Published archive checksum mismatch")
    return entry


def audit(path):
    report = read_json(path)
    if report.get("problems") or report.get("version") != 1 or not report.get("projects") or not report.get("sources"):
        raise ValueError("NuGet vulnerability report could not be completed")
    findings = []
    for project in report.get("projects", []):
        for framework in project.get("frameworks", []):
            for package in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                for vulnerability in package.get("vulnerabilities", []):
                    findings.append(f"{package['id']}: {vulnerability['severity']} {vulnerability['advisoryurl']}")
    if findings:
        raise ValueError("Vulnerable dependencies:\n" + "\n".join(findings))


def audit_sarif(directory):
    reports = list(directory.glob("*.sarif"))
    if not reports:
        raise ValueError("CodeQL produced no SARIF reports")
    findings = []
    for report in reports:
        analyses = read_json(report).get("runs")
        if not analyses:
            raise ValueError("CodeQL SARIF report contains no analysis runs")
        for analysis in analyses:
            if any(invocation.get("executionSuccessful") is False for invocation in analysis.get("invocations", [])):
                raise ValueError("CodeQL reported an unsuccessful analysis")
            components = [analysis["tool"]["driver"]] + analysis["tool"].get("extensions", [])
            rules = {rule["id"]: rule for component in components for rule in component.get("rules", [])}
            for result in analysis.get("results", []):
                rule_id = result.get("ruleId") or result.get("rule", {}).get("id")
                if rule_id not in rules:
                    raise ValueError("CodeQL result references an unknown rule")
                rule = rules[rule_id]
                severity = float(rule.get("properties", {}).get("security-severity", 0))
                if severity >= 7:
                    findings.append(f"{rule_id}: security severity {severity}")
    if findings:
        raise ValueError("High/critical CodeQL findings:\n" + "\n".join(findings))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    build = sub.add_parser("package")
    build.add_argument("--output", type=Path, default=ROOT / "build/release")
    build.add_argument("--tag")
    check = sub.add_parser("validate")
    check.add_argument("--manifest", type=Path, default=ROOT / "manifest.json")
    security = sub.add_parser("audit")
    security.add_argument("report", type=Path)
    sarif = sub.add_parser("audit-sarif")
    sarif.add_argument("directory", type=Path)
    args = parser.parse_args()
    if args.command == "package":
        package(args.output, args.tag)
    elif args.command == "validate":
        validate_catalog(read_json(args.manifest))
    elif args.command == "audit":
        audit(args.report)
    else:
        audit_sarif(args.directory)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
