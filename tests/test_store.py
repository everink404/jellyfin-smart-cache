import hashlib
from pathlib import Path
import shutil
import unittest
import uuid
import threading
from unittest.mock import patch

from jellycache.store import CacheStore, CapacityError


class CacheTests(unittest.TestCase):
    def setUp(self):
        workspace = Path(__file__).resolve().parents[1]
        self.base = workspace / (".cache-test-" + uuid.uuid4().hex)
        self.base.mkdir()
        self.source = self.base / "Cloud Sync" / "video"
        self.source.mkdir(parents=True)
        for name in ("E01.mkv", "E02.mkv", "E03.mkv"):
            (self.source / name).write_bytes(name.encode() * 8)
        self.size = (self.source / "E01.mkv").stat().st_size
        self.cache = self.base / "cache"
        self.store = CacheStore(self.source, self.cache, self.size * 2)

    def tearDown(self):
        self.store.close()
        assert self.base.resolve().is_relative_to(Path(__file__).resolve().parents[1])
        shutil.rmtree(self.base)

    def test_miss_hit_seek_and_dedup(self):
        with self.store.open_media("E01.mkv") as (f, origin):
            self.assertEqual(origin, "source")
            self.assertEqual(f.read(), (self.source / "E01.mkv").read_bytes())
        self.assertEqual(self.store.prefetch("E01.mkv"), "complete")
        self.assertEqual(self.store.prefetch("E01.mkv"), "complete")
        self.assertEqual(len(list(self.cache.glob("*.blob"))), 1)
        with self.store.open_media("E01.mkv") as (f, origin):
            self.assertEqual(origin, "cache")
            f.seek(7)
            self.assertEqual(f.read(9), (self.source / "E01.mkv").read_bytes()[7:16])

    def test_occupied_file_survives_lru(self):
        self.store.prefetch("E01.mkv")
        self.store.prefetch("E02.mkv")
        with self.store.open_media("E01.mkv") as (first, _):
            with self.store.open_media("E02.mkv"):
                with self.assertRaises(CapacityError):
                    self.store.prefetch("E03.mkv")
            self.store.prefetch("E03.mkv")
            self.assertTrue(first.read())
        with self.store.open_media("E02.mkv") as (_, origin):
            self.assertEqual(origin, "source")

    def test_source_change_and_corruption_fall_back(self):
        self.store.prefetch("E01.mkv")
        blob = next(self.cache.glob("*.blob"))
        blob.write_bytes(b"x" * self.size)
        with self.store.open_media("E01.mkv") as (f, origin):
            self.assertEqual(origin, "source")
            self.assertNotEqual(f.read(), b"x" * self.size)
        self.store.prefetch("E01.mkv")
        (self.source / "E01.mkv").write_bytes(b"new source")
        with self.store.open_media("E01.mkv") as (f, origin):
            self.assertEqual(origin, "source")
            self.assertEqual(f.read(), b"new source")

    def test_recovery_only_cleans_indexed_partial(self):
        self.store.prefetch("E01.mkv")
        key = hashlib.sha256(b"E02.mkv").hexdigest()
        self.store.db.execute("INSERT INTO entries VALUES (?,?,?,?,?,?,?)",
                              (key, "E02.mkv", self.size, 0, None, 0, "copying"))
        self.store.db.commit()
        (self.cache / (key + ".part")).write_bytes(b"partial")
        unknown = self.cache / "unknown.part"
        unknown.write_bytes(b"keep")
        self.store.close()
        self.store = CacheStore(self.source, self.cache, self.size * 2)
        self.assertFalse((self.cache / (key + ".part")).exists())
        self.assertEqual(unknown.read_bytes(), b"keep")
        with self.store.open_media("E01.mkv") as (_, origin):
            self.assertEqual(origin, "cache")

    def test_refuse_unsafe_paths_and_unowned_cache(self):
        with self.assertRaises(ValueError):
            self.store.prefetch("../outside.mkv")
        with self.assertRaises(ValueError):
            CacheStore(self.source, self.source / "cache", 100)
        other = self.base / "existing"
        other.mkdir()
        (other / "keep.txt").write_text("keep")
        with self.assertRaises(ValueError):
            CacheStore(self.source, other, 100)
        self.assertEqual((other / "keep.txt").read_text(), "keep")

    def test_second_owner_refused(self):
        with self.assertRaises(RuntimeError):
            CacheStore(self.source, self.cache, self.size * 2)

    def test_oversized_media_keeps_source(self):
        (self.source / "big.mkv").write_bytes(b"x" * (self.size * 3))
        with self.assertRaises(CapacityError):
            self.store.prefetch("big.mkv")
        with self.store.open_media("big.mkv") as (_, origin):
            self.assertEqual(origin, "source")

    def test_disk_safety_margin_keeps_source(self):
        with patch("jellycache.store.shutil.disk_usage") as usage:
            usage.return_value.free = 0
            with self.assertRaises(CapacityError):
                self.store.prefetch("E01.mkv")
        self.assertEqual(list(self.cache.glob("*.blob")), [])

    def test_failure_during_copy_removes_partial(self):
        with patch("jellycache.store.os.fsync", side_effect=OSError("simulated write failure")):
            with self.assertRaises(OSError):
                self.store.prefetch("E01.mkv")
        self.assertEqual(list(self.cache.glob("*.part")), [])
        self.assertEqual(list(self.cache.glob("*.blob")), [])
        with self.store.open_media("E01.mkv") as (_, origin):
            self.assertEqual(origin, "source")

    def test_cache_read_error_falls_back(self):
        self.store.prefetch("E01.mkv")
        self.store._verified.clear()  # Model a cold verification after restart.
        with patch("jellycache.store.hashlib.file_digest", side_effect=OSError("simulated cache read error")):
            with self.store.open_media("E01.mkv") as (f, origin):
                self.assertEqual(origin, "source")
                self.assertTrue(f.read())

    def test_incomplete_copy_does_not_block_source_read(self):
        reached = threading.Event()
        release = threading.Event()
        errors = []

        def hold_completion(_):
            reached.set()
            if not release.wait(5):
                raise TimeoutError("test completion gate")

        def copy():
            try:
                self.store.prefetch("E01.mkv")
            except Exception as error:
                errors.append(error)

        with patch("jellycache.store.os.fsync", side_effect=hold_completion):
            task = threading.Thread(target=copy)
            task.start()
            try:
                self.assertTrue(reached.wait(5))
                self.assertEqual(self.store.prefetch("E01.mkv"), "busy")
                with self.store.open_media("E01.mkv") as (f, origin):
                    self.assertEqual(origin, "source")
                    self.assertTrue(f.read())
            finally:
                release.set()
                task.join(5)
        self.assertFalse(task.is_alive())
        self.assertEqual(errors, [])
        with self.store.open_media("E01.mkv") as (_, origin):
            self.assertEqual(origin, "cache")

    def test_source_changed_during_copy_is_not_published(self):
        identity = self.store._identity(self.source / "E01.mkv")
        with patch.object(self.store, "_identity", side_effect=[identity, (identity[0], identity[1] + 1)]):
            with self.assertRaises(OSError):
                self.store.prefetch("E01.mkv")
        self.assertEqual(list(self.cache.glob("*.blob")), [])
        self.assertEqual(list(self.cache.glob("*.part")), [])

    def test_unindexed_collision_is_preserved(self):
        key = hashlib.sha256(b"E01.mkv").hexdigest()
        part = self.cache / (key + ".part")
        blob = self.cache / (key + ".blob")
        part.write_bytes(b"unknown partial")
        blob.write_bytes(b"unknown complete")
        with self.assertRaises(ValueError):
            self.store.prefetch("E01.mkv")
        self.assertEqual(part.read_bytes(), b"unknown partial")
        self.assertEqual(blob.read_bytes(), b"unknown complete")


if __name__ == "__main__":
    unittest.main()
