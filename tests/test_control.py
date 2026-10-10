import json
from pathlib import Path
import shutil
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import uuid

from jellycache.control import ControlServer
from jellycache.filesystem import MediaFilesystem
from jellycache.jellyfin_events import mapped_path, normalized_event
from jellycache.store import CacheStore
from jellycache.worker import PrefetchWorker
from test_worker import episodes, event


def jellyfin_payload():
    items = [{"Id": e["id"], "Type": "Episode", "SeriesId": "series", "SeasonId": "s1",
              "IndexNumber": e["index"], "ParentIndexNumber": 1, "Path": "/video/" + e["path"]}
             for e in episodes()]
    return {"sequence": 1, "phase": "start", "session": {"Id": "dto-session",
            "NowPlayingItem": {"Id": items[0]["Id"], "Type": "Episode"},
            "PlayState": {"IsPaused": False}}, "episodes": items}


class ControlTests(unittest.TestCase):
    def setUp(self):
        self.workspace = Path(__file__).resolve().parents[1]
        self.base = self.workspace / (".cache-test-" + uuid.uuid4().hex)
        source = self.base / "source"
        source.mkdir(parents=True)
        for item in episodes():
            path = source / item["path"]
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(b"source-video-content")
        self.store = CacheStore(source, self.base / "cache", 100000)
        self.worker = PrefetchWorker(self.store)
        self.token = "test-only-control-secret-" + "x" * 32
        self.control = ControlServer(self.worker, self.token)
        self.control.start()
        self.url = "http://127.0.0.1:" + str(self.control.address[1])

    def tearDown(self):
        self.control.close()
        self.worker.close()
        self.store.close()
        assert self.base.resolve().is_relative_to(self.workspace)
        shutil.rmtree(self.base)

    def request(self, route, payload=None, *, auth=True, raw=None, headers=None):
        body = json.dumps(payload).encode() if payload is not None else raw
        merged = {"Content-Type": "application/json"}
        if auth:
            merged["Authorization"] = "Bearer " + self.token
        merged.update(headers or {})
        request = Request(self.url + route, data=body, headers=merged)
        try:
            with urlopen(request, timeout=3) as response:
                return response.status, json.loads(response.read())
        except HTTPError as error:
            with error:
                return error.code, json.loads(error.read())

    def test_authenticated_event_duplicate_and_status(self):
        self.assertEqual(self.control.address[0], "127.0.0.1")
        self.assertEqual(self.request("/v1/status", auth=False)[0], 401)
        code, accepted = self.request("/v1/events", event())
        self.assertEqual(code, 200)
        self.assertEqual(accepted["targets"], ["s1/E02.mkv", "s1/E03.mkv"])
        self.assertEqual(self.request("/v1/events", event())[1]["result"], "ignored")
        self.assertEqual(self.request("/v1/status")[1]["task_count"], 2)

    def test_unauthorized_write_does_not_mutate_queue(self):
        self.assertEqual(self.request("/v1/events", event(), auth=False)[0], 401)
        self.assertEqual(self.worker.status()["tasks"], [])

    def test_pause_resume_and_bad_body(self):
        self.assertTrue(self.request("/v1/pause", {})[1]["paused"])
        self.assertFalse(self.request("/v1/resume", {})[1]["paused"])
        self.assertEqual(self.request("/v1/events", raw=b"not JSON")[0], 400)
        self.assertEqual(self.request("/v1/events", [1, 2])[0], 400)
        self.assertEqual(self.request("/v1/unknown", {})[0], 404)
        self.assertEqual(self.request("/v1/events", event(), headers={"Content-Type": "text/plain"})[0], 415)

    def test_dto_adapter_event_to_cache_to_stable_reading(self):
        fs = MediaFilesystem(self.store)
        try:
            old = fs.open("/s1/E02.mkv", 0)
            self.assertEqual(fs.origin(old), "source")
            code, body = self.request("/v1/jellyfin-events", jellyfin_payload())
            self.assertEqual(code, 200)
            self.assertEqual(body["targets"], ["s1/E02.mkv", "s1/E03.mkv"])
            self.worker.run_once()
            new = fs.open("/s1/E02.mkv", 0)
            self.assertEqual(fs.origin(new), "cache")
            self.assertEqual(fs.origin(old), "source")
            self.assertEqual(fs.read("", 5, 3, new), fs.read("", 5, 3, old))
        finally:
            fs.close()

    def test_outside_media_root_and_ambiguous_versions_rejected(self):
        payload = jellyfin_payload()
        payload["episodes"][1]["Path"] = "/video-other/E02.mkv"
        self.assertEqual(self.request("/v1/jellyfin-events", payload)[0], 400)
        payload = jellyfin_payload()
        payload["episodes"][1]["MediaSources"] = [{"Id": "a", "Path": "/video/a.mkv"},
                                                     {"Id": "b", "Path": "/video/b.mkv"}]
        self.assertEqual(self.request("/v1/jellyfin-events", payload)[0], 400)
        self.assertEqual(self.worker.status()["task_count"], 0)

    def test_movie_snapshot_stops_existing_episode_session(self):
        payload = jellyfin_payload()
        self.request("/v1/jellyfin-events", payload)
        payload["sequence"] = 2
        payload["session"]["NowPlayingItem"] = {"Type": "Movie", "Id": "movie"}
        self.assertEqual(self.request("/v1/jellyfin-events", payload)[0], 200)
        self.assertTrue(all(t["state"] == "cancelled" for t in self.worker.status()["tasks"]))

    def test_paused_snapshot_does_not_start_queued_downloads(self):
        payload = jellyfin_payload()
        payload["session"]["PlayState"]["IsPaused"] = True
        self.assertEqual(self.request("/v1/jellyfin-events", payload)[0], 200)
        self.assertFalse(self.worker.run_once())

    def test_malformed_dto_and_path_traversal(self):
        for playing in ("bad", [1, 2]):
            payload = jellyfin_payload()
            payload["session"]["NowPlayingItem"] = playing
            self.assertEqual(self.request("/v1/jellyfin-events", payload)[0], 400)
        with self.assertRaises(ValueError):
            mapped_path("/video/../outside")
        self.assertEqual(mapped_path("/video/Cloud Sync/E01.mkv"), "Cloud Sync/E01.mkv")

    def test_missing_episode_is_not_a_download_target(self):
        payload = jellyfin_payload()
        payload["episodes"][1]["IsMissing"] = True
        translated = normalized_event(payload)
        self.assertNotIn("s1-e2", [e["id"] for e in translated["episodes"]])
        self.assertEqual(self.request("/v1/jellyfin-events", payload)[1]["targets"], ["s1/E03.mkv", "s1/E04.mkv"])
