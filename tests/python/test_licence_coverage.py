"""A file the whole-tree REUSE table would license without a declaration, or would label
EUPL-1.2 only although it names an MIT origin, fails the licence coverage check."""

import subprocess
import tempfile
import unittest
from pathlib import Path

import console
from tools.licence_coverage import CoverageError, main, mit_unlabelled, pattern, undeclared

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
# Split, so that the coverage check does not read this file as naming an MIT origin.
MIT_PROVENANCE = "derived_from: \"https://example.org/upstream (" "MIT)\"\n"
MIT_NOTICE = "Permission is hereby " "granted, free of charge, to any person\n"
SKILL = ".agents/skills/upstream/SKILL.md"


def mit_table(path, licence="MIT"):
    return f"""[[annotations]]
path = ["{path}"]
precedence = "aggregate"
SPDX-FileCopyrightText = "2026 Upstream"
SPDX-License-Identifier = "{licence}"
"""


def run(arguments):
    return console.run(main, arguments)


class RepositoryTests(unittest.TestCase):
    def test_repository_has_no_undeclared_file(self):
        self.assertEqual(undeclared(REPOSITORY), [])

    def test_repository_labels_every_mit_origin_mit(self):
        self.assertEqual(mit_unlabelled(REPOSITORY), [])


class PlantedRepository(unittest.TestCase):
    """A temporary git repository with a declared tree; holds no tests itself."""

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


class PlantedTests(PlantedRepository):
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


class MitLabelTests(PlantedRepository):
    def test_mit_provenance_labelled_eupl_only_is_refused(self):
        self.write(SKILL, MIT_PROVENANCE)
        code, _, errors = run(["--root", str(self.root)])
        self.assertEqual(code, 1)
        self.assertIn(f"{SKILL}: names an MIT origin but is not labelled MIT", errors)

    def test_mit_notice_text_labelled_eupl_only_is_refused(self):
        self.write("src/Vendored.cs", "// " + MIT_NOTICE)
        self.assertEqual(mit_unlabelled(self.root), ["src/Vendored.cs"])

    def test_mit_licence_title_line_labelled_eupl_only_is_refused(self):
        self.write("src/Vendored.cs", "// MIT License\n//\n// Copyright (c) Upstream\n")
        self.assertEqual(mit_unlabelled(self.root), ["src/Vendored.cs"])

    def test_changelog_prose_naming_the_mit_licence_is_not_a_notice(self):
        self.write("CHANGELOG.md", "* label the adapted register skills with their upstream MIT licence ([#170](x))\n")
        self.assertEqual(mit_unlabelled(self.root), [])

    def test_mit_table_accepts_the_file(self):
        self.write(SKILL, MIT_PROVENANCE)
        self.write("REUSE.toml", REUSE_TOML + mit_table(".agents/skills/upstream/**"))
        self.assertEqual(mit_unlabelled(self.root), [])

    def test_table_with_another_licence_does_not_accept_the_file(self):
        self.write(SKILL, MIT_PROVENANCE)
        self.write("REUSE.toml", REUSE_TOML + mit_table(".agents/skills/upstream/**", "Apache-2.0"))
        self.assertEqual(mit_unlabelled(self.root), [SKILL])

    def test_own_mit_header_accepts_the_file(self):
        self.write("src/Vendored.cs", "// SPDX-License-" "Identifier: EUPL-1.2 AND MIT\n// " + MIT_NOTICE)
        self.assertEqual(mit_unlabelled(self.root), [])

    def test_licence_files_need_no_label(self):
        self.write(".agents/skills/upstream/LICENSE", "MIT License\n\n" + MIT_NOTICE)
        self.write("LICENSES/MIT.txt", MIT_NOTICE)
        self.assertEqual(mit_unlabelled(self.root), [])


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
