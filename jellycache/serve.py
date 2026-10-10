"""Run the queue/control owner with an optional read-only Linux media view."""
import argparse
import logging
import os
import sys
import threading

from .control import ControlServer
from .filesystem import MediaFilesystem
from .mount import checked_mountpoint, fuse_operations
from .store import CacheStore
from .worker import PrefetchWorker


def main(argv=None):
    parser = argparse.ArgumentParser(description="JellyCache test queue/control service")
    parser.add_argument("--source", required=True)
    parser.add_argument("--cache", required=True)
    parser.add_argument("--capacity-bytes", required=True, type=int)
    parser.add_argument("--safety-bytes", required=True, type=int)
    parser.add_argument("--rate-bytes", type=int, default=8 * 1024 * 1024)
    parser.add_argument("--session-timeout", type=int, default=120)
    parser.add_argument("--control-port", type=int, default=8765)
    parser.add_argument("--media-root", default="/video")
    parser.add_argument("--mountpoint")
    parser.add_argument("--allow-other", action="store_true")
    args = parser.parse_args(argv)
    token = os.environ.get("JELLYCACHE_CONTROL_TOKEN", "")
    if len(token) < 32:
        parser.error("set JELLYCACHE_CONTROL_TOKEN to an independently generated secret of at least 32 characters")
    binding = None
    if args.mountpoint:
        if not sys.platform.startswith("linux"):
            parser.error("optional test mounting requires Linux")
        try:
            import mfusepy as binding
        except (ImportError, OSError) as error:
            parser.error(f"optional FUSE dependencies unavailable: {error}")
    store = worker = control = backend = None
    try:
        target = checked_mountpoint(args.source, args.cache, args.mountpoint) if args.mountpoint else None
        store = CacheStore(args.source, args.cache, args.capacity_bytes, args.safety_bytes)
        worker = PrefetchWorker(store, bytes_per_second=args.rate_bytes, session_timeout=args.session_timeout)
        control = ControlServer(worker, token, args.control_port, args.media_root)
        worker.start()
        control.start()
        print(f"Control: http://127.0.0.1:{control.address[1]}", flush=True)
        if target:
            backend = MediaFilesystem(store)
            logging.basicConfig(level=logging.INFO)
            binding.FUSE(fuse_operations(binding, backend), str(target), foreground=True,
                         ro=True, direct_io=True, default_permissions=True,
                         allow_other=args.allow_other, attr_timeout=0, entry_timeout=0, negative_timeout=0)
        else:
            threading.Event().wait()
        return 0
    except KeyboardInterrupt:
        return 0
    except (OSError, ValueError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        return 1
    finally:
        if control:
            control.close()
        if worker:
            worker.close()
        if backend:
            backend.close()
        if store:
            store.close()


if __name__ == "__main__":
    sys.exit(main())
