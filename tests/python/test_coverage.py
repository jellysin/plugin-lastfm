"""Coverage parsing protects against stale, duplicate and misleading reports."""

import contextlib
import io
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools.coverage import (
    CoverageError,
    Counts,
    main,
    measure,
    select_report,
    source_name,
)


def report(classes, other_packages=""):
    return (
        '<?xml version="1.0"?><coverage line-rate="1" lines-covered="99999" lines-valid="99999">'
        '<packages><package name="JellySin.Plugin.Lastfm"><classes>'
        + classes
        + "</classes></package>"
        + other_packages
        + "</packages></coverage>"
    )


def source(filename, hits):
    lines = "".join(
        f'<line number="{index}" hits="{value}"/>'
        for index, value in enumerate(hits, 1)
    )
    return f'<class name="Fixture" filename="{filename}"><lines>{lines}</lines></class>'


class CoverageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def write(self, name, xml):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(xml, encoding="utf-8")
        return path

    def test_real_line_counts_ignore_forged_summary_and_deduplicate_nested_classes(
        self,
    ):
        classes = source("Api/Caller.cs", [1, 0]) + source("Api\\Caller.cs", [0, 1])
        classes += source(
            "C:\\repo\\src\\JellySin.Plugin.Lastfm\\Configuration\\Account.cs", [1]
        )
        classes += source("Transport/Client.cs", [1, 0])
        classes += source("Features/Music.cs", [0, 0])
        unrelated = (
            '<package name="JellySin.Plugin.Lastfm.Tests"><classes>'
            + source("Api/Tests.cs", [1] * 100)
            + "</classes></package>"
        )
        result = measure(
            self.write("coverage.cobertura.xml", report(classes, unrelated))
        )
        self.assertEqual(Counts(4, 7), result.overall)
        self.assertEqual(Counts(4, 5), result.security)
        with self.assertRaises(CoverageError):
            result.require_thresholds()

    def test_generated_production_code_is_not_excluded(self):
        classes = source("obj/Release/Regex.g.cs", [0])
        classes += "".join(
            source(area + "/Example.cs", [1])
            for area in ("Api", "Configuration", "Transport")
        )
        result = measure(self.write("coverage.cobertura.xml", report(classes)))
        self.assertEqual(Counts(3, 4), result.overall)
        result.require_thresholds()

    def test_latest_run_wins_without_combining_earlier_hits(self):
        full = "".join(
            source(area + "/Example.cs", [1])
            for area in ("Api", "Configuration", "Transport")
        )
        empty = "".join(
            source(area + "/Example.cs", [0])
            for area in ("Api", "Configuration", "Transport")
        )
        older = self.write("old/coverage.cobertura.xml", report(full))
        newer = self.write("new/coverage.cobertura.xml", report(empty))
        os.utime(older, ns=(100, 100))
        os.utime(newer, ns=(200, 200))
        self.assertEqual(newer, select_report(self.root))
        self.assertEqual(Counts(0, 3), measure(select_report(self.root)).overall)
        self.assertEqual(older, select_report(older))

    def test_tied_report_timestamps_choose_one_deterministically(self):
        for name in ("a", "b"):
            path = self.write(
                name + "/coverage.cobertura.xml", report(source("Api/Example.cs", [1]))
            )
            os.utime(path, ns=(100, 100))
        self.assertEqual("b", select_report(self.root).parent.name)

    def test_missing_security_area_fails_even_when_global_percentage_is_high(self):
        path = self.write(
            "coverage.cobertura.xml", report(source("Features/Music.cs", [1] * 100))
        )
        with self.assertRaisesRegex(CoverageError, "Missing security"):
            measure(path).require_thresholds()

    def test_exact_integer_thresholds_do_not_round_up(self):
        self.assertTrue(Counts(70, 100).meets(70))
        self.assertTrue(Counts(85, 100).meets(85))
        self.assertFalse(Counts(6999, 10000).meets(70))
        self.assertFalse(Counts(8499, 10000).meets(85))
        self.assertFalse(Counts(0, 0).meets(0))

    def test_no_inventory_or_wrong_package_fails(self):
        for xml in (
            report(""),
            '<coverage><packages><package name="Tests"/></packages></coverage>',
            report('<class filename="Api/Caller.cs"/>'),
        ):
            with self.subTest(xml=xml), self.assertRaises(CoverageError):
                measure(self.write("bad.cobertura.xml", xml))

    def test_malformed_xml_and_entities_are_rejected(self):
        for xml in (
            "<coverage>",
            "<test/>",
            '<!DOCTYPE coverage [<!ENTITY a "x">]><coverage>&a;</coverage>',
            report(
                '<class filename="Api/Caller.cs"><lines><line number="0" hits="1"/></lines></class>'
            ),
            report(
                '<class filename="Api/Caller.cs"><lines><line number="1" hits="-1"/></lines></class>'
            ),
        ):
            with self.subTest(xml=xml), self.assertRaises(CoverageError):
                measure(self.write("bad.cobertura.xml", xml))

    def test_source_paths_cannot_escape_or_hide_the_security_area(self):
        self.assertEqual(
            "api/caller.cs", source_name("/_/src/JellySin.Plugin.Lastfm/Api/Caller.cs")
        )
        self.assertEqual("api/caller.cs", source_name("./Api/Caller.cs"))
        for filename in (
            "../Api/Caller.cs",
            "/other/Api/Caller.cs",
            "Api/Caller.txt",
            "",
            "Api/Caller.cs\n",
        ):
            with self.subTest(filename=filename), self.assertRaises(CoverageError):
                source_name(filename)

    def test_report_and_discovery_limits_fail_closed(self):
        report_path = self.write(
            "coverage.cobertura.xml", report(source("Api/Caller.cs", [1]))
        )
        with (
            patch("tools.coverage.MAX_REPORT_BYTES", 1),
            self.assertRaises(CoverageError),
        ):
            measure(report_path)
        with patch("tools.coverage.MAX_LINES", 0), self.assertRaises(CoverageError):
            measure(report_path)
        with patch("tools.coverage.MAX_REPORTS", 0), self.assertRaises(CoverageError):
            select_report(self.root)
        with (
            patch("tools.coverage.MAX_DIRECTORIES", 0),
            self.assertRaises(CoverageError),
        ):
            select_report(self.root)

    def test_absent_reports_and_nonzero_cli_failure(self):
        with self.assertRaises(CoverageError):
            select_report(self.root)
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, main([str(self.root / "missing")]))
        classes = "".join(
            source(area + "/Example.cs", [1])
            for area in ("Api", "Configuration", "Transport")
        )
        path = self.write("coverage.cobertura.xml", report(classes))
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, main([str(path)]))


if __name__ == "__main__":
    unittest.main()
