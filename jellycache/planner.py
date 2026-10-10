"""Plan only from explicit same-season episode metadata, never filenames."""
from dataclasses import dataclass
from pathlib import PurePosixPath


def relative_path(value):
    if not isinstance(value, str) or not value or len(value) > 4096 or "\\" in value or "\x00" in value:
        raise ValueError("invalid relative media path")
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or not path.parts:
        raise ValueError("expected relative media path without ..")
    # Windows drive paths are not permitted in the portable event contract.
    if ":" in path.parts[0]:
        raise ValueError("drive paths are not permitted")
    return str(path)


def identifier(value):
    if not isinstance(value, str) or not value or len(value) > 128 or any(ord(c) < 32 for c in value):
        raise ValueError("invalid identifier")
    return value


@dataclass(frozen=True)
class Episode:
    id: str
    series_id: str
    season_id: str
    index: int
    path: str

    @classmethod
    def from_dict(cls, item):
        if not isinstance(item, dict) or item.get("type") != "Episode":
            raise ValueError("only explicit Episode metadata is supported")
        index = item.get("index")
        if type(index) is not int or index <= 0:
            raise ValueError("ordinary episode numbering must be a positive integer")
        season_number = item.get("season_number", 1)
        if type(season_number) is not int or season_number <= 0 or item.get("index_end", index) != index:
            raise ValueError("specials and combined episode ranges need an explicit future policy")
        return cls(identifier(item.get("id")), identifier(item.get("series_id")),
                   identifier(item.get("season_id")), index, relative_path(item.get("path")))


def plan_window(current, episodes, strategy="ahead", ahead=2):
    if strategy not in ("next", "ahead", "season"):
        raise ValueError("unknown strategy")
    if type(ahead) is not int or not 1 <= ahead <= 50:
        raise ValueError("ahead must be between 1 and 50")
    if not isinstance(episodes, list) or len(episodes) > 1000:
        raise ValueError("bounded, complete season metadata is required")
    current = Episode.from_dict(current)
    season = []
    for item in episodes:
        # Explicitly ignore other seasons/series rather than crossing boundaries.
        if not isinstance(item, dict):
            raise ValueError("invalid episode metadata")
        if item.get("series_id") == current.series_id and item.get("season_id") == current.season_id:
            season.append(Episode.from_dict(item))
    if len({e.index for e in season}) != len(season) or len({e.id for e in season}) != len(season):
        raise ValueError("ambiguous episode ordering or identity")
    matches = [e for e in season if e.id == current.id]
    if len(matches) != 1 or matches[0] != current:
        raise ValueError("current episode must match complete season metadata")
    following = sorted((e for e in season if e.index > current.index), key=lambda e: e.index)
    count = 1 if strategy == "next" else ahead
    return current, following if strategy == "season" else following[:count]
