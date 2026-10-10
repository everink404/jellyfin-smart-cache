"""Read-only path/handle operations, independent of the optional FUSE binding."""
from dataclasses import dataclass, field
import errno
import logging
import os
from pathlib import PurePosixPath
import stat
import threading

log = logging.getLogger(__name__)


@dataclass
class Reader:
    lease: object
    file: object
    origin: str
    attributes: dict
    lock: object = field(default_factory=threading.Lock)
    closed: bool = False


class MediaFilesystem:
    def __init__(self, store):
        self.store = store
        self._handles = {}
        self._lock = threading.Lock()
        self._sequence = 0

    def _path(self, path):
        posix = PurePosixPath(path)
        if not path.startswith("/") or ".." in posix.parts or "\\" in path:
            raise OSError(errno.EACCES, "invalid media path")
        parts = posix.parts[1:]
        candidate = self.store.source
        for part in parts:
            candidate = candidate / part
            if candidate.is_symlink():
                raise OSError(errno.EACCES, "symlinks are excluded from test media view")
        resolved = candidate.resolve(strict=True)
        if not resolved.is_relative_to(self.store.source):
            raise OSError(errno.EACCES, "outside media root")
        return resolved, "/".join(parts)

    @staticmethod
    def _attributes(info):
        # Preserve source metadata irrespective of selected backing file.
        return {"st_mode": info.st_mode & ~0o222, "st_nlink": info.st_nlink,
                "st_size": info.st_size, "st_uid": info.st_uid, "st_gid": info.st_gid,
                "st_ino": info.st_ino, "st_atime": info.st_atime_ns,
                "st_mtime": info.st_mtime_ns, "st_ctime": info.st_ctime_ns}

    def getattr(self, path, fh=None):
        if fh is not None:
            return dict(self._reader(fh).attributes)
        file, _ = self._path(path)
        return self._attributes(file.stat())

    def readdir(self, path, fh=None):
        directory, _ = self._path(path)
        if not directory.is_dir():
            raise NotADirectoryError(errno.ENOTDIR, "not a directory")
        return [".", "..", *sorted(p.name for p in directory.iterdir()
                                    if not p.is_symlink() and (p.is_file() or p.is_dir()))]

    def access(self, path, mode):
        if mode & os.W_OK:
            raise OSError(errno.EROFS, "read-only media view")
        file, _ = self._path(path)
        if not os.access(file, mode):
            raise OSError(errno.EACCES, "source access denied")
        return 0

    def open(self, path, flags):
        access_mask = os.O_WRONLY | os.O_RDWR
        if flags & access_mask != os.O_RDONLY or flags & (os.O_CREAT | os.O_TRUNC | os.O_APPEND):
            raise OSError(errno.EROFS, "read-only media view")
        file, relative = self._path(path)
        if not stat.S_ISREG(file.stat().st_mode):
            raise OSError(errno.EISDIR, "not a regular media file")
        attributes = self._attributes(file.stat())
        lease = self.store.open_media(relative)
        handle, origin = lease.__enter__()
        with self._lock:
            self._sequence += 1
            number = self._sequence
            self._handles[number] = Reader(lease, handle, origin, attributes)
        log.info("open fh=%d origin=%s path=%s", number, origin, path)
        return number

    def _reader(self, fh):
        with self._lock:
            reader = self._handles.get(fh)
        if reader is None:
            raise OSError(errno.EBADF, "unknown file handle")
        return reader

    def origin(self, fh):
        return self._reader(fh).origin

    def read(self, path, size, offset, fh):
        if size < 0 or offset < 0:
            raise OSError(errno.EINVAL, "negative read size or offset")
        reader = self._reader(fh)
        with reader.lock:
            if reader.closed:
                raise OSError(errno.EBADF, "closed file handle")
            reader.file.seek(offset)
            return reader.file.read(size)

    def release(self, path, fh):
        reader = self._reader(fh)
        with reader.lock:
            if reader.closed:
                raise OSError(errno.EBADF, "closed file handle")
            reader.closed = True
            reader.lease.__exit__(None, None, None)
        with self._lock:
            self._handles.pop(fh, None)
        return 0

    def close(self):
        with self._lock:
            handles = list(self._handles)
        for fh in handles:
            self.release("", fh)
