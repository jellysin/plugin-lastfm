"""The local gate's SDK containers carry the CI toolchain and target runtime, pinned by digest, and the update bot moves them together."""

import json
import re
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/sdk.sh"
IMAGE = re.compile(r'^(DOTNET|DOTNET_RUNTIME|NODE)_IMAGE="([^"]*)"$', re.MULTILINE)
PINNED = {"DOTNET": re.compile(r"mcr\.microsoft\.com/dotnet/sdk:(\d+\.\d+\.\d+)@sha256:[a-f0-9]{64}"),
          "DOTNET_RUNTIME": re.compile(r"mcr\.microsoft\.com/dotnet/aspnet:(\d+)\.\d+\.\d+@sha256:[a-f0-9]{64}"),
          "NODE": re.compile(r"node:(\d+\.\d+\.\d+)@sha256:[a-f0-9]{64}")}
LIMIT = re.compile(r'^MEMORY=(\d+)g$', re.MULTILINE)
UPDATES = json.loads((ROOT / "renovate.json").read_text(encoding="utf-8"))


def expected():
    """Read the CI toolchain versions and the plugin's target runtime major."""
    sdk = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    target = re.search(r"<TargetFramework>net(\d+)\.0</TargetFramework>",
                       (ROOT / "Directory.Build.props").read_text(encoding="utf-8"))
    if target is None:
        raise ValueError("Directory.Build.props must declare <TargetFramework>netMAJOR.0</TargetFramework>")
    return {"DOTNET": sdk, "DOTNET_RUNTIME": target[1],
            "NODE": (ROOT / ".node-version").read_text(encoding="utf-8").strip()}


def drift(script, versions):
    """Return what keeps the script's images from being the CI toolchain pinned by digest, each as one statement."""
    images = IMAGE.findall(script)
    if sorted(name for name, _ in images) != ["DOTNET", "DOTNET_RUNTIME", "NODE"]:
        return [f"the script names the images {sorted(name for name, _ in images)}"]
    problems = []
    for name, image in images:
        pinned = PINNED[name].fullmatch(image)
        if not pinned:
            problems.append(f"{name} image {image} is not a release pinned by digest")
        elif pinned[1] != versions[name]:
            problems.append(f"{name} image carries {pinned[1]}, the toolchain needs {versions[name]}")
    if "'COPY --from=runtime /usr/share/dotnet/shared/ /usr/share/dotnet/shared/'" not in script:
        problems.append("the dotnet container does not copy the target runtime's shared frameworks")
    if '"FROM $DOTNET_RUNTIME_IMAGE AS runtime"' not in script:
        problems.append("the dotnet container does not use DOTNET_RUNTIME_IMAGE as its runtime stage")
    memory = LIMIT.findall(script)
    if len(memory) != 1 or not 1 <= int(memory[0]) <= 4:
        problems.append(f"the container memory limit {memory} is not one value of at most 4g")
    return problems


def bot_capture_drift(script, matches):
    """Return the missing or incorrect update captures for the script's image pins."""
    pins = {}
    for _, image in IMAGE.findall(script):
        tagged, _, digest = image.partition("@")
        name, _, version = tagged.rpartition(":")
        pins[name] = (version, digest)
    problems = []
    for match in matches:
        name = match.get("depName", "")
        for group, pinned in zip(("currentValue", "currentDigest"), pins.get(name, ("", ""))):
            value = match.get(group)
            if not value:
                problems.append(f"the update entry reads no {group} for {name}")
            elif value != pinned:
                problems.append(f"the update entry reads {group} {value!r} for {name} instead of the pinned {pinned!r}")
    return problems


def bot_drift(script, updates, major):
    """Return why the update bot would move global.json or .node-version without the script, or the script alone."""
    managers = [entry for entry in updates["customManagers"] if entry["managerFilePatterns"] == ["/^tools/sdk\\.sh$/"]]
    if len(managers) != 1 or managers[0].get("datasourceTemplate") != "docker":
        return ["no single update entry reads the script's images"]
    regex = managers[0]["matchStrings"][0].replace("(?<", "(?P<")
    matches = [match.groupdict("") for match in re.finditer(regex, script)]
    read = sorted(match.get("depName", "") for match in matches)
    problems = bot_capture_drift(script, matches)
    if read != ["mcr.microsoft.com/dotnet/aspnet", "mcr.microsoft.com/dotnet/sdk", "node"]:
        return [f"the update entry reads {read}", *problems]
    grouped = [rule for rule in updates["packageRules"]
               if sorted(rule.get("matchFileNames", [])) == [".node-version", "global.json", "tools/sdk.sh"] and rule.get("groupName")]
    if len(grouped) != 1:
        return ["no single rule puts the script and both version files into one pull request", *problems]
    # The image registries give no release time; without this rule the three-day wait holds the images forever.
    timeless = [rule for rule in updates["packageRules"] if rule.get("matchFileNames") == ["tools/sdk.sh"]
                and rule.get("matchDatasources") == ["docker"] and rule.get("minimumReleaseAgeBehaviour") == "timestamp-optional"]
    if len(timeless) != 1:
        problems.append("no rule lets the images move without a release time")
    held = [rule for rule in updates["packageRules"]
            if rule.get("matchPackageNames") == ["mcr.microsoft.com/dotnet/aspnet"]]
    pattern = f"/^{major}\\./"
    if len(held) != 1 or held[0].get("allowedVersions") != pattern:
        problems.append(f"no single runtime image rule holds allowedVersions at {pattern}")
    return problems


class SdkPinTests(unittest.TestCase):
    """Keep the containers and update rules aligned with the toolchain and target."""

    def test_the_containers_carry_the_ci_toolchain(self):
        """The real script supplies pinned toolchain images and the target runtime."""
        self.assertEqual([], drift(SCRIPT.read_text(encoding="utf-8"), expected()))

    def test_update_bot_moves_the_containers_with_the_version_files(self):
        """The real update rules group the images and hold the runtime on target."""
        self.assertEqual([], bot_drift(SCRIPT.read_text(encoding="utf-8"), UPDATES, expected()["DOTNET_RUNTIME"]))

    def test_a_missing_target_framework_fails_clearly(self):
        """A target file without the expected framework cannot silently supply a major."""
        sdk = (ROOT / "global.json").read_text(encoding="utf-8")
        with patch.object(Path, "read_text", side_effect=[sdk, "<Project />"]):
            with self.assertRaisesRegex(ValueError, "TargetFramework"):
                expected()

    def test_runtime_patch_releases_remain_on_target(self):
        """A different pinned runtime release on the target major remains valid."""
        script = SCRIPT.read_text(encoding="utf-8")
        versions = expected()
        changed = re.sub(r'aspnet:\d+\.\d+\.\d+@', "aspnet:" + versions["DOTNET_RUNTIME"] + ".0.0@", script)
        self.assertNotEqual(script, changed)
        self.assertEqual([], drift(changed, versions))

    def test_runtime_stage_must_use_the_pinned_image(self):
        """A runtime stage using another image fails even with references to the pin elsewhere."""
        script = SCRIPT.read_text(encoding="utf-8")
        changed = script.replace("FROM $DOTNET_RUNTIME_IMAGE AS runtime",
                                 "FROM mcr.microsoft.com/dotnet/aspnet:9.0.0 AS runtime", 1)
        self.assertNotEqual(script, changed)
        self.assertNotEqual([], drift(changed, expected()))

    def test_runtime_copy_must_be_active(self):
        """A commented Dockerfile COPY does not install the shared frameworks."""
        script = SCRIPT.read_text(encoding="utf-8")
        changed = script.replace("'COPY --from=runtime", "'# COPY --from=runtime", 1)
        self.assertNotEqual(script, changed)
        self.assertNotEqual([], drift(changed, expected()))

    def test_an_older_an_unpinned_a_missing_image_and_a_larger_limit_are_drift(self):
        """Planted image, layer and resource drift must each be rejected."""
        script = SCRIPT.read_text(encoding="utf-8")
        dotnet = IMAGE.search(script)[2] if IMAGE.search(script)[1] == "DOTNET" else ""
        versions = expected()
        refused = {
            "older SDK": script.replace(versions["DOTNET"] + "@", "10.0.100@", 1),
            "older Node": script.replace("node:" + versions["NODE"], "node:24.0.0", 1),
            "tag without digest": script.replace(dotnet, dotnet.split("@")[0], 1),
            "no Node image": re.sub(r'^NODE_IMAGE=.*$', "", script, flags=re.MULTILINE),
            "eight gigabytes": script.replace("MEMORY=4g", "MEMORY=8g", 1),
            "runtime of another major": script.replace("aspnet:" + versions["DOTNET_RUNTIME"] + ".",
                                                       f"aspnet:{int(versions['DOTNET_RUNTIME']) + 1}.", 1),
            "runtime tag without digest": re.sub(r'(DOTNET_RUNTIME_IMAGE="[^"]*)@sha256:[a-f0-9]{64}', r'\1', script),
            "no runtime image": re.sub(r'^DOTNET_RUNTIME_IMAGE=.*$', "", script, flags=re.MULTILINE),
            "no runtime layer": script.replace("COPY --from=runtime /usr/share/dotnet/shared/ /usr/share/dotnet/shared/", ""),
            "unused runtime image": script.replace("$DOTNET_RUNTIME_IMAGE", "unused-runtime"),
        }
        for label, changed in refused.items():
            with self.subTest(label):
                self.assertNotEqual(script, changed)
                self.assertNotEqual([], drift(changed, versions))

    def test_missing_or_drifting_bot_rules_are_refused(self):
        """Planted grouping, release-time, runtime hold and extraction drift fail."""
        script = SCRIPT.read_text(encoding="utf-8")
        major = expected()["DOTNET_RUNTIME"]
        rules = UPDATES["packageRules"]
        refused = {
            "no group": {**UPDATES, "packageRules": [rule for rule in rules if rule.get("groupName") != "SDK toolchain"]},
            "waiting": {**UPDATES, "packageRules": [rule for rule in rules if "minimumReleaseAgeBehaviour" not in rule]},
            "no runtime hold": {**UPDATES, "packageRules": [rule for rule in rules
                                 if rule.get("matchPackageNames") != ["mcr.microsoft.com/dotnet/aspnet"]]},
            "hold another major": {**UPDATES, "packageRules": [
                {**rule, "allowedVersions": f"/^{int(major) + 1}\\./"}
                if rule.get("matchPackageNames") == ["mcr.microsoft.com/dotnet/aspnet"]
                else rule for rule in rules]},
            "duplicate runtime hold": {**UPDATES, "packageRules": rules + [rule for rule in rules
                                        if rule.get("matchPackageNames") == ["mcr.microsoft.com/dotnet/aspnet"]]},
            "manager reads SDK only": {**UPDATES, "customManagers": [
                {**entry, "matchStrings": [pattern.replace("(?:sdk|aspnet)", "sdk") for pattern in entry["matchStrings"]]}
                for entry in UPDATES["customManagers"]]},
        }
        for label, changed in refused.items():
            with self.subTest(label):
                self.assertNotEqual(UPDATES, changed)
                self.assertNotEqual([], bot_drift(script, changed, major))

    def test_update_bot_requires_complete_pin_captures(self):
        """Each image needs both named captures with the full value from its pin."""
        script = SCRIPT.read_text(encoding="utf-8")
        major = expected()["DOTNET_RUNTIME"]
        managers = UPDATES["customManagers"]
        manager, = (entry for entry in managers if entry["managerFilePatterns"] == ["/^tools/sdk\\.sh$/"])
        pattern = manager["matchStrings"][0]
        partials = {
            "currentValue": pattern.replace("(?<currentValue>[0-9]+", "(?<currentValue>[0-9])(?:[0-9]*"),
            "currentDigest": pattern.replace("[a-f0-9]{64})", "[a-f0-9]{63})[a-f0-9]"),
        }
        for group, partial in partials.items():
            capture = f"(?<{group}>"
            for label, changed in {
                "missing": pattern.replace(capture, "(?:"),
                "empty": pattern.replace(capture, capture + ")(?:"),
                "partial": partial,
            }.items():
                with self.subTest(group=group, mutation=label):
                    self.assertNotEqual(pattern, changed)
                    updates = {**UPDATES, "customManagers": [
                        {**entry, "matchStrings": [changed]} if entry is manager else entry for entry in managers]}
                    problems = bot_drift(script, updates, major)
                    self.assertEqual(3, len(problems), problems)
                    self.assertTrue(all(group in problem for problem in problems), problems)


if __name__ == "__main__":
    unittest.main()
