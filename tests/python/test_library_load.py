"""The real-library load check refuses a p95 over its budget and a request kind it never measured."""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from library_load import P95_BUDGET_MILLISECONDS, summarize  # noqa: E402
from smoke import SmokeFailure  # noqa: E402


def timings(slow: int, value: float) -> list[float]:
    """100 samples of 10 ms with the slowest `slow` set to `value`; index 95 of the sorted list is the p95."""
    return [10.0] * (100 - slow) + [value] * slow


class SummarizeTests(unittest.TestCase):
    def test_percentiles_within_the_budget_pass(self):
        metrics = summarize([("playbackHttp", timings(1, 2000.0)), ("pluginSearchHttp", timings(5, 900.0))])
        self.assertEqual(metrics["playbackHttp"], {"samples": 100, "p50Milliseconds": 10.0, "p95Milliseconds": 10.0, "p99Milliseconds": 2000.0})
        self.assertEqual(metrics["pluginSearchHttp"]["p95Milliseconds"], 900.0)

    def test_a_p95_at_the_budget_passes(self):
        metrics = summarize([("playbackHttp", timings(5, P95_BUDGET_MILLISECONDS)), ("pluginSearchHttp", timings(5, P95_BUDGET_MILLISECONDS))])
        self.assertEqual(metrics["playbackHttp"]["p95Milliseconds"], P95_BUDGET_MILLISECONDS)

    def test_a_planted_p95_over_the_budget_fails_for_either_kind(self):
        slow = timings(5, P95_BUDGET_MILLISECONDS + 0.1)
        for results in ([("playbackHttp", slow), ("pluginSearchHttp", timings(0, 10.0))],
                        [("playbackHttp", timings(0, 10.0)), ("pluginSearchHttp", slow)]):
            with self.subTest(slow=results[0][1] is slow), self.assertRaisesRegex(SmokeFailure, "p95 budget of 1000 ms was exceeded"):
                summarize(results)

    def test_samples_from_several_workers_are_pooled(self):
        metrics = summarize([("playbackHttp", [10.0] * 50), ("playbackHttp", [900.0] * 50), ("pluginSearchHttp", [10.0] * 100)])
        self.assertEqual((metrics["playbackHttp"]["samples"], metrics["playbackHttp"]["p95Milliseconds"]), (100, 900.0))

    def test_a_kind_without_samples_fails(self):
        with self.assertRaisesRegex(SmokeFailure, "measured no pluginSearchHttp requests"):
            summarize([("playbackHttp", timings(0, 10.0)), ("pluginSearchHttp", [])])


if __name__ == "__main__":
    unittest.main()
