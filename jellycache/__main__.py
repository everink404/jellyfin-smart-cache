"""Manual prototype runner, intentionally no unattended queue or mount changes."""
import argparse
import hashlib
import json
import sys

from .store import CacheStore


def main():
    parser = argparse.ArgumentParser(description="File-source cache prototype (Python 3.11+)")
    parser.add_argument("--source", required=True, help="existing, read-only media source tree")
    parser.add_argument("--cache", required=True, help="dedicated local cache directory")
    parser.add_argument("--capacity-bytes", required=True, type=int)
    parser.add_argument("--safety-bytes", required=True, type=int)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("prefetch").add_argument("relative_file")
    probe = commands.add_parser("probe", help="report selected origin and hash a small read")
    probe.add_argument("relative_file")
    probe.add_argument("--offset", type=int, default=0)
    probe.add_argument("--length", type=int, default=4096)
    args = parser.parse_args()
    if args.command == "probe" and (args.offset < 0 or not 0 <= args.length <= 1048576):
        parser.error("offset must be nonnegative; length must be between 0 and 1048576")
    store = None
    try:
        store = CacheStore(args.source, args.cache, args.capacity_bytes, args.safety_bytes)
        if args.command == "prefetch":
            result = {"result": store.prefetch(args.relative_file)}
        else:
            with store.open_media(args.relative_file) as (handle, origin):
                handle.seek(args.offset)
                data = handle.read(args.length)
                result = {"origin": origin, "offset": args.offset,
                          "bytes_read": len(data), "read_sha256": hashlib.sha256(data).hexdigest()}
        print(json.dumps(result))
        return 0
    except (OSError, ValueError, RuntimeError) as error:
        print(json.dumps({"error": str(error)}), file=sys.stderr)
        return 1
    finally:
        if store:
            store.close()


if __name__ == "__main__":
    sys.exit(main())
