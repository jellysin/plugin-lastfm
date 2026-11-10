"""The Ruff gate reads one pinned release, scans only Python files and verifies the Codacy pattern set."""

from datetime import date
import io
from pathlib import Path
import sys
import tempfile
import tomllib
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
import ruff_gate  # noqa: E402 - module imported after sys.path manipulation
from ruff_gate import (  # noqa: E402 - module imported after sys.path manipulation
    GateError,
    exception_problems,
    launcher,
    pinned_version,
)
from semgrep_gate import MAX_TARGETS  # noqa: E402 - module imported after sys.path manipulation

ENTRY = '".config/lefthook/scripts/common.py" = ["S603"]'
NOTE = "# expires 2026-11-20 (MEL C, entered 2026-11-10): rendered by Praetor"
PY = frozenset({".py"})


def config(*lines: str) -> str:
    """Return ruff.toml text with one select code and the given per-file-ignores lines."""
    return "\n".join(['[lint]', 'select = ["S603"]', "", "[lint.per-file-ignores]", *lines, ""])


class PinTests(unittest.TestCase):
    """The requirements file pins exactly one Ruff release with `==`."""

    def test_committed_requirements_pin_one_release(self):
        """The committed file pins a plain major.minor.patch release."""
        version = pinned_version(
            ruff_gate.REQUIREMENTS.read_text(encoding="utf-8"),
            package="ruff",
            source=ruff_gate.REQUIREMENTS,
        )
        self.assertRegex(version, r"^\d+\.\d+\.\d+$")

    def test_refuses_package_mismatch(self):
        """A pin for another package does not count as a Ruff pin."""
        with self.assertRaises(GateError):
            pinned_version("semgrep==1.183.0\n", package="ruff", source=ruff_gate.REQUIREMENTS)

    def test_refuses_two_pins(self):
        """Two Ruff pins are refused rather than one picked."""
        with self.assertRaises(GateError):
            pinned_version("ruff==0.17.4\nruff==0.17.5\n", package="ruff", source=ruff_gate.REQUIREMENTS)

    def test_refuses_a_missing_pin(self):
        """A range is not a pin."""
        with self.assertRaises(GateError):
            pinned_version("ruff>=0.17.0\n", package="ruff", source=ruff_gate.REQUIREMENTS)


class TargetTests(unittest.TestCase):
    """Only repository Python files reach Ruff, and at most MAX_TARGETS of them."""

    def test_keeps_py_only(self):
        """Files without the .py suffix are dropped, order kept."""
        paths = ["tools/ruff_gate.py", "README.md", "src/Plugin.cs", "tests/python/test_ruff_gate.py", "web/app.js"]
        self.assertEqual(ruff_gate.scanned(paths, suffixes=PY), ["tools/ruff_gate.py", "tests/python/test_ruff_gate.py"])

    def test_empty_list_returns_empty(self):
        """No paths give no targets."""
        self.assertEqual(ruff_gate.scanned([], suffixes=PY), [])

    def test_refuses_a_path_outside_the_repository(self):
        """A path that resolves outside the repository is refused, relative or absolute."""
        for path in ("../outside.py", "/etc/passwd.py", "tools/../../outside.py"):
            with self.subTest(path=path), self.assertRaises(GateError):
                ruff_gate.scanned(["tools/ruff_gate.py", path], suffixes=PY)

    def test_refuses_more_than_the_bound(self):
        """One path over the bound is refused."""
        with self.assertRaises(GateError):
            ruff_gate.scanned(["a.py"] * (MAX_TARGETS + 1), suffixes=PY)

    def test_accepts_exactly_the_bound(self):
        """Exactly MAX_TARGETS paths are accepted."""
        targets = ruff_gate.scanned(["a.py"] * MAX_TARGETS, suffixes=PY)
        self.assertEqual(len(targets), MAX_TARGETS)


class ConfigTests(unittest.TestCase):
    """ruff.toml is the only rule source and holds a plain, sorted pattern list."""

    def test_ruff_toml_structure(self):
        """The select list is non-empty, sorted and unique, with no ignore or extend-select keys."""
        data = tomllib.loads(ruff_gate.CONFIG.read_text(encoding="utf-8"))
        lint = data.get("lint", {})
        select = lint.get("select", [])
        self.assertTrue(select)
        self.assertEqual(select, sorted(select))
        self.assertEqual(len(select), len(set(select)))
        self.assertNotIn("ignore", lint)
        self.assertNotIn("extend-select", lint)
        self.assertNotIn("extend-per-file-ignores", lint)

    def test_committed_exceptions_are_dated_and_current(self):
        """Every committed per-file exception carries a note and has not expired."""
        self.assertEqual(exception_problems(ruff_gate.CONFIG.read_text(encoding="utf-8"), date.today()), [])


class ExceptionTests(unittest.TestCase):
    """A per-file exception needs a dated note on the line above it and fails after that date."""

    def test_accepts_a_dated_exception_before_expiry(self):
        """A noted exception is valid before its date."""
        self.assertEqual(exception_problems(config(NOTE, ENTRY), date(2026, 11, 10)), [])

    def test_accepts_an_exception_on_its_expiry_day(self):
        """The expiry day itself is still valid."""
        self.assertEqual(exception_problems(config(NOTE, ENTRY), date(2026, 11, 20)), [])

    def test_refuses_an_exception_the_day_after_expiry(self):
        """The day after the date, the exception is reported as expired."""
        problems = exception_problems(config(NOTE, ENTRY), date(2026, 11, 21))
        self.assertEqual(len(problems), 1)
        self.assertIn("expired on 2026-11-20", problems[0])

    def test_refuses_an_exception_without_a_note(self):
        """An exception with no note above it is reported."""
        problems = exception_problems(config("", ENTRY), date(2026, 11, 10))
        self.assertEqual(len(problems), 1)
        self.assertIn("without a", problems[0])

    def test_refuses_a_note_without_category_or_reason(self):
        """A note that lacks the MEL category or the reason is no note."""
        for note in ("# expires 2026-11-20: rendered by Praetor", "# expires 2026-11-20 (MEL C, entered 2026-11-10): "):
            with self.subTest(note=note):
                self.assertEqual(len(exception_problems(config(note, ENTRY), date(2026, 11, 10))), 1)

    def test_no_exceptions_give_no_problems(self):
        """A file without per-file-ignores has nothing to check."""
        self.assertEqual(exception_problems('[lint]\nselect = ["S603"]\n', date(2026, 11, 10)), [])

    def test_refuses_malformed_toml(self):
        """A configuration with TOML syntax errors raises GateError."""
        with self.assertRaises(GateError) as context:
            exception_problems("[lint\n", date(2026, 11, 10))
        self.assertIn("ruff.toml", str(context.exception))
        self.assertIn("not valid TOML", str(context.exception))

    def test_refuses_impossible_calendar_date(self):
        """An impossible calendar date in an exception note raises GateError."""
        note = "# expires 2026-13-40 (MEL B, entered 2026-11-10): reason"
        with self.assertRaises(GateError) as context:
            exception_problems(config(note, ENTRY), date(2026, 11, 10))
        self.assertIn("ruff.toml", str(context.exception))
        self.assertIn("2026-13-40", str(context.exception))

    def test_checks_leap_year_boundary(self):
        """February 29 is refused in a common year and accepted in a leap year."""
        non_leap = "# expires 2026-02-29 (MEL B, entered 2026-11-10): reason"
        with self.assertRaises(GateError) as context:
            exception_problems(config(non_leap, ENTRY), date(2026, 11, 10))
        self.assertIn("ruff.toml", str(context.exception))
        self.assertIn("2026-02-29", str(context.exception))
        leap = "# expires 2028-02-29 (MEL B, entered 2028-01-10): reason"
        self.assertEqual(exception_problems(config(leap, ENTRY), date(2028, 2, 29)), [])

    def test_main_fails_on_an_expired_exception(self):
        """The gate exits 1 before scanning when an exception has expired."""
        with tempfile.TemporaryDirectory() as directory:
            expired = Path(directory) / "ruff.toml"
            expired.write_text(config(NOTE, ENTRY), encoding="utf-8")
            with (
                mock.patch.object(ruff_gate, "CONFIG", expired),
                mock.patch.object(ruff_gate, "launcher", return_value=["ruff"]),
                mock.patch.object(ruff_gate, "date", wraps=date) as today,
                mock.patch.object(ruff_gate, "run") as run,
            ):
                today.today.return_value = date(2026, 11, 21)
                self.assertEqual(ruff_gate.main([]), 1)
                run.assert_not_called()


class LauncherTests(unittest.TestCase):
    """The launcher starts the pinned Ruff through uvx, or pipx when uvx is absent."""

    def test_prefers_uvx_and_pins_ruff(self):
        """uvx wins when both launchers exist."""
        with mock.patch("shutil.which", side_effect=lambda name: f"/bin/{name}"):
            self.assertEqual(
                launcher("1.2.3", package="ruff"),
                ["/bin/uvx", "--quiet", "--python", "3.14", "--from", "ruff==1.2.3", "ruff"],
            )

    def test_falls_back_to_pipx_by_name(self):
        """pipx runs the same pin when uvx is missing."""
        with mock.patch("shutil.which", side_effect=lambda name: "/bin/pipx" if name == "pipx" else None):
            self.assertEqual(
                launcher("1.2.3", package="ruff"),
                ["/bin/pipx", "run", "--python", "3.14", "--fetch-missing-python", "--spec", "ruff==1.2.3", "ruff"],
            )

    def test_refuses_without_a_launcher(self):
        """No uvx and no pipx is a gate error that names Ruff."""
        with mock.patch("shutil.which", return_value=None), self.assertRaisesRegex(GateError, "to run ruff"):
            launcher("1.2.3", package="ruff")


class RunTests(unittest.TestCase):
    """The run helper runs Ruff check, bounds its report size and captures stderr on failure."""

    def test_oversized_report_raises_gate_error(self):
        """A report file larger than MAX_REPORT_BYTES raises GateError."""
        def fake_run(arguments, **kwargs):
            out_idx = arguments.index("--output-file") + 1
            Path(arguments[out_idx]).write_bytes(b"[" + b" " * 98 + b"]")
            return mock.Mock(returncode=0)

        with (
            mock.patch.object(ruff_gate, "MAX_REPORT_BYTES", 10),
            mock.patch("subprocess.run", side_effect=fake_run),
            self.assertRaisesRegex(GateError, "exceeds the limit of 10 bytes"),
        ):
            ruff_gate.run(["ruff"], ["tools/ruff_gate.py"])

    def test_exit_code_two_raises_gate_error_with_stderr(self):
        """Exit code 2 raises GateError carrying stderr text."""
        def fake_run(arguments, **kwargs):
            """Simulate a subprocess failure by writing to stderr and returning exit code 2."""
            stderr = kwargs.get("stderr")
            if stderr:
                stderr.write("unexpected failure in ruff check")
                stderr.flush()
            return mock.Mock(returncode=2)

        with (
            mock.patch("subprocess.run", side_effect=fake_run),
            self.assertRaisesRegex(GateError, "unexpected failure in ruff check"),
        ):
            ruff_gate.run(["ruff"], ["tools/ruff_gate.py"])


class MainTests(unittest.TestCase):
    """The gate's exit status separates findings from failures to run."""

    def test_returns_two_when_config_missing(self):
        """A missing ruff.toml is a failure to run, not a clean scan."""
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "ruff.toml"
            with (
                mock.patch.object(ruff_gate, "CONFIG", missing),
                mock.patch.object(ruff_gate, "launcher", return_value=["ruff"]),
            ):
                self.assertEqual(ruff_gate.main([]), 2)

    def test_main_bounds_findings_to_max_printed(self):
        """201 fake findings print 200 lines plus one summary line and return 1."""
        fake_findings = [
            {"filename": "a.py", "location": {"row": i, "column": 1}, "code": "E999", "message": "msg"}
            for i in range(1, 202)
        ]
        stderr = io.StringIO()
        with (
            mock.patch.object(ruff_gate, "launcher", return_value=["ruff"]),
            mock.patch.object(ruff_gate, "run", return_value=fake_findings),
            mock.patch.object(ruff_gate, "scanned", return_value=["a.py"]),
            mock.patch("sys.stderr", stderr),
        ):
            status = ruff_gate.main(["a.py"])
            self.assertEqual(status, 1)
        lines = [line for line in stderr.getvalue().splitlines() if line]
        self.assertEqual(len(lines), 201)
        self.assertEqual(lines[-1], "ruff-gate: 1 more findings not shown")


class BoundedReadTests(unittest.TestCase):
    """Files read under a size bound are accepted at the bound and refused when larger."""

    def test_read_bounded_refuses_one_byte_over_and_accepts_at_limit(self):
        """read_bounded refuses a file one byte over its limit and accepts one at the limit."""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.txt"
            path.write_bytes(b"x" * 10)
            self.assertEqual(ruff_gate.read_bounded(path, 10), "x" * 10)
            with self.assertRaises(GateError):
                ruff_gate.read_bounded(path, 9)


if __name__ == "__main__":
    unittest.main()
