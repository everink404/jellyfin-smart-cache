import errno
import hashlib
import os
from pathlib import Path
import shutil
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

from jellycache.filesystem import MediaFilesystem
from jellycache.mount import checked_mountpoint, fuse_operations
from jellycache.store import CacheStore, CapacityError


class FilesystemTests(unittest.TestCase):
    def setUp(self):
        self.workspace = Path(__file__).resolve().parents[1]
        self.base = self.workspace / (".cache-test-" + uuid.uuid4().hex)
        self.source = self.base / "Cloud Sync" / "video"
        self.source.mkdir(parents=True)
        self.series = self.source / "某剧" / "Season 01"
        self.series.mkdir(parents=True)
        self.data = bytes(range(256)) * 8
        self.relative = "某剧/Season 01/E02.mkv"
        self.path = "/" + self.relative
        for name in ("E02.mkv", "E03.mkv"):
            (self.series / name).write_bytes(self.data)
        (self.series / "E02.srt").write_text("subtitle", encoding="utf-8")
        self.cache = self.base / "cache"
        self.store = CacheStore(self.source, self.cache, len(self.data))
        self.fs = MediaFilesystem(self.store)

    def tearDown(self):
        self.fs.close()
        self.store.close()
        assert self.base.resolve().is_relative_to(self.workspace)
        shutil.rmtree(self.base)

    def test_same_path_new_open_changes_backing_and_metadata_stays_stable(self):
        before = self.fs.getattr(self.path)
        old = self.fs.open(self.path, os.O_RDONLY)
        self.assertEqual(self.fs.origin(old), "source")
        self.store.prefetch(self.relative)
        new = self.fs.open(self.path, os.O_RDONLY)
        self.assertEqual(self.fs.origin(new), "cache")
        self.assertEqual(self.fs.origin(old), "source")
        self.assertEqual(self.fs.getattr(self.path)["st_ino"], before["st_ino"])
        for handle in (old, new):
            self.assertEqual(self.fs.read(self.path, 99, 543, handle), self.data[543:642])
            self.assertEqual(self.fs.read(self.path, 99, len(self.data), handle), b"")
            self.fs.release(self.path, handle)

    def test_lease_protects_cache_until_release(self):
        self.store.prefetch(self.relative)
        handle = self.fs.open(self.path, os.O_RDONLY)
        with self.assertRaises(CapacityError):
            self.store.prefetch("某剧/Season 01/E03.mkv")
        self.fs.release(self.path, handle)
        self.store.prefetch("某剧/Season 01/E03.mkv")
        new = self.fs.open(self.path, os.O_RDONLY)
        self.assertEqual(self.fs.origin(new), "source")
        self.fs.release(self.path, new)

    def test_nested_listing_subtitle_and_read_only_flags(self):
        names = self.fs.readdir("/某剧/Season 01")
        self.assertIn("E02.mkv", names)
        self.assertIn("E02.srt", names)
        subtitle = self.fs.open("/某剧/Season 01/E02.srt", os.O_RDONLY)
        self.assertEqual(self.fs.read("", 20, 0, subtitle), b"subtitle")
        self.fs.release("", subtitle)
        for flags in (os.O_WRONLY, os.O_RDWR, os.O_RDONLY | os.O_TRUNC,
                      os.O_RDONLY | os.O_CREAT, os.O_RDONLY | os.O_APPEND):
            with self.assertRaises(OSError) as error:
                self.fs.open(self.path, flags)
            self.assertEqual(error.exception.errno, errno.EROFS)
        with self.assertRaises(OSError):
            self.fs.access(self.path, os.W_OK)
        self.assertFalse(self.fs.getattr(self.path)["st_mode"] & 0o222)

    def test_traversal_missing_and_bad_handles(self):
        for path in ("/../outside", "relative", "/某剧/../../outside", "/bad\\path"):
            with self.assertRaises(OSError):
                self.fs.getattr(path)
        with self.assertRaises(FileNotFoundError):
            self.fs.getattr("/missing")
        with self.assertRaises(OSError) as error:
            self.fs.read(self.path, 10, 0, 999)
        self.assertEqual(error.exception.errno, errno.EBADF)

    def test_parallel_offsets_share_a_handle_safely(self):
        handle = self.fs.open(self.path, os.O_RDONLY)
        errors = []

        def read_at(offset):
            try:
                for _ in range(100):
                    if self.fs.read(self.path, 31, offset, handle) != self.data[offset:offset + 31]:
                        errors.append(offset)
            except Exception as error:
                errors.append(error)

        threads = [threading.Thread(target=read_at, args=(offset,)) for offset in (0, 51, 512, 1024)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(5)
        self.assertFalse(any(t.is_alive() for t in threads))
        self.assertEqual(errors, [])
        self.fs.release(self.path, handle)

    def test_warm_opens_do_not_hash_entire_file(self):
        self.store.prefetch(self.relative)
        with patch("jellycache.store.hashlib.file_digest", side_effect=AssertionError("unexpected full scan")):
            for _ in range(3):
                handle = self.fs.open(self.path, os.O_RDONLY)
                self.assertEqual(self.fs.origin(handle), "cache")
                self.fs.release(self.path, handle)

    def test_fuse_adapter_maps_errors_and_preserves_read_semantics(self):
        binding = SimpleNamespace(Operations=object, FuseOSError=lambda code: OSError(code, "FUSE error"))
        adapter = fuse_operations(binding, self.fs)
        self.assertTrue(adapter.use_ns)
        handle = adapter.open(self.path, os.O_RDONLY)
        self.assertEqual(adapter.read(self.path, 20, 10, handle), self.data[10:30])
        adapter.release(self.path, handle)
        with self.assertRaises(OSError) as error:
            adapter.open(self.path, os.O_WRONLY)
        self.assertEqual(error.exception.errno, errno.EROFS)

    def test_mountpoint_refuses_overlap_and_nonempty_directory(self):
        target = self.base / "view"
        target.mkdir()
        self.assertEqual(checked_mountpoint(self.source, self.cache, target), target.resolve())
        with self.assertRaises(ValueError):
            checked_mountpoint(self.source, self.cache, self.source / "view")
        (target / "keep.txt").write_text("keep")
        with self.assertRaises(ValueError):
            checked_mountpoint(self.source, self.cache, target)
        self.assertEqual((target / "keep.txt").read_text(), "keep")

    def test_cold_cache_verification_detects_same_size_damage(self):
        self.store.prefetch(self.relative)
        self.store._verified.clear()
        next(self.cache.glob("*.blob")).write_bytes(b"z" * len(self.data))
        handle = self.fs.open(self.path, os.O_RDONLY)
        self.assertEqual(self.fs.origin(handle), "source")
        self.fs.release(self.path, handle)

    def test_source_change_during_background_verification_rejects_publication(self):
        source = self.source / self.relative
        before = source.stat()
        original_digest = self.store._digest

        def verify_then_change(file, checkpoint):
            result = original_digest(file, checkpoint)
            os.utime(source, ns=(before.st_atime_ns, before.st_mtime_ns + 2000000000))
            return result

        with patch.object(self.store, "_digest", side_effect=verify_then_change):
            with self.assertRaises(OSError):
                self.store.prefetch(self.relative)
        self.assertEqual(list(self.cache.glob("*.blob")), [])
        self.assertEqual(list(self.cache.glob("*.part")), [])


if __name__ == "__main__":
    unittest.main()
