import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

import launcher_core


class LauncherCoreTests(unittest.TestCase):
    def test_get_game_path_returns_existing_game_directory(self):
        game_path = launcher_core.get_game_path()

        self.assertTrue(os.path.isdir(game_path))
        self.assertTrue(os.path.exists(os.path.join(game_path, "config.json")))


if __name__ == "__main__":
    unittest.main()
