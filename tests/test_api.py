"""Comprehensive integration and smoke tests for offline satellite intelligence API."""
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import pytest
import numpy as np
import rasterio
from rasterio.transform import from_origin
from fastapi.testclient import TestClient

from app.core.config import settings
from app.db.session import Base, engine
from app.main import app


@pytest.fixture(scope="session", autouse=True)
def setup_test_environment():
    """Set up database tables and test GeoTIFF rasters."""
    settings.ensure_directories()
    Base.metadata.drop_all(bind=engine)
    Base.metadata.create_all(bind=engine)

    # Generate sample GeoTIFFs
    transform = from_origin(92.0, 27.0, 0.0001, 0.0001)

    # 3-band raster A (Before)
    base_3band = np.zeros((3, 64, 64), dtype=np.uint16)
    base_3band[:, 10:40, 10:40] = 500
    with rasterio.open(
        settings.data_root / "test_before.tif",
        "w",
        driver="GTiff",
        height=64,
        width=64,
        count=3,
        dtype=base_3band.dtype,
        crs="EPSG:4326",
        transform=transform,
    ) as dst:
        dst.write(base_3band)

    # 3-band raster B (After)
    changed_3band = base_3band.copy()
    changed_3band[:, 35:55, 35:55] = 2000
    with rasterio.open(
        settings.data_root / "test_after.tif",
        "w",
        driver="GTiff",
        height=64,
        width=64,
        count=3,
        dtype=changed_3band.dtype,
        crs="EPSG:4326",
        transform=transform,
    ) as dst:
        dst.write(changed_3band)

    # 1-band raster C (Single-band SAR/Panchromatic to test dimension safety)
    single_band = np.zeros((1, 64, 64), dtype=np.uint16)
    single_band[0, 15:45, 15:45] = 800
    with rasterio.open(
        settings.data_root / "test_singleband.tif",
        "w",
        driver="GTiff",
        height=64,
        width=64,
        count=1,
        dtype=single_band.dtype,
        crs="EPSG:4326",
        transform=transform,
    ) as dst:
        dst.write(single_band)

    yield


@pytest.fixture
def client():
    """Create FastAPI test client."""
    with TestClient(app) as test_client:
        yield test_client


def test_health(client):
    """Verify health endpoint."""
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "offline_mode": True}


def test_system_status(client):
    """Verify system status report."""
    response = client.get("/system/status")
    assert response.status_code == 200
    data = response.json()
    assert data["database"] == "connected"
    assert "observations" in data
    assert "embeddings" in data


def test_ingest_and_dimension_safety(client):
    """Ingest both 3-band and 1-band rasters and verify fixed 96-dim vector safety."""
    # Ingest 3-band Before
    r1 = client.post(
        "/api/v1/ingest",
        json={
            "path": "test_before.tif",
            "sensor": "Sentinel-2",
            "acquisition_date": "2025-01-01",
            "location_name": "Test Site Alpha",
            "source": "ESA",
        },
    )
    assert r1.status_code == 201
    obs1_id = r1.json()["observation_id"]
    loc1_id = r1.json()["location_id"]

    # Ingest 3-band After
    r2 = client.post(
        "/api/v1/ingest",
        json={
            "path": "test_after.tif",
            "sensor": "Sentinel-2",
            "acquisition_date": "2025-02-01",
            "location_name": "Test Site Alpha",
            "source": "ESA",
        },
    )
    assert r2.status_code == 201
    obs2_id = r2.json()["observation_id"]

    # Ingest 1-band SAR
    r3 = client.post(
        "/api/v1/ingest",
        json={
            "path": "test_singleband.tif",
            "sensor": "Sentinel-1",
            "acquisition_date": "2025-01-15",
            "location_name": "Test Site Beta",
            "source": "ESA",
        },
    )
    assert r3.status_code == 201
    obs3_id = r3.json()["observation_id"]

    # Verify search across mixed 1-band and 3-band does NOT crash with dimension mismatch
    search_resp = client.post(
        "/api/v1/search/image",
        json={"observation_id": obs1_id, "top_k": 5},
    )
    assert search_resp.status_code == 200
    results = search_resp.json()["results"]
    assert len(results) >= 2
    # Ensure scores are valid floats
    for item in results:
        assert 0.0 <= item["score"] <= 1.0


def test_path_traversal_prevention(client):
    """Verify that path traversal attempts are rejected with 400 Bad Request."""
    response = client.post(
        "/api/v1/ingest",
        json={
            "path": "../secret_file.tif",
            "sensor": "Sentinel-2",
            "acquisition_date": "2025-01-01",
        },
    )
    assert response.status_code == 400
    assert "required" in response.json()["detail"].lower()


def test_change_detection_and_review_workflow(client):
    """Test full change detection analysis, review submission, and provenance retrieval."""
    # Retrieve observations and group by location
    obs_list = client.get("/api/v1/observations").json()
    assert len(obs_list) >= 2

    # Find two observations with the same location_id
    loc_groups: dict[str, list[str]] = {}
    for obs in obs_list:
        loc_groups.setdefault(obs["location_id"], []).append(obs["id"])

    same_loc_obs = next(ids for ids in loc_groups.values() if len(ids) >= 2)
    obs_before, obs_after = same_loc_obs[1], same_loc_obs[0]

    # Execute change analysis
    change_resp = client.post(
        "/api/v1/change/analyze",
        json={
            "before_observation_id": obs_before,
            "after_observation_id": obs_after,
        },
    )
    assert change_resp.status_code == 201
    change_data = change_resp.json()
    change_id = change_data["change_id"]
    assert "class" in change_data
    assert "confidence" in change_data
    assert "spectral_difference" in change_data["evidence"]

    # Get Change Event Details
    event_resp = client.get(f"/api/v1/change/{change_id}")
    assert event_resp.status_code == 200
    assert event_resp.json()["id"] == change_id

    # Get Change Provenance
    prov_resp = client.get(f"/api/v1/change/{change_id}/provenance")
    assert prov_resp.status_code == 200
    assert prov_resp.json()["change_id"] == change_id
    assert "algorithm" in prov_resp.json()["run"]

    # Submit Analyst Review
    review_resp = client.post(
        f"/api/v1/change/{change_id}/review",
        json={
            "analyst": "Officer_A",
            "decision": "confirmed",
            "note": "Significant spectral change confirmed in northeast sector",
        },
    )
    assert review_resp.status_code == 201
    assert "review_id" in review_resp.json()

    # List Reviews with pagination
    reviews = client.get("/api/v1/reviews?limit=10&offset=0")
    assert reviews.status_code == 200
    assert len(reviews.json()) >= 1
    assert reviews.json()[0]["decision"] == "confirmed"


def test_locations_and_timeline(client):
    """Test location details, timeline, and similar locations search."""
    obs_list = client.get("/api/v1/observations").json()
    loc_id = obs_list[0]["location_id"]

    # Location details
    loc_resp = client.get(f"/api/v1/locations/{loc_id}")
    assert loc_resp.status_code == 200
    assert loc_resp.json()["id"] == loc_id
    assert loc_resp.json()["observations"] >= 1

    # Location timeline
    timeline_resp = client.get(f"/api/v1/locations/{loc_id}/timeline")
    assert timeline_resp.status_code == 200
    assert isinstance(timeline_resp.json(), list)

    # Similar locations
    sim_resp = client.post(
        "/api/v1/similar-locations",
        json={"location_id": loc_id, "top_k": 5},
    )
    assert sim_resp.status_code == 200
    assert "results" in sim_resp.json()


def test_text_search_503(client):
    """Verify that offline text search properly returns 503 until model weights are staged."""
    response = client.post("/api/v1/search/text", json={"query": "deforestation area"})
    assert response.status_code == 503
