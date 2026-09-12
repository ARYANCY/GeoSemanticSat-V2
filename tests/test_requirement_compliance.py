"""Unit tests for onset CUSUM and AOI helpers — no fabricated satellite labels."""
from __future__ import annotations

from datetime import date

import pytest
from fastapi import HTTPException

from app.services.geo_filters import aoi_to_envelope, observation_in_aoi, validate_date_range
from app.services.onset import estimate_onset
from app.services.quality import estimate_quality_score
import numpy as np


def test_onset_requires_three_observations():
    result = estimate_onset([
        ("a", date(2025, 1, 1), [0.0] * 128),
        ("b", date(2025, 2, 1), [0.0] * 128),
    ])
    assert result["change_detected"] is False
    assert result["onset_observation_id"] is None
    assert result["reason"] == "insufficient_temporal_sequence"


def test_onset_does_not_use_last_scene_when_threshold_not_crossed():
    series = []
    for i, d in enumerate([date(2025, 1, 1), date(2025, 2, 1), date(2025, 3, 1), date(2025, 4, 1)]):
        vec = [0.0] * 128
        vec[18] = 0.01 * i
        series.append((f"obs-{i}", d, vec))
    result = estimate_onset(series, threshold=5.0)
    assert result["change_detected"] is False
    assert result["onset_observation_id"] is None


def test_onset_returns_first_supported_observation():
    series = []
    for i, d in enumerate([date(2025, 1, 1), date(2025, 2, 1), date(2025, 3, 1), date(2025, 4, 1)]):
        vec = [0.0] * 128
        vec[18] = 0.0 if i < 2 else 0.9
        series.append((f"obs-{i}", d, vec))
    result = estimate_onset(series, threshold=0.18, drift=0.02)
    assert result["change_detected"] is True
    assert result["onset_observation_id"] == "obs-2"
    assert result["onset_date"] == "2025-03-01"


def test_validate_date_range():
    validate_date_range(date(2025, 1, 1), date(2025, 2, 1))
    with pytest.raises(HTTPException):
        validate_date_range(date(2025, 3, 1), date(2025, 2, 1))


def test_aoi_intersection():
    wkt = "POLYGON((92.0 27.0, 92.1 27.0, 92.1 27.1, 92.0 27.1, 92.0 27.0))"
    assert observation_in_aoi(wkt, {"min_lon": 91.9, "min_lat": 26.9, "max_lon": 92.2, "max_lat": 27.2})
    assert not observation_in_aoi(wkt, {"min_lon": 0.0, "min_lat": 0.0, "max_lon": 1.0, "max_lat": 1.0})
    with pytest.raises(HTTPException):
        aoi_to_envelope("garbage")


def test_quality_score_bright_scene_is_downweighted():
    cloudy = np.ones((3, 16, 16), dtype=np.float32)
    clear = np.full((3, 16, 16), 0.2, dtype=np.float32)
    assert estimate_quality_score(cloudy)["quality_score"] < estimate_quality_score(clear)["quality_score"]
