"""The analyzer release a document links as pinned is the one the project file pins, and the update bot moves both."""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "tools/JellySin.CodePolicy/JellySin.CodePolicy.csproj"
DOCUMENT = ROOT / "tools/JellySin.CodePolicy/README.md"
PACKAGE = "SonarAnalyzer.CSharp"
PINNED = re.compile(r'<PackageReference Include="SonarAnalyzer\.CSharp" Version="(\d+(?:\.\d+){3})"')
LINKED = re.compile(r"https://github\.com/SonarSource/sonar-dotnet/releases/tag/([^)\s]+)")
MENTION = re.compile(r"sonar-dotnet/releases")
UPDATES = json.loads((ROOT / "renovate.json").read_text(encoding="utf-8"))


def drift(project, document):
    """Return what keeps the document's release link from naming the project's pin, each as one statement."""
    pinned, linked = PINNED.findall(project), LINKED.findall(document)
    if len(pinned) != 1:
        return [f"the project file pins {len(pinned)} exact release(s) of {PACKAGE}"]
    # A link in a form the pattern does not read would escape the comparison.
    if len(linked) != 1 or len(MENTION.findall(document)) != 1:
        return [f"the document links {len(linked)} release(s) in {len(MENTION.findall(document))} mention(s)"]
    return [] if linked == pinned else [f"the document links {linked[0]}, the project file pins {pinned[0]}"]


def bot_drift(document, updates):
    """Return why the update bot would move the project's pin without the document's link."""
    managers = [entry for entry in updates["customManagers"] if entry["managerFilePatterns"] == ["/^tools/JellySin\\.CodePolicy/README\\.md$/"]]
    if len(managers) != 1 or managers[0].get("depNameTemplate") != PACKAGE or managers[0].get("datasourceTemplate") != "nuget":
        return ["no single update entry reads the document as this package"]
    read = [match["currentValue"] for match in re.finditer(managers[0]["matchStrings"][0].replace("(?<", "(?P<"), document)]
    if read != LINKED.findall(document):
        return [f"the update entry reads {read}, the document links {LINKED.findall(document)}"]
    grouped = [rule for rule in updates["packageRules"] if rule.get("matchPackageNames") == [PACKAGE] and rule.get("groupName")]
    return [] if len(grouped) == 1 else ["no single rule puts both places into one pull request"]


class DocumentedPinTests(unittest.TestCase):
    def test_document_links_the_release_the_project_pins(self):
        self.assertEqual([], drift(PROJECT.read_text(encoding="utf-8"), DOCUMENT.read_text(encoding="utf-8")))

    def test_update_bot_moves_the_link_with_the_pin(self):
        self.assertEqual([], bot_drift(DOCUMENT.read_text(encoding="utf-8"), UPDATES))

    def test_an_older_a_missing_and_an_unreadable_link_are_drift(self):
        project = '<PackageReference Include="SonarAnalyzer.CSharp" Version="10.35.0.4138" GeneratePathProperty="true" />'
        link = "- [Pinned analyzer release](https://github.com/SonarSource/sonar-dotnet/releases/tag/{})"
        refused = {
            "older release": (project, link.format("10.33.0.1635")),
            "no link": (project, "The analyzer is pinned."),
            "two links": (project, link.format("10.35.0.4138") + "\n" + link.format("10.33.0.1635")),
            "a link the pattern does not read": (project, "[release](https://github.com/SonarSource/sonar-dotnet/releases/10.35.0.4138)"),
            "a range in the project file": (project.replace('"10.35.0.4138"', '"10.*"'), link.format("10.35.0.4138")),
            "no pin in the project file": ("<Project />", link.format("10.35.0.4138")),
        }
        for label, (source, document) in refused.items():
            with self.subTest(label):
                self.assertEqual(1, len(drift(source, document)))
        self.assertEqual([], drift(project, link.format("10.35.0.4138")))

    def test_an_update_entry_that_misses_the_link_or_the_group_is_drift(self):
        document = "- [Pinned analyzer release](https://github.com/SonarSource/sonar-dotnet/releases/tag/10.35.0.4138)"
        entry = {"managerFilePatterns": ["/^tools/JellySin\\.CodePolicy/README\\.md$/"], "depNameTemplate": PACKAGE, "datasourceTemplate": "nuget",
                 "matchStrings": ["sonar-dotnet/releases/tag/(?<currentValue>[0-9.]+)"]}
        rule = {"matchPackageNames": [PACKAGE], "groupName": PACKAGE}
        refused = {
            "no entry": {"customManagers": [], "packageRules": [rule]},
            "another package": {"customManagers": [entry | {"depNameTemplate": "Other"}], "packageRules": [rule]},
            "a pattern that reads nothing": {"customManagers": [entry | {"matchStrings": ["releases/v(?<currentValue>[0-9.]+)"]}], "packageRules": [rule]},
            "no group": {"customManagers": [entry], "packageRules": []},
        }
        for label, updates in refused.items():
            with self.subTest(label):
                self.assertEqual(1, len(bot_drift(document, updates)))
        self.assertEqual([], bot_drift(document, {"customManagers": [entry], "packageRules": [rule]}))


if __name__ == "__main__":
    unittest.main()
