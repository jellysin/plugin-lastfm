"""Release test level run bindings must match normal steps only, never planted steps."""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STANDARDS = ROOT / ".standards.yaml"
CI = ROOT / ".github" / "workflows" / "ci.yml"


def levels(text):
    result = {}
    in_levels = False
    cur_level, job, run = None, None, None
    for line in text.splitlines():
        if re.match(r"^  test_levels:\s*$", line):
            in_levels = True
        elif in_levels and (
            re.match(r"^[^\s#]", line)
            or (re.match(r"^ {1,2}\S", line) and not line.startswith("  #"))
        ):
            break
        elif in_levels and re.match(r"^ {4}([a-zA-Z0-9_]+):\s*$", line):
            if cur_level and job and run:
                result[cur_level] = (job, run)
            cur_level = re.match(r"^ {4}([a-zA-Z0-9_]+):\s*$", line).group(1)
            job, run = None, None
        elif in_levels and re.match(r"^ {6}job:\s*[\"']([^\"']+)[\"']", line):
            job = re.match(r"^ {6}job:\s*[\"']([^\"']+)[\"']", line).group(1)
        elif in_levels and re.match(r"^ {6}run:\s*[\"']([^\"']+)[\"']", line):
            run = re.match(r"^ {6}run:\s*[\"']([^\"']+)[\"']", line).group(1)
    if cur_level and job and run:
        result[cur_level] = (job, run)
    return result


def _finish_step(acc, job_id, job_name, run_lines):
    if run_lines is not None:
        acc.append((job_id, job_name, "\n".join(run_lines)))


def _handle_run_line(line, run_lines, multi):
    if multi:
        if line.startswith("          ") or not line.strip():
            run_lines.append(line[10:] if len(line) >= 10 else "")
            return True
    if line.startswith("        run:"):
        val = line.split("run:", 1)[1].strip()
        if val.startswith("|"):
            return True
        run_lines.append(val)
    return False


def steps(text):
    result = []
    job_id, job_name, run_lines, multi = None, None, None, False
    in_jobs = False
    for line in text.splitlines():
        if line.startswith("jobs:"):
            in_jobs = True
        elif in_jobs and re.match(r"^ {2}([a-zA-Z0-9_-]+):\s*$", line):
            _finish_step(result, job_id, job_name, run_lines)
            job_id = job_name = line.strip()[:-1]
            run_lines, multi = None, False
        elif in_jobs and re.match(r"^ {4}name:\s*", line) and run_lines is None:
            job_name = line.split("name:", 1)[1].strip().strip("'\"")
        elif in_jobs and line.startswith("      - "):
            _finish_step(result, job_id, job_name, run_lines)
            run_lines = []
            multi = False
        elif in_jobs and run_lines is not None:
            multi = _handle_run_line(line, run_lines, multi)
    _finish_step(result, job_id, job_name, run_lines)
    return result


def planted(run_text):
    return "--plant" in run_text or "tools/expect_refusal.py" in run_text


def proving_steps(level_job, level_run, all_steps):
    return [
        step
        for step in all_steps
        if (step[0] == level_job or step[1] == level_job) and level_run in step[2]
    ]


# Bare script paths bound before review of pull request 266; each also matched a planted step.
OLD_RUNS = {
    "e2e_settings_entry": "tests/integration/settings_entry.py",
    "recovery_restore": "tests/integration/state_carryover.py",
    "recovery_restore_catalog": "tests/integration/catalog_over_canary.py",
}


class ReleaseLevelsTests(unittest.TestCase):
    def test_all_release_levels_have_non_planted_proving_steps_only(self):
        """Every level in .standards.yaml has >=1 proving step that is not planted, and 0 planted."""
        all_levels = levels(STANDARDS.read_text(encoding="utf-8"))
        all_steps = steps(CI.read_text(encoding="utf-8"))
        for level_name, (job, run) in all_levels.items():
            with self.subTest(level=level_name):
                prov = proving_steps(job, run, all_steps)
                normal = [s for s in prov if not planted(s[2])]
                planted_steps = [s for s in prov if planted(s[2])]
                self.assertGreaterEqual(
                    len(normal), 1, f"Level {level_name} must have >= 1 normal proving step"
                )
                self.assertEqual(
                    len(planted_steps), 0, f"Level {level_name} matched planted steps: {planted_steps}"
                )

    def test_planted_bare_paths_match_planted_steps(self):
        """Planted case: the old bare paths match a planted step, so the first test refuses them."""
        all_levels = levels(STANDARDS.read_text(encoding="utf-8"))
        all_steps = steps(CI.read_text(encoding="utf-8"))
        for level_name, old_run in OLD_RUNS.items():
            with self.subTest(level=level_name):
                job = all_levels[level_name][0]
                prov = proving_steps(job, old_run, all_steps)
                self.assertTrue(any(planted(s[2]) for s in prov), f"{old_run} matches no planted step")

    def test_dropping_normal_step_leaves_level_unproved(self):
        """Without its normal step no step, planted or not, carries the run text of a rebound level."""
        all_levels = levels(STANDARDS.read_text(encoding="utf-8"))
        ci_lines = CI.read_text(encoding="utf-8").splitlines()
        for level_name in OLD_RUNS:
            with self.subTest(level=level_name):
                job, run = all_levels[level_name]
                mutated = "\n".join(line for line in ci_lines if run not in line)
                self.assertEqual(proving_steps(job, run, steps(mutated)), [])

    def test_at_least_twelve_levels_parsed(self):
        """At least 12 levels parsed to guard against an empty parser."""
        all_levels = levels(STANDARDS.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(all_levels), 12)


if __name__ == "__main__":
    unittest.main()
