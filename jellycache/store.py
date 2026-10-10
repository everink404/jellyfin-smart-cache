"""Single-process cache with durable index and leases for readers.

All media input is read-only. The cache must have its own dedicated directory.
Source size/mtime are provisional identity hints, not a remote version guarantee.
"""
from __future__ import annotations

from contextlib import contextmanager
import hashlib
import os
from pathlib import Path
import shutil
import sqlite3
import threading
import time


class CapacityError(RuntimeError):
    pass


class CopyCancelled(RuntimeError):
    pass


class CacheStore:
    MARKER = "jellycache-prototype-v1\n"

    def __init__(self, source, cache, capacity_bytes, safety_bytes=0):
        self.source = Path(source).resolve(strict=True)
        self.root = Path(cache).resolve()
        if not self.source.is_dir():
            raise ValueError("source must be a directory")
        if (self.root.is_relative_to(self.source)
                or self.source.is_relative_to(self.root)):
            raise ValueError("source and cache must be separate directory trees")
        if capacity_bytes <= 0 or safety_bytes < 0:
            raise ValueError("invalid capacity or safety margin")
        self.capacity = capacity_bytes
        self.safety = safety_bytes
        self.root.mkdir(parents=True, exist_ok=True)
        marker = self.root / ".jellycache-owner"
        if not marker.exists():
            if any(self.root.iterdir()):
                raise ValueError("refusing to adopt a nonempty, unowned cache directory")
            marker.write_text(self.MARKER, encoding="utf-8")
        if marker.read_text(encoding="utf-8") != self.MARKER:
            raise ValueError("unrecognized cache owner marker")
        self._instance = (self.root / ".instance.lock").open("a+b")
        try:
            if os.name == "nt":
                import msvcrt
                self._instance.seek(0)
                self._instance.write(b"0")
                self._instance.flush()
                self._instance.seek(0)
                msvcrt.locking(self._instance.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._instance, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self._instance.close()
            raise RuntimeError("cache already has an active owner") from None
        self._lock = threading.RLock()
        self._download = threading.Lock()
        self._pins = {}
        self._protected = {}
        self._verified = {}
        try:
            self.db = sqlite3.connect(self.root / "index.sqlite", check_same_thread=False)
            self.db.execute("""CREATE TABLE IF NOT EXISTS entries (
                key TEXT PRIMARY KEY, rel TEXT NOT NULL, size INTEGER NOT NULL,
                mtime INTEGER NOT NULL, digest TEXT, accessed REAL NOT NULL,
                state TEXT NOT NULL)""")
            # Only indexed, owned incomplete files are removed on restart.
            for (key,) in self.db.execute("SELECT key FROM entries WHERE state='copying'"):
                self._blob(key, ".part").unlink(missing_ok=True)
                self._blob(key, ".blob").unlink(missing_ok=True)
            self.db.execute("DELETE FROM entries WHERE state='copying'")
            self.db.commit()
        except Exception:
            if hasattr(self, "db"):
                self.db.close()
            self._instance.close()
            raise

    def _blob(self, key, suffix=".blob"):
        if len(key) != 64 or any(c not in "0123456789abcdef" for c in key):
            raise ValueError("invalid index key")
        path = self.root / (key + suffix)
        if path.is_symlink():
            raise ValueError("cache blobs cannot be symlinks")
        return path

    def _source(self, relative):
        rel = Path(relative)
        if rel.is_absolute() or ".." in rel.parts or not rel.parts:
            raise ValueError("expected a relative media filename without ..")
        file = (self.source / rel).resolve(strict=True)
        if not file.is_relative_to(self.source) or not file.is_file():
            raise ValueError("media must be a regular file within source")
        return rel.as_posix(), file

    @staticmethod
    def _identity(file):
        stat = file.stat()
        return stat.st_size, stat.st_mtime_ns

    def _remove(self, key):
        state = self.db.execute("SELECT state FROM entries WHERE key=?", (key,)).fetchone()
        if self._pins.get(key) or (self._protected.get(key) and state != ("copying",)):
            raise CapacityError("file has active readers")
        self._blob(key).unlink(missing_ok=True)
        self.db.execute("DELETE FROM entries WHERE key=?", (key,))
        self.db.commit()
        self._verified.pop(key, None)

    @staticmethod
    def _local_signature(stat):
        return (stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns, stat.st_ino, stat.st_dev)

    def _reserve(self, size):
        # Completed bytes and each full in-flight reservation count once.
        used = self.db.execute("SELECT COALESCE(SUM(size),0) FROM entries").fetchone()[0]
        if size > self.capacity:
            raise CapacityError("file exceeds cache capacity")
        candidates = list(self.db.execute(
            "SELECT key,size FROM entries WHERE state IN ('complete','invalid') ORDER BY (state='invalid') DESC,accessed"))
        for key, item_size in candidates:
            if used + size <= self.capacity and shutil.disk_usage(self.root).free >= self.safety + size:
                break
            if not self._pins.get(key) and not self._protected.get(key):
                self._remove(key)
                used -= item_size
        if used + size > self.capacity or shutil.disk_usage(self.root).free < self.safety + size:
            raise CapacityError("no safe space; remote reading remains available")

    def prefetch(self, relative, *, bytes_per_second=0, checkpoint=None):
        """Synchronous, one download at a time. Returns complete/busy/in-use."""
        if type(bytes_per_second) is not int or bytes_per_second < 0:
            raise ValueError("invalid copy rate")
        checkpoint = checkpoint or (lambda: None)
        rel, file = self._source(relative)
        key = hashlib.sha256(rel.encode()).hexdigest()
        if not self._download.acquire(blocking=False):
            return "busy"
        try:
            size, mtime = self._identity(file)
            with self._lock:
                row = self.db.execute("SELECT size,mtime,state FROM entries WHERE key=?", (key,)).fetchone()
                if row and row == (size, mtime, "complete") and self._blob(key).is_file() and self._blob(key).stat().st_size == size:
                    return "complete"
                if self._pins.get(key) or self._protected.get(key):
                    return "in-use"
                if not row and (self._blob(key).exists() or self._blob(key, ".part").exists()):
                    raise ValueError("unindexed cache files exist; preserve them for review")
                if row:
                    self._remove(key)
                self._reserve(size)
                self.db.execute("INSERT INTO entries VALUES (?,?,?,?,?,?,?)",
                                (key, rel, size, mtime, None, time.time(), "copying"))
                self.db.commit()
            part = self._blob(key, ".part")
            try:
                digest = hashlib.sha256()
                copied = 0
                block_size = min(1024 * 1024, max(4096, bytes_per_second // 4)) if bytes_per_second else 1024 * 1024
                with file.open("rb") as src, part.open("xb") as dst:
                    while True:
                        checkpoint()
                        started = time.monotonic()
                        chunk = src.read(block_size)
                        if not chunk:
                            break
                        if copied + len(chunk) > size:
                            raise OSError("source grew during copying")
                        if shutil.disk_usage(self.root).free < self.safety + len(chunk):
                            raise CapacityError("disk safety margin reached")
                        dst.write(chunk)
                        digest.update(chunk)
                        copied += len(chunk)
                        if bytes_per_second:
                            deadline = started + len(chunk) / bytes_per_second
                            while time.monotonic() < deadline:
                                checkpoint()
                                time.sleep(min(0.1, max(0, deadline - time.monotonic())))
                    dst.flush()
                    os.fsync(dst.fileno())
                if copied != size or self._identity(file) != (size, mtime):
                    raise OSError("source changed or copy was incomplete")
                # Validate written bytes in the background, before publication.
                with part.open("rb") as check:
                    if self._digest(check, checkpoint) != digest.hexdigest():
                        raise OSError("local copy failed digest verification")
                if self._identity(file) != (size, mtime):
                    raise OSError("source changed during local verification")
                with self._lock:
                    part.replace(self._blob(key))
                    self.db.execute("UPDATE entries SET digest=?,state='complete' WHERE key=?",
                                    (digest.hexdigest(), key))
                    self.db.commit()
                    with self._blob(key).open("rb") as published:
                        self._verified[key] = self._local_signature(os.fstat(published.fileno()))
                return "complete"
            except Exception:
                with self._lock:
                    part.unlink(missing_ok=True)
                    self._remove(key)
                raise
        finally:
            self._download.release()

    @contextmanager
    def open_media(self, relative):
        """Yield (binary handle, 'cache'/'source'); lease lasts through close.

        Rehash after restart or when local metadata changes. In-process copies
        were verified before publication. This is not a silent-bitrot detector.
        """
        rel, file = self._source(relative)
        identity = self._identity(file)
        key = hashlib.sha256(rel.encode()).hexdigest()
        handle = None
        pinned = False
        origin = "source"
        try:
            with self._lock:
                row = self.db.execute("SELECT size,mtime,digest,state FROM entries WHERE key=?", (key,)).fetchone()
                if row and row[:2] == identity and row[3] == "complete":
                    try:
                        handle = self._blob(key).open("rb")
                        self._pins[key] = self._pins.get(key, 0) + 1
                        pinned = True
                    except OSError:
                        handle = None
            if handle:
                try:
                    signature = self._local_signature(os.fstat(handle.fileno()))
                    with self._lock:
                        verified = self._verified.get(key) == signature
                    if verified:
                        valid = signature[0] == row[0]
                    else:
                        digest = hashlib.file_digest(handle, "sha256").hexdigest()
                        handle.seek(0)
                        valid = (signature[0] == row[0] and digest == row[2]
                                 and signature == self._local_signature(os.fstat(handle.fileno())))
                except OSError:
                    valid = False
                if valid:
                    origin = "cache"
                    with self._lock:
                        self._verified[key] = signature
                        self.db.execute("UPDATE entries SET accessed=? WHERE key=?", (time.time(), key))
                        self.db.commit()
                else:
                    handle.close()
                    handle = None
                    with self._lock:
                        self._pins[key] -= 1
                        pinned = False
                        self.db.execute("UPDATE entries SET state='invalid' WHERE key=?", (key,))
                        self.db.commit()
                        self._verified.pop(key, None)
                        if not self._pins[key] and not self._protected.get(key):
                            self._remove(key)
            if handle is None:
                handle = file.open("rb")
            yield handle, origin
        finally:
            if handle:
                handle.close()
            if pinned:
                with self._lock:
                    self._pins[key] -= 1

    def close(self):
        with self._lock:
            if any(self._pins.values()) or any(self._protected.values()) or self._download.locked():
                raise RuntimeError("cannot close with active readers or downloads")
            self.db.close()
            self._instance.close()

    @staticmethod
    def _digest(file, checkpoint):
        digest = hashlib.sha256()
        while True:
            checkpoint()
            chunk = file.read(1024 * 1024)
            if not chunk:
                return digest.hexdigest()
            digest.update(chunk)

    def protect_media(self, relative):
        """Logical playback protection, including clients bypassing our view."""
        from .planner import relative_path
        rel = relative_path(relative)
        key = hashlib.sha256(rel.encode()).hexdigest()
        with self._lock:
            self._protected[key] = self._protected.get(key, 0) + 1
        return key

    def unprotect_media(self, key):
        with self._lock:
            if not self._protected.get(key):
                raise ValueError("unknown playback protection")
            self._protected[key] -= 1
