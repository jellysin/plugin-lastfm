"""The Ruff gate ran in CI only, so the local Makefile gate passed a change CI failed."""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
CI_WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
MAKEFILE = ROOT / "Makefile"
GATE_PATTERN = re.compile(r"^python3 tools/[a-zA-Z0-9_-]+_gate\.py(?:\s+.*)?$")


def ci_gate_commands(workflow_text):
    """Return gate tool commands invoked across workflow run lines."""
    commands = set()
    for raw in workflow_text.splitlines():
        line = raw.strip()
        if line.startswith("- "):
            line = line[2:].strip()
        if line.startswith("run:"):
            line = line[4:].strip()
        if GATE_PATTERN.match(line):
            commands.add(line)
    return commands


def make_recipe_commands(makefile_text):
    """Return tab-indented recipe lines with execution prefixes removed."""
    commands = set()
    for line in makefile_text.splitlines():
        if not line.startswith("\t"):
            continue
        cmd = line[1:].strip()
        if cmd.startswith("#"):
            continue
        if cmd.startswith("@"):
            cmd = cmd[1:].strip()
        if cmd.startswith("exec "):
            cmd = cmd[5:].strip()
        if cmd:
            commands.add(cmd)
    return commands


def missing_local(workflow_text, makefile_text):
    """Return sorted CI gate commands absent from the recipe set."""
    ci_commands = ci_gate_commands(workflow_text)
    recipe_commands = make_recipe_commands(makefile_text)
    return sorted(ci_commands - recipe_commands)


class GateParityTests(unittest.TestCase):
    """Every gate command the CI workflow runs also runs in a Makefile recipe."""

    def test_repository_parity(self):
        """Repository CI workflow gate commands must all exist in the Makefile."""
        ci_text = CI_WORKFLOW.read_text(encoding="utf-8")
        makefile_text = MAKEFILE.read_text(encoding="utf-8")
        self.assertEqual(missing_local(ci_text, makefile_text), [])

    def test_planted_missing_ruff_reports_both_commands(self):
        """A Makefile missing both Ruff gate lines reports them as absent."""
        ci_text = CI_WORKFLOW.read_text(encoding="utf-8")
        makefile_text = MAKEFILE.read_text(encoding="utf-8")
        without_ruff = "\n".join(
            line for line in makefile_text.splitlines() if "ruff_gate.py" not in line
        )
        self.assertEqual(
            missing_local(ci_text, without_ruff),
            ["python3 tools/ruff_gate.py", "python3 tools/ruff_gate.py --self-test"],
        )

    def test_planted_workflow_without_gate_command_gives_empty(self):
        """A workflow containing no gate commands produces an empty missing list."""
        makefile_text = MAKEFILE.read_text(encoding="utf-8")
        self.assertEqual(missing_local("name: Minimal\njobs:\n", makefile_text), [])

    def test_recipe_prefix_handling(self):
        """Recipes with @exec prefix or plain commands are both recognized."""
        workflow = "run: python3 tools/ruff_gate.py\n"
        self.assertEqual(
            missing_local(workflow, "\t@exec python3 tools/ruff_gate.py\n"), []
        )
        self.assertEqual(
            missing_local(workflow, "\tpython3 tools/ruff_gate.py\n"), []
        )

    def test_makefile_comment_does_not_count(self):
        """A gate command only present in a Makefile comment is not a recipe."""
        workflow = "run: python3 tools/x_gate.py\n"
        commented_makefile = "# python3 tools/x_gate.py\n\t# python3 tools/x_gate.py\n"
        self.assertEqual(
            missing_local(workflow, commented_makefile),
            ["python3 tools/x_gate.py"],
        )

    def test_ci_gate_commands_variations(self):
        """Extract gate commands across varied run line prefixes and ignore non-gates."""
        sample_workflow = (
            "      - run: python3 tools/alpha_gate.py\n"
            "        run: python3 tools/beta_gate.py --arg value\n"
            "      - name: static checks\n"
            "        run: |\n"
            "          python3 tools/gamma_gate.py --self-test\n"
            "          python tools/other.py\n"
            "          # python3 tools/delta_gate.py\n"
        )
        self.assertEqual(
            ci_gate_commands(sample_workflow),
            {
                "python3 tools/alpha_gate.py",
                "python3 tools/beta_gate.py --arg value",
                "python3 tools/gamma_gate.py --self-test",
            },
        )


if __name__ == "__main__":
    unittest.main()
