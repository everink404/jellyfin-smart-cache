"""Optional Linux FUSE test entry. Does not alter Docker or original mounts."""
import argparse
import errno
import logging
import os
from pathlib import Path
import sys

from .filesystem import MediaFilesystem
from .store import CacheStore


def checked_mountpoint(source, cache, mountpoint):
    if Path(mountpoint).is_symlink():
        raise ValueError("mountpoint cannot be a symlink")
    roots = [Path(p).resolve() for p in (source, cache, mountpoint)]
    for i, root in enumerate(roots):
        for other in roots[i + 1:]:
            if root.is_relative_to(other) or other.is_relative_to(root):
                raise ValueError("source, cache and mountpoint must be separate directory trees")
    target = roots[2]
    if not target.is_dir() or target.is_symlink() or os.path.ismount(target):
        raise ValueError("mountpoint must be an existing unmounted test directory")
    if any(target.iterdir()):
        raise ValueError("refusing to hide a nonempty directory")
    return target


def fuse_operations(binding, backend):
    class Operations(binding.Operations):
        use_ns = True

    def callback(name):
        def call(self, *args):
            try:
                return getattr(backend, name)(*args)
            except OSError as error:
                raise binding.FuseOSError(error.errno or errno.EIO) from error
            except ValueError as error:
                raise binding.FuseOSError(errno.EACCES) from error
        return call

    for name in ("getattr", "readdir", "access", "open", "read", "release"):
        setattr(Operations, name, callback(name))
    return Operations()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Read-only Linux test media mount")
    parser.add_argument("--source", required=True)
    parser.add_argument("--cache", required=True)
    parser.add_argument("--mountpoint", required=True)
    parser.add_argument("--capacity-bytes", required=True, type=int)
    parser.add_argument("--safety-bytes", required=True, type=int)
    parser.add_argument("--warm", action="append", default=[], help="relative file to prefetch before mounting")
    parser.add_argument("--allow-other", action="store_true", help="explicitly permit other users; needs host FUSE permission support")
    args = parser.parse_args(argv)
    if not sys.platform.startswith("linux"):
        parser.error("actual mounting requires Linux; portable callback tests work on Windows")
    try:
        import mfusepy
    except (ImportError, OSError) as error:
        parser.error(f"optional mfusepy/libfuse dependency unavailable: {error}")
    store = None
    backend = None
    try:
        target = checked_mountpoint(args.source, args.cache, args.mountpoint)
        store = CacheStore(args.source, args.cache, args.capacity_bytes, args.safety_bytes)
        for relative in args.warm:
            store.prefetch(relative)
        backend = MediaFilesystem(store)
        logging.basicConfig(level=logging.INFO)
        # Disable kernel read/attribute reuse so test opens reach our selection logic.
        mfusepy.FUSE(fuse_operations(mfusepy, backend), str(target),
                     foreground=True, ro=True, direct_io=True, default_permissions=True,
                     allow_other=args.allow_other, attr_timeout=0,
                     entry_timeout=0, negative_timeout=0)
        return 0
    except (OSError, ValueError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        return 1
    finally:
        if backend:
            backend.close()
        if store:
            store.close()


if __name__ == "__main__":
    sys.exit(main())
