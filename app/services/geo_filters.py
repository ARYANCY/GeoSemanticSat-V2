"""Spatial and temporal filter helpers for retrieval (no fabricated geometry)."""
from __future__ import annotations

import re
from datetime import date
from typing import Any

from fastapi import HTTPException, status


_NUM = r"[-+]?\d+(?:\.\d+)?"
_PAIR = re.compile(rf"({_NUM})\s+({_NUM})")


def validate_date_range(date_from: date | None, date_to: date | None) -> None:
    if date_from is not None and date_to is not None and date_from > date_to:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Invalid date range: date_from must be on or before date_to",
        )


def parse_wkt_envelope(wkt: str | None) -> tuple[float, float, float, float] | None:
    """Return (minx, miny, maxx, maxy) from a POLYGON/POINT WKT, or None."""
    if not wkt:
        return None
    pairs = [(float(x), float(y)) for x, y in _PAIR.findall(wkt)]
    if not pairs:
        return None
    xs = [p[0] for p in pairs]
    ys = [p[1] for p in pairs]
    return min(xs), min(ys), max(xs), max(ys)


def centroid_from_wkt(wkt: str | None) -> tuple[float, float]:
    env = parse_wkt_envelope(wkt)
    if env is None:
        return (0.0, 0.0)
    minx, miny, maxx, maxy = env
    return ((minx + maxx) / 2.0, (miny + maxy) / 2.0)


def aoi_to_envelope(aoi: dict | str | None) -> tuple[float, float, float, float] | None:
    if aoi is None:
        return None
    if isinstance(aoi, str):
        env = parse_wkt_envelope(aoi)
        if env is None:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="Invalid AOI: expected WKT POLYGON/POINT or GeoJSON-like object",
            )
        return env
    if not isinstance(aoi, dict):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Invalid AOI: unsupported type",
        )
    if all(k in aoi for k in ("minx", "miny", "maxx", "maxy")):
        return float(aoi["minx"]), float(aoi["miny"]), float(aoi["maxx"]), float(aoi["maxy"])
    if all(k in aoi for k in ("min_lon", "min_lat", "max_lon", "max_lat")):
        return (
            float(aoi["min_lon"]),
            float(aoi["min_lat"]),
            float(aoi["max_lon"]),
            float(aoi["max_lat"]),
        )
    coords = aoi.get("coordinates")
    if aoi.get("type") == "Polygon" and coords:
        ring = coords[0]
        xs = [float(p[0]) for p in ring]
        ys = [float(p[1]) for p in ring]
        return min(xs), min(ys), max(xs), max(ys)
    if aoi.get("type") == "Point" and coords:
        x, y = float(coords[0]), float(coords[1])
        return x, y, x, y
    raise HTTPException(
        status_code=status.HTTP_400_BAD_REQUEST,
        detail="Invalid AOI: provide WKT, bbox keys, or GeoJSON Polygon/Point",
    )


def envelopes_intersect(
    a: tuple[float, float, float, float] | None,
    b: tuple[float, float, float, float] | None,
) -> bool:
    if a is None or b is None:
        return True
    return not (a[2] < b[0] or b[2] < a[0] or a[3] < b[1] or b[3] < a[1])


def observation_in_aoi(footprint_wkt: str, aoi: dict | str | None) -> bool:
    aoi_env = aoi_to_envelope(aoi) if aoi is not None else None
    if aoi_env is None:
        return True
    obs_env = parse_wkt_envelope(footprint_wkt)
    if obs_env is None:
        return False
    return envelopes_intersect(obs_env, aoi_env)
