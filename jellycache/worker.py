"""Durable intent queue. One worker and filesystem share the same CacheStore."""
import sqlite3
import threading
import time

from .planner import identifier, plan_window, relative_path
from .store import CapacityError, CopyCancelled


class PrefetchWorker:
    def __init__(self, store, *, bytes_per_second=8 * 1024 * 1024,
                 max_attempts=3, session_timeout=120, clock=time.time):
        if type(bytes_per_second) is not int or bytes_per_second <= 0:
            raise ValueError("worker requires a positive copy rate")
        if type(max_attempts) is not int or not 1 <= max_attempts <= 5 or session_timeout <= 0:
            raise ValueError("invalid retry or timeout limit")
        self.store = store
        self.rate = bytes_per_second
        self.max_attempts = max_attempts
        self.timeout = session_timeout
        self.clock = clock
        self._cv = threading.Condition(threading.RLock())
        self._execution = threading.Lock()
        self._stopping = False
        self._thread = None
        self._playing = {}
        self._served = {}
        self._counter = 0
        self.db = sqlite3.connect(store.root / "queue.sqlite", check_same_thread=False)
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY, seq INTEGER NOT NULL, current TEXT,
                state TEXT NOT NULL, seen REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS demands (
                session TEXT NOT NULL, path TEXT NOT NULL, rank INTEGER NOT NULL,
                PRIMARY KEY(session,path));
            CREATE TABLE IF NOT EXISTS tasks (
                path TEXT PRIMARY KEY, state TEXT NOT NULL, attempts INTEGER NOT NULL,
                ready REAL NOT NULL, error TEXT, updated REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY, paused INTEGER NOT NULL);
            INSERT OR IGNORE INTO settings VALUES (1,0);
        """)
        # Old sessions cannot authorize downloads until fresh playback evidence.
        self.db.execute("UPDATE sessions SET state='stale' WHERE state IN ('active','paused')")
        self.db.execute("DELETE FROM demands")
        self.db.execute("UPDATE tasks SET state='cancelled' WHERE state IN ('queued','downloading','blocked')")
        self.db.commit()
        self._paused = bool(self.db.execute("SELECT paused FROM settings WHERE id=1").fetchone()[0])

    def event(self, event):
        if not isinstance(event, dict):
            raise ValueError("event must be an object")
        session = identifier(event.get("session_id"))
        seq = event.get("sequence")
        phase = event.get("phase")
        if type(seq) is not int or not 0 <= seq < 2**63:
            raise ValueError("sequence must be a nonnegative 63-bit integer")
        if phase not in ("start", "progress", "pause", "stop"):
            raise ValueError("invalid playback phase")
        if phase == "stop":
            current, targets = None, []
        else:
            current, targets = plan_window(event.get("current"), event.get("episodes"),
                                           event.get("strategy", "ahead"), event.get("ahead", 2))
        now = self.clock()
        with self._cv:
            if self._stopping:
                raise RuntimeError("worker is stopping")
            old = self.db.execute("SELECT seq,current,state FROM sessions WHERE id=?", (session,)).fetchone()
            if old and seq <= old[0]:
                return {"result": "ignored", "reason": "stale-or-duplicate"}
            if not old and self.db.execute("SELECT COUNT(*) FROM sessions").fetchone()[0] >= 128:
                raise ValueError("session record limit reached")
            known = {row[0] for row in self.db.execute("SELECT path FROM tasks")}
            if len(known | {e.path for e in targets}) > 10000:
                raise ValueError("task history limit reached")
            state = "stopped" if phase == "stop" else "paused" if phase == "pause" else "active"
            new_path = current.path if current else None
            new_window = not old or old[1] != new_path or old[2] in ("stopped", "stale", "expired")
            with self.db:
                self.db.execute("INSERT OR REPLACE INTO sessions VALUES (?,?,?,?,?)",
                                (session, seq, new_path, state, now))
                self.db.execute("DELETE FROM demands WHERE session=?", (session,))
                for rank, item in enumerate(targets, 1):
                    self.db.execute("INSERT OR REPLACE INTO demands VALUES (?,?,?)", (session, item.path, rank))
                    row = self.db.execute("SELECT state FROM tasks WHERE path=?", (item.path,)).fetchone()
                    if not row:
                        self.db.execute("INSERT INTO tasks VALUES (?,?,?,?,?,?)",
                                        (item.path, "queued", 0, now, None, now))
                    elif row[0] == "cancelled" or (row[0] == "complete" and new_window):
                        self.db.execute("UPDATE tasks SET state='queued',attempts=0,ready=?,error=NULL,updated=? WHERE path=?",
                                        (now, now, item.path))
                self._cancel_unwanted()
            if session in self._playing and (phase == "stop" or not old or old[1] != new_path):
                self.store.unprotect_media(self._playing.pop(session))
            if current and session not in self._playing:
                self._playing[session] = self.store.protect_media(new_path)
            self._cv.notify_all()
            return {"result": "accepted", "targets": [e.path for e in targets]}

    def _cancel_unwanted(self):
        self.db.execute("""UPDATE tasks SET state='cancelled' WHERE state IN ('queued','blocked')
            AND NOT EXISTS (SELECT 1 FROM demands WHERE demands.path=tasks.path)""")

    def _expire(self):
        cutoff = self.clock() - self.timeout
        expired = [s for (s,) in self.db.execute(
            "SELECT id FROM sessions WHERE state IN ('active','paused') AND seen<?", (cutoff,))]
        with self.db:
            for session in expired:
                self.db.execute("UPDATE sessions SET state='expired' WHERE id=?", (session,))
                self.db.execute("DELETE FROM demands WHERE session=?", (session,))
                if session in self._playing:
                    self.store.unprotect_media(self._playing.pop(session))
            self._cancel_unwanted()

    def pause(self, value=True):
        if type(value) is not bool:
            raise ValueError("pause value must be boolean")
        with self._cv:
            self._paused = value
            with self.db:
                self.db.execute("UPDATE settings SET paused=? WHERE id=1", (int(value),))
            self._cv.notify_all()

    def retry(self, path):
        path = relative_path(path)
        with self._cv:
            with self.db:
                changed = self.db.execute("""UPDATE tasks SET state='queued',attempts=0,ready=?,error=NULL
                    WHERE path=? AND state IN ('failed','blocked')
                    AND EXISTS (SELECT 1 FROM demands WHERE demands.path=tasks.path)""",
                    (self.clock(), path)).rowcount
            self._cv.notify_all()
            return {"retried": bool(changed)}

    def status(self):
        with self._cv:
            self._expire()
            tasks = [dict(zip(("path", "state", "attempts", "ready", "error"), row)) for row in
                     self.db.execute("SELECT path,state,attempts,ready,error FROM tasks ORDER BY updated DESC LIMIT 500")]
            return {"paused": self._paused, "bytes_per_second": self.rate, "tasks": tasks,
                    "task_count": self.db.execute("SELECT COUNT(*) FROM tasks").fetchone()[0],
                    "sessions": self.db.execute("SELECT COUNT(*) FROM sessions WHERE state IN ('active','paused')").fetchone()[0]}

    def _checkpoint(self):
        with self._cv:
            while self._paused and not self._stopping:
                self._expire()
                self._cv.wait(0.25)
            if self._stopping:
                raise CopyCancelled("worker shutdown")

    def run_once(self):
        if not self._execution.acquire(blocking=False):
            return False
        try:
            with self._cv:
                self._expire()
                if self._paused or self._stopping:
                    return False
                rows = list(self.db.execute("""SELECT t.path,d.session,d.rank,t.updated
                    FROM tasks t JOIN demands d ON t.path=d.path JOIN sessions s ON s.id=d.session
                    WHERE t.state IN ('queued','blocked') AND t.ready<=? AND s.state='active'""", (self.clock(),)))
                if not rows:
                    return False
                path, owner, _, _ = min(rows, key=lambda r: (r[2], self._served.get(r[1], 0), r[3], r[0]))
                self._counter += 1
                self._served[owner] = self._counter
                with self.db:
                    self.db.execute("UPDATE tasks SET state='downloading',attempts=attempts+1,updated=? WHERE path=?",
                                    (self.clock(), path))
            try:
                result = self.store.prefetch(path, bytes_per_second=self.rate, checkpoint=self._checkpoint)
                state, error, delay, undo_attempt = ("complete", None, 0, False) if result == "complete" else ("queued", result, 1, True)
            except CopyCancelled:
                state, error, delay, undo_attempt = "queued", "shutdown", 0, True
            except CapacityError:
                state, error, delay, undo_attempt = "blocked", "no-safe-space", 10, True
            except Exception as failure:
                with self._cv:
                    attempts = self.db.execute("SELECT attempts FROM tasks WHERE path=?", (path,)).fetchone()[0]
                state = "failed" if attempts >= self.max_attempts or isinstance(failure, ValueError) else "queued"
                error = type(failure).__name__  # Never expose credentials or full source errors.
                delay, undo_attempt = min(60, 2 ** attempts), False
            with self._cv:
                wanted = self.db.execute("SELECT 1 FROM demands WHERE path=? LIMIT 1", (path,)).fetchone()
                if not wanted and state not in ("complete", "failed"):
                    state = "cancelled"
                with self.db:
                    self.db.execute("UPDATE tasks SET state=?,error=?,ready=?,attempts=attempts-?,updated=? WHERE path=?",
                                    (state, error, self.clock() + delay, int(undo_attempt), self.clock(), path))
                self._cv.notify_all()
            return True
        finally:
            self._execution.release()

    def start(self):
        with self._cv:
            if self._thread or self._stopping:
                raise RuntimeError("worker already started or stopping")
            def loop():
                while not self._stopping:
                    if not self.run_once():
                        with self._cv:
                            self._cv.wait(0.25)
            self._thread = threading.Thread(target=loop, name="prefetch-worker", daemon=True)
            self._thread.start()

    def close(self):
        with self._cv:
            self._stopping = True
            self._cv.notify_all()
        if self._thread:
            self._thread.join(5)
            if self._thread.is_alive():
                raise RuntimeError("source I/O has not returned; worker cannot close yet")
        if self._execution.locked():
            raise RuntimeError("cannot close during a manual execution")
        with self._cv:
            for token in self._playing.values():
                self.store.unprotect_media(token)
            self._playing.clear()
            self.db.close()
