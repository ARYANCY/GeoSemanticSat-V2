"""Sequential CUSUM onset from ordered observation embeddings (not a pair-only date)."""
from __future__ import annotations

from datetime import date
from typing import Any

import numpy as np


def estimate_onset(
    dated_vectors: list[tuple[str, date, list[float] | np.ndarray]],
    threshold: float = 0.18,
    drift: float = 0.02,
) -> dict[str, Any]:
    """Return earliest observation whose CUSUM on built-up axis (dim 18) crosses threshold.

    Requires at least three usable observations. Does not invent dates: if the
    threshold is never crossed, change_detected is False and onset_observation_id
    is None.
    """
    ordered = sorted(dated_vectors, key=lambda item: item[1])
    if len(ordered) < 3:
        return {
            "change_detected": False,
            "onset_observation_id": None,
            "onset_date": None,
            "observations_used": len(ordered),
            "reason": "insufficient_temporal_sequence",
            "cusum_path": [],
        }

    values: list[float] = []
    for _, _, vec in ordered:
        arr = np.asarray(vec, dtype=np.float32)
        values.append(float(arr[18]) if arr.size > 18 else float(arr.mean()))

    baseline = values[0]
    s = 0.0
    path = [0.0]
    onset_idx: int | None = None
    for i in range(1, len(values)):
        s = max(0.0, s + (values[i] - baseline) - drift)
        path.append(round(s, 6))
        if onset_idx is None and s >= threshold:
            onset_idx = i

    if onset_idx is None:
        return {
            "change_detected": False,
            "onset_observation_id": None,
            "onset_date": None,
            "observations_used": len(ordered),
            "reason": "cusum_threshold_not_crossed",
            "cusum_path": path,
            "metric_series": [round(v, 6) for v in values],
        }

    obs_id, obs_date, _ = ordered[onset_idx]
    return {
        "change_detected": True,
        "onset_observation_id": obs_id,
        "onset_date": obs_date.isoformat(),
        "observations_used": len(ordered),
        "reason": "cusum_threshold_crossed",
        "cusum_path": path,
        "metric_series": [round(v, 6) for v in values],
        "threshold": threshold,
    }
