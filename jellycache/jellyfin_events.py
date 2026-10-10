"""Translate supplied Jellyfin DTO snapshots, not a live event subscription."""
from pathlib import PurePosixPath

from .planner import identifier, relative_path


def mapped_path(value, media_root="/video"):
    if not isinstance(value, str) or not value.startswith("/") or ".." in PurePosixPath(value).parts:
        raise ValueError("Jellyfin media path must be absolute and unambiguous")
    root = PurePosixPath(media_root)
    if not root.is_absolute() or ".." in root.parts:
        raise ValueError("invalid Jellyfin media root")
    try:
        relative = PurePosixPath(value).relative_to(root)
    except ValueError:
        raise ValueError("Jellyfin path is outside configured media root") from None
    return relative_path(str(relative))


def episode_dto(item, media_root="/video", selected_source=None):
    if not isinstance(item, dict) or item.get("Type") != "Episode":
        raise ValueError("Jellyfin item is not an Episode")
    if item.get("IsVirtualItem") or item.get("IsMissing") or item.get("LocationType") == "Virtual":
        raise ValueError("virtual/missing episodes cannot be prefetched")
    sources = item.get("MediaSources") or []
    if not isinstance(sources, list):
        raise ValueError("invalid media source metadata")
    if len(sources) > 1:
        matches = [s for s in sources if isinstance(s, dict) and s.get("Id") == selected_source] if selected_source else []
        if len(matches) != 1:
            raise ValueError("multiple media versions require explicit source selection")
        path = matches[0].get("Path")
    elif sources:
        if not isinstance(sources[0], dict):
            raise ValueError("invalid media source")
        if selected_source and sources[0].get("Id") != selected_source:
            raise ValueError("selected source does not match episode source")
        path = sources[0].get("Path") or item.get("Path")
    else:
        if selected_source:
            raise ValueError("selected source requires explicit media source metadata")
        path = item.get("Path")
    index = item.get("IndexNumber")
    end = item.get("IndexNumberEnd")
    return {"type": "Episode", "id": identifier(item.get("Id")),
            "series_id": identifier(item.get("SeriesId")),
            "season_id": identifier(item.get("SeasonId")), "index": index,
            "index_end": index if end is None else end,
            "season_number": item.get("ParentIndexNumber"),
            "path": mapped_path(path, media_root)}


def normalized_event(payload, media_root="/video"):
    """Envelope: sequence, phase, session DTO, complete season episodes DTOs."""
    if not isinstance(payload, dict) or not isinstance(payload.get("session"), dict):
        raise ValueError("Jellyfin session envelope required")
    session = payload["session"]
    event = {"session_id": identifier(session.get("Id")),
             "sequence": payload.get("sequence"), "phase": payload.get("phase")}
    if event["phase"] not in ("start", "progress", "pause", "stop"):
        raise ValueError("invalid playback phase")
    playing = session.get("NowPlayingItem")
    if playing is not None and not isinstance(playing, dict):
        raise ValueError("invalid now-playing metadata")
    play_state = session.get("PlayState") or {}
    if not isinstance(play_state, dict):
        raise ValueError("invalid play state")
    if event["phase"] == "stop" or not playing or playing.get("Type") != "Episode":
        event["phase"] = "stop"
        return event
    if play_state.get("IsPaused"):
        event["phase"] = "pause"
    episodes = payload.get("episodes")
    if not isinstance(episodes, list) or len(episodes) > 1000:
        raise ValueError("complete bounded season metadata required")
    current_id = playing.get("Id")
    matches = [item for item in episodes if isinstance(item, dict) and item.get("Id") == current_id]
    if len(matches) != 1:
        raise ValueError("now-playing item must uniquely match season metadata")
    selected_source = play_state.get("MediaSourceId")
    current = episode_dto(matches[0], media_root, selected_source)
    normalized = []
    for item in episodes:
        if not isinstance(item, dict):
            raise ValueError("invalid episode DTO")
        if item.get("SeriesId") != current["series_id"] or item.get("SeasonId") != current["season_id"]:
            continue
        if item.get("IsVirtualItem") or item.get("IsMissing") or item.get("LocationType") == "Virtual":
            continue
        normalized.append(current if item.get("Id") == current_id else episode_dto(item, media_root))
    event.update(current=current, episodes=normalized,
                 strategy=payload.get("strategy", "ahead"), ahead=payload.get("ahead", 2))
    return event
