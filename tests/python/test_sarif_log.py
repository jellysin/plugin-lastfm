"""Every compile writes its diagnostics as SARIF 2.1 into the ignored build folder, and the folder exists before the compiler runs."""

import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROPS = ROOT / "Directory.Build.props"


def problems(text):
    """Return what keeps the build properties from writing one SARIF 2.1 file per project under build/sarif, each as one statement."""
    project = ET.fromstring(text)
    values = {child.tag: (child.text or "").strip() for group in project.iter("PropertyGroup") for child in group}
    found = []
    folder = values.get("SarifDirectory", "")
    if folder != "$(MSBuildThisFileDirectory)build/sarif/":
        found.append(f"the SARIF folder is {folder!r}, not build/sarif/ in the repository")
    if values.get("ErrorLog", "") != "$(SarifDirectory)$(MSBuildProjectName).sarif,version=2.1":
        found.append(f"the error log is {values.get('ErrorLog')!r}, not one SARIF 2.1 file per project")
    made = [target for target in project.iter("Target")
            if target.get("BeforeTargets") == "CoreCompile"
            and any(step.get("Directories") == "$(SarifDirectory)" for step in target.iter("MakeDir"))]
    if not made:
        found.append("no target creates the SARIF folder before CoreCompile")
    return found


class SarifLogTest(unittest.TestCase):
    def test_build_writes_sarif(self):
        self.assertEqual(problems(PROPS.read_text(encoding="utf-8")), [])

    def test_build_folder_is_ignored(self):
        self.assertIn("build/", (ROOT / ".gitignore").read_text(encoding="utf-8").splitlines())

    def test_refuses_missing_or_wrong_log(self):
        good = PROPS.read_text(encoding="utf-8")
        self.assertEqual(len(problems(good.replace(",version=2.1", ""))), 1)
        self.assertEqual(len(problems(good.replace("build/sarif/", "sarif/"))), 1)
        self.assertEqual(len(problems(good.replace('BeforeTargets="CoreCompile"', 'AfterTargets="CoreCompile"'))), 1)


if __name__ == "__main__":
    unittest.main()
