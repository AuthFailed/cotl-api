import unittest

import watch


class WatchTest(unittest.TestCase):
    def test_new_build_opens_an_issue(self):
        self.assertEqual(watch.issue_title(captured=100, public=200, existing=[]), "Game updated: Steam build 200")

    def test_same_build_does_nothing(self):
        self.assertIsNone(watch.issue_title(captured=200, public=200, existing=[]))

    def test_one_issue_per_build(self):
        self.assertIsNone(watch.issue_title(captured=100, public=200, existing=["Game updated: Steam build 200"]))

    def test_unknown_capture_build_does_nothing(self):
        self.assertIsNone(watch.issue_title(captured=None, public=200, existing=[]))


if __name__ == "__main__":
    unittest.main()
