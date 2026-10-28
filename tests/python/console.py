"""Capture standard output and standard error around a console entry point."""

import contextlib
import io


def run(main, arguments):
    output, errors = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
        code = main(arguments)
    return code, output.getvalue(), errors.getvalue()
