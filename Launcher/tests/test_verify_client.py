import hashlib
import io
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import verify_client


class VerifyClientTests(unittest.TestCase):
    def test_manifest_rejects_path_traversal(self):
        with self.assertRaisesRegex(verify_client.VerificationError, "Ruta inválida"):
            verify_client.parse_manifest(
                {
                    "files": {
                        "../outside.jar": {
                            "sha256": "A" * 64,
                            "size": 1,
                        }
                    }
                }
            )

    def test_only_missing_or_different_size_files_need_repair(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            game_path = Path(temporary_directory)
            existing_file = game_path / "libraries" / "valid.jar"
            existing_file.parent.mkdir()
            existing_file.write_bytes(b"same-size")

            files = verify_client.parse_manifest(
                {
                    "files": {
                        "libraries/valid.jar": {
                            "sha256": "A" * 64,
                            "size": len(b"same-size"),
                        },
                        "libraries/missing.jar": {
                            "sha256": "B" * 64,
                            "size": 1,
                        },
                        "mods/wrong-size.jar": {
                            "sha256": "C" * 64,
                            "size": 5,
                        },
                    }
                }
            )
            wrong_size_file = game_path / "mods" / "wrong-size.jar"
            wrong_size_file.parent.mkdir()
            wrong_size_file.write_bytes(b"x")

            repair_paths = {file.url_path for file in verify_client.find_files_to_repair(game_path, files)}

            self.assertEqual(repair_paths, {"libraries/missing.jar", "mods/wrong-size.jar"})

    def test_downloaded_file_requires_expected_sha256(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            path = Path(temporary_directory) / "file.bin"
            path.write_bytes(b"verified")

            self.assertEqual(
                verify_client.sha256_file(path),
                hashlib.sha256(b"verified").hexdigest().upper(),
            )

    def test_download_reports_accumulated_bytes(self):
        class FakeResponse(io.BytesIO):
            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, traceback):
                self.close()

        content = b"x" * (1024 * 1024 + 123)
        progress = []

        with tempfile.TemporaryDirectory() as temporary_directory:
            destination = Path(temporary_directory) / "download.bin"
            with patch("verify_client.urlopen", return_value=FakeResponse(content)):
                verify_client.download_file(
                    "http://example.test/download.bin",
                    destination,
                    hashlib.sha256(content).hexdigest().upper(),
                    progress.append,
                )

        self.assertEqual(progress, [1024 * 1024, len(content)])

    def test_cleanup_only_removes_unlisted_files_in_protected_folders(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            game_path = Path(temporary_directory)
            allowed = game_path / "mods" / "allowed.jar"
            unlisted = game_path / "mods" / "unlisted.jar"
            outside_scope = game_path / "config" / "keep.json"
            for path in (allowed, unlisted, outside_scope):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("content")

            removed = verify_client.remove_unauthorized_files(game_path, {Path("mods/allowed.jar")})

            self.assertEqual(removed, 1)
            self.assertTrue(allowed.exists())
            self.assertFalse(unlisted.exists())
            self.assertTrue(outside_scope.exists())


if __name__ == "__main__":
    unittest.main()
