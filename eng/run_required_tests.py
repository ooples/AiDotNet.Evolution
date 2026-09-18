"""Run unittest discovery and fail when a required gate is skipped.

Every skip decorator in these suites is gated purely on an environment variable that CI
supplies (the sandbox image id, the built comparison host, the pinned OpenEvolve checkout).
A skip therefore never means "not applicable here" -- it means the gate did not run and the
job is about to report success without having tested anything. Plain "unittest discover"
exits 0 in that case, so the skip has to be turned into a failure explicitly.
"""
import sys
import unittest


def main(argv):
    if len(argv) < 3:
        raise SystemExit("Usage: run_required_tests.py <start-directory> <pattern> [pattern ...]")
    start, patterns = argv[1], argv[2:]
    loader = unittest.TestLoader()
    suite = unittest.TestSuite(loader.discover(start, pattern=pattern) for pattern in patterns)
    if loader.errors:
        for error in loader.errors:
            print("::error::test discovery failed: " + str(error))
        return 1
    if suite.countTestCases() == 0:
        print("::error::no tests discovered in %s for %s" % (start, ", ".join(patterns)))
        return 1
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if result.skipped:
        for test, reason in result.skipped:
            print("::error::required gate skipped: %s (%s)" % (test, reason))
        return 1
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
