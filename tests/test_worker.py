import os
from pathlib import Path
import shutil
import threading
import time
import unittest
from unittest.mock import patch
import uuid

from jellycache.planner import plan_window
from jellycache.store import CacheStore, CapacityError, CopyCancelled
from jellycache.worker import PrefetchWorker


def episodes(season="s1", count=12):
    return [{"type": "Episode", "id": f"{season}-e{i}", "series_id": "series",
             "season_id": season, "index": i, "path": f"{season}/E{i:02d}.mkv"}
            for i in range(1, count + 1)]


def event(index=1, seq=1, session="session-a", phase="start", strategy="ahead"):
    items = episodes()
    return {"session_id": session, "sequence": seq, "phase": phase,
            "current": items[index - 1], "episodes": items, "strategy": strategy}


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.workspace = Path(__file__).resolve().parents[1]
        self.base = self.workspace / (".cache-test-" + uuid.uuid4().hex)
        self.source = self.base / "source"
        self.source.mkdir(parents=True)
        for item in episodes():
            file = self.source / item["path"]
            file.parent.mkdir(exist_ok=True)
            file.write_bytes(item["id"].encode() * 10)
        self.cache = self.base / "cache"
        self.store = CacheStore(self.source, self.cache, 100000)
        self.now = 1000
        self.worker = PrefetchWorker(self.store, clock=lambda: self.now)

    def tearDown(self):
        self.worker.close()
        self.store.close()
        assert self.base.resolve().is_relative_to(self.workspace)
        shutil.rmtree(self.base)

    def states(self):
        return {row["path"]: row for row in self.worker.status()["tasks"]}

    def test_default_window_jump_and_stale_event(self):
        result = self.worker.event(event())
        self.assertEqual(result["targets"], ["s1/E02.mkv", "s1/E03.mkv"])
        self.assertEqual(self.worker.event(event())["result"], "ignored")
        self.worker.event(event(8, 2))
        states = self.states()
        self.assertEqual(states["s1/E02.mkv"]["state"], "cancelled")
        self.assertEqual(states["s1/E09.mkv"]["state"], "queued")
        self.assertEqual(self.worker.event(event(1, 1))["result"], "ignored")

    def test_all_strategies_no_cross_season(self):
        items = episodes() + episodes("s2")
        current = items[0]
        self.assertEqual([e.index for e in plan_window(current, items, "next")[1]], [2])
        self.assertEqual([e.index for e in plan_window(current, items, "ahead", 3)[1]], [2, 3, 4])
        self.assertEqual([e.index for e in plan_window(items[7], items, "season")[1]], [9, 10, 11, 12])
        self.assertEqual(plan_window(items[11], items)[1], [])

    def test_ambiguous_metadata_rejected_without_mutation(self):
        payload = event()
        payload["episodes"][1]["index"] = 1
        with self.assertRaises(ValueError):
            self.worker.event(payload)
        self.assertEqual(self.states(), {})

    def test_shared_demand_survives_one_session_stop_and_deduplicates(self):
        self.worker.event(event())
        self.worker.event(event(session="session-b"))
        self.worker.event({"session_id": "session-a", "sequence": 2, "phase": "stop"})
        self.assertEqual(len(self.states()), 2)
        self.assertTrue(self.worker.run_once())
        self.assertEqual(self.states()["s1/E02.mkv"]["state"], "complete")
        self.assertTrue(self.worker.run_once())
        self.assertFalse(self.worker.run_once())
        self.assertEqual(len(list(self.cache.glob("*.blob"))), 2)

    def test_pause_keeps_window_and_progress_resumes(self):
        self.worker.event(event(phase="pause"))
        self.assertFalse(self.worker.run_once())
        self.assertEqual(len(self.states()), 2)
        self.worker.event(event(seq=2, phase="progress"))
        self.worker.pause()
        self.assertFalse(self.worker.run_once())
        self.worker.pause(False)
        self.assertTrue(self.worker.run_once())

    def test_bounded_retry_backoff_and_explicit_retry(self):
        self.worker.event(event())
        with patch.object(self.store, "prefetch", side_effect=OSError("no source")) as copy:
            for _ in range(3):
                self.worker.run_once()
                # Allow the other task to attempt, then advance both backoffs.
                self.worker.run_once()
                self.now += 10
            self.assertEqual(copy.call_count, 6)
        self.assertEqual(self.states()["s1/E02.mkv"]["state"], "failed")
        self.worker.event(event(seq=2, phase="progress"))
        self.assertFalse(self.worker.run_once())
        self.assertTrue(self.worker.retry("s1/E02.mkv")["retried"])
        self.assertTrue(self.worker.run_once())

    def test_capacity_block_does_not_consume_retry_budget(self):
        self.worker.event(event())
        with patch.object(self.store, "prefetch", side_effect=CapacityError("full")):
            self.worker.run_once()
        row = self.states()["s1/E02.mkv"]
        self.assertEqual((row["state"], row["attempts"]), ("blocked", 0))

    def test_session_timeout_releases_protection_and_cancels_pending(self):
        self.worker.event(event())
        self.assertTrue(any(self.store._protected.values()))
        self.now += 121
        self.assertEqual(self.worker.status()["sessions"], 0)
        self.assertFalse(any(self.store._protected.values()))
        self.assertTrue(all(t["state"] == "cancelled" for t in self.states().values()))

    def test_restart_requires_fresh_events_and_retains_pause(self):
        self.worker.event(event())
        self.worker.pause()
        self.worker.close()
        self.worker = PrefetchWorker(self.store, clock=lambda: self.now)
        self.assertTrue(self.worker.status()["paused"])
        self.worker.pause(False)
        self.assertFalse(self.worker.run_once())
        self.worker.event(event(seq=2, phase="progress"))
        self.assertTrue(self.worker.run_once())

    def test_raw_playback_protects_cached_current_file(self):
        self.store.prefetch("s1/E01.mkv")
        self.store.capacity = (self.source / "s1/E01.mkv").stat().st_size
        self.worker.event(event())
        with self.assertRaises(CapacityError):
            self.store.prefetch("s1/E02.mkv")
        self.worker.event({"session_id": "session-a", "sequence": 2, "phase": "stop"})
        self.store.prefetch("s1/E02.mkv")

    def test_cancel_checkpoint_cleans_owned_partial(self):
        def cancel():
            raise CopyCancelled("stop")
        with self.assertRaises(CopyCancelled):
            self.store.prefetch("s1/E02.mkv", checkpoint=cancel)
        self.assertEqual(list(self.cache.glob("*.part")), [])

    def test_copy_rate_is_enforced(self):
        times = [0.0]
        with patch("jellycache.store.time.monotonic", side_effect=lambda: times[0]), \
             patch("jellycache.store.time.sleep", side_effect=lambda delay: times.__setitem__(0, times[0] + delay)):
            self.store.prefetch("s1/E02.mkv", bytes_per_second=10)
        self.assertGreaterEqual(times[0], (self.source / "s1/E02.mkv").stat().st_size / 10)

    def test_equal_priority_round_robin(self):
        self.worker.event(event())
        self.worker.event(event(8, session="session-b"))
        with patch.object(self.store, "prefetch", return_value="complete") as copy:
            self.worker.run_once()
            self.worker.run_once()
        self.assertEqual([c.args[0] for c in copy.call_args_list], ["s1/E02.mkv", "s1/E09.mkv"])

    def test_background_worker_finishes_and_closes(self):
        self.worker.event(event())
        self.worker.start()
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            if all(t["state"] == "complete" for t in self.states().values()):
                break
            time.sleep(0.01)
        self.assertTrue(all(t["state"] == "complete" for t in self.states().values()))
