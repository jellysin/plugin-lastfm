"""The local gate's SDK containers carry the toolchain CI uses, pinned by digest, and the update bot moves them together."""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/sdk.sh"
IMAGE = re.compile(r'^(DOTNET|NODE)_IMAGE="([^"]*)"$', re.MULTILINE)
PINNED = {"DOTNET": re.compile(r"mcr\.microsoft\.com/dotnet/sdk:(\d+\.\d+\.\d+)@sha256:[a-f0-9]{64}"),
          "NODE": re.compile(r"node:(\d+\.\d+\.\d+)@sha256:[a-f0-9]{64}")}
LIMIT = re.compile(r'^MEMORY=(\d+)g$', re.MULTILINE)
UPDATES = json.loads((ROOT / "renovate.json").read_text(encoding="utf-8"))


def expected():
    sdk = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    return {"DOTNET": sdk, "NODE": (ROOT / ".node-version").read_text(encoding="utf-8").strip()}


def drift(script, versions):
    """Return what keeps the script's images from being the CI toolchain pinned by digest, each as one statement."""
    images = IMAGE.findall(script)
    if sorted(name for name, _ in images) != ["DOTNET", "NODE"]:
        return [f"the script names the images {sorted(name for name, _ in images)}"]
    problems = []
    for name, image in images:
        pinned = PINNED[name].fullmatch(image)
        if not pinned:
            problems.append(f"{name} image {image} is not a release pinned by digest")
        elif pinned[1] != versions[name]:
            problems.append(f"{name} image carries {pinned[1]}, CI uses {versions[name]}")
    memory = LIMIT.findall(script)
    if len(memory) != 1 or not 1 <= int(memory[0]) <= 4:
        problems.append(f"the container memory limit {memory} is not one value of at most 4g")
    return problems


def bot_drift(script, updates):
    """Return why the update bot would move global.json or .node-version without the script, or the script alone."""
    managers = [entry for entry in updates["customManagers"] if entry["managerFilePatterns"] == ["/^tools/sdk\\.sh$/"]]
    if len(managers) != 1 or managers[0].get("datasourceTemplate") != "docker":
        return ["no single update entry reads the script's images"]
    read = sorted(match["depName"] for match in re.finditer(managers[0]["matchStrings"][0].replace("(?<", "(?P<"), script))
    if read != ["mcr.microsoft.com/dotnet/sdk", "node"]:
        return [f"the update entry reads {read}"]
    grouped = [rule for rule in updates["packageRules"]
               if sorted(rule.get("matchFileNames", [])) == [".node-version", "global.json", "tools/sdk.sh"] and rule.get("groupName")]
    if len(grouped) != 1:
        return ["no single rule puts the script and both version files into one pull request"]
    # The image registries give no release time; without this rule the three-day wait holds the images forever.
    timeless = [rule for rule in updates["packageRules"] if rule.get("matchFileNames") == ["tools/sdk.sh"]
                and rule.get("matchDatasources") == ["docker"] and rule.get("minimumReleaseAgeBehaviour") == "timestamp-optional"]
    return [] if len(timeless) == 1 else ["no rule lets the images move without a release time"]


class SdkPinTests(unittest.TestCase):
    def test_the_containers_carry_the_ci_toolchain(self):
        self.assertEqual([], drift(SCRIPT.read_text(encoding="utf-8"), expected()))

    def test_update_bot_moves_the_containers_with_the_version_files(self):
        self.assertEqual([], bot_drift(SCRIPT.read_text(encoding="utf-8"), UPDATES))

    def test_an_older_an_unpinned_a_missing_image_and_a_larger_limit_are_drift(self):
        script = SCRIPT.read_text(encoding="utf-8")
        dotnet = IMAGE.search(script)[2] if IMAGE.search(script)[1] == "DOTNET" else ""
        versions = expected()
        refused = {
            "older SDK": script.replace(versions["DOTNET"] + "@", "10.0.100@", 1),
            "older Node": script.replace("node:" + versions["NODE"], "node:24.0.0", 1),
            "tag without digest": script.replace(dotnet, dotnet.split("@")[0], 1),
            "no Node image": re.sub(r'^NODE_IMAGE=.*$', "", script, flags=re.MULTILINE),
            "eight gigabytes": script.replace("MEMORY=4g", "MEMORY=8g", 1),
        }
        for label, changed in refused.items():
            with self.subTest(label):
                self.assertNotEqual(script, changed)
                self.assertNotEqual([], drift(changed, versions))
        without_group = {**UPDATES, "packageRules": [rule for rule in UPDATES["packageRules"] if rule.get("groupName") != "SDK toolchain"]}
        self.assertNotEqual([], bot_drift(script, without_group))
        waiting = {**UPDATES, "packageRules": [rule for rule in UPDATES["packageRules"] if "minimumReleaseAgeBehaviour" not in rule]}
        self.assertNotEqual([], bot_drift(script, waiting))


if __name__ == "__main__":
    unittest.main()
