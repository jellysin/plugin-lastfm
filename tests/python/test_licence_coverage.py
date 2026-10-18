"""A file the whole-tree REUSE table would license without a declaration fails the licence coverage check."""

import contextlib
import io
import subprocess
import tempfile
import unittest
from pathlib import Path

from tools.licence_coverage import CoverageError, main, pattern, undeclared

REPOSITORY = Path(__file__).resolve().parents[2]
REUSE_TOML = """version = 1
[[annotations]]
path = ["**"]
SPDX-FileCopyrightText = "2026 Test"
SPDX-License-Identifier = "EUPL-1.2"
[[annotations]]
path = ["docs/**"]
SPDX-FileCopyrightText = "2026 Test"
SPDX-License-Identifier = "CC-BY-SA-4.0"
"""


def run(arguments):
    output, errors = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
        code = main(arguments)
    return code, output.getvalue(), errors.getvalue()


class RepositoryTests(unittest.TestCase):
    def test_repository_has_no_undeclared_file(self):
        self.assertEqual(undeclared(REPOSITORY), [])


class PlantedTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        subprocess.run(["git", "init", "-q", str(self.root)], check=True, capture_output=True, timeout=30)
        self.write("REUSE.toml", REUSE_TOML)
        self.write("src/Plugin.cs", "class Plugin {}\n")
        self.write("docs/guide.md", "# Guide\n")
        self.write("LICENSES/EUPL-1.2.txt", "licence text\n")

    def tearDown(self):
        self.directory.cleanup()

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_declared_tree_passes(self):
        code, output, errors = run(["--root", str(self.root)])
        self.assertEqual((code, errors), (0, ""))
        self.assertIn("every file", output)

    def test_unlicensed_file_of_a_new_type_is_refused(self):
        self.write("src/vendor/font.woff2", "binary\n")
        code, _, errors = run(["--root", str(self.root)])
        self.assertEqual(code, 1)
        self.assertIn("src/vendor/font.woff2", errors)

    def test_unlicensed_file_in_a_new_place_is_refused(self):
        self.write("third_party/lib.cs", "class Lib {}\n")
        self.assertEqual(undeclared(self.root), ["third_party/lib.cs"])

    def test_own_spdx_header_is_accepted(self):
        self.write("third_party/lib.cs", "// SPDX-License-" "Identifier: MIT\nclass Lib {}\n")
        self.assertEqual(undeclared(self.root), [])

    def test_other_reuse_table_is_accepted_and_the_whole_tree_table_is_not(self):
        self.write("docs/diagram.svg", "<svg/>\n")
        self.write("notes.txt", "notes\n")
        self.assertEqual(undeclared(self.root), ["notes.txt"])

    def test_ignored_files_are_not_listed(self):
        self.write(".gitignore", "build/\n")
        self.write("build/output.bin", "binary\n")
        self.assertEqual(undeclared(self.root), [])

    def test_unreadable_reuse_toml_fails_closed(self):
        self.write("REUSE.toml", "path = [\n")
        code, _, errors = run(["--root", str(self.root)])
        self.assertEqual(code, 2)
        self.assertIn("cannot read", errors)

    def test_missing_repository_fails_closed(self):
        with self.assertRaises(CoverageError):
            undeclared(self.root / "absent")


class PatternTests(unittest.TestCase):
    def test_single_star_stays_inside_one_directory(self):
        self.assertTrue(pattern("*.json").fullmatch("package.json"))
        self.assertFalse(pattern("*.json").fullmatch("src/package.json"))

    def test_double_star_crosses_directories(self):
        self.assertTrue(pattern("src/**.cs").fullmatch("src/a/b/Plugin.cs"))
        self.assertFalse(pattern("src/**.cs").fullmatch("src/a/b/Plugin.css"))

    def test_dots_are_literal(self):
        self.assertFalse(pattern("global.json").fullmatch("globalxjson"))


if __name__ == "__main__":
    unittest.main()
