"""Comprehensive integration and smoke tests for offline satellite intelligence API."""
from __future__ import annotations

import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import pytest
import numpy as np
from fastapi.testclient import TestClient

from app.core.config import settings
from app.db.session import Base, engine
from app.main import app

try:
    import rasterio
    from rasterio.transform import from_origin
    RASTERIO_AVAILABLE = True
except ImportError:
    rasterio = None
    from_origin = None
    RASTERIO_AVAILABLE = False


class MockRasterDataset:
    def __init__(self, bands: int = 3, height: int = 64, width: int = 64):
        self.count = bands
        self.height = height
        self.width = width
        self.crs = "EPSG:4326"
        self.bounds = (92.0, 27.0, 92.1, 27.1)
        self._data = np.ones((bands, height, width), dtype=np.float32) * 500.0
        self._data[:, 30:50, 30:50] = 1500.0

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass

    def read(self, indexes=None, out_shape=None, masked=False):
        if out_shape:
            shape = out_shape
        elif indexes and isinstance(indexes, list):
            shape = (len(indexes), self.height, self.width)
        else:
            shape = (self.count, self.height, self.width)
        arr = np.resize(self._data, shape)
        if masked:
            import numpy.ma as ma
            return ma.masked_array(arr, mask=np.zeros(shape, dtype=bool))
        return arr


@pytest.fixture(scope="session", autouse=True)
def setup_test_environment(monkeypatch_session=None):
    """Set up database tables and test GeoTIFF rasters."""
    settings.ensure_directories()
    Base.metadata.drop_all(bind=engine)
    Base.metadata.create_all(bind=engine)

    before_path = settings.data_root / "test_before.tif"
    after_path = settings.data_root / "test_after.tif"
    single_path = settings.data_root / "test_singleband.tif"

    if RASTERIO_AVAILABLE:
        transform = from_origin(92.0, 27.0, 0.0001, 0.0001)

        # 3-band raster A (Before)
        base_3band = np.zeros((3, 64, 64), dtype=np.uint16)
        base_3band[:, 10:40, 10:40] = 500
        with rasterio.open(
            before_path, "w", driver="GTiff", height=64, width=64, count=3,
            dtype=base_3band.dtype, crs="EPSG:4326", transform=transform,
        ) as dst:
            dst.write(base_3band)

        # 3-band raster B (After)
        changed_3band = base_3band.copy()
        changed_3band[:, 35:55, 35:55] = 2000
        with rasterio.open(
            after_path, "w", driver="GTiff", height=64, width=64, count=3,
            dtype=changed_3band.dtype, crs="EPSG:4326", transform=transform,
        ) as dst:
            dst.write(changed_3band)

        # 1-band raster C
        single_band = np.zeros((1, 64, 64), dtype=np.uint16)
        single_band[0, 15:45, 15:45] = 800
        with rasterio.open(
            single_path, "w", driver="GTiff", height=64, width=64, count=1,
            dtype=single_band.dtype, crs="EPSG:4326", transform=transform,
        ) as dst:
            dst.write(single_band)
    else:
        # Create dummy placeholder files for containment checks
        for p in [before_path, after_path, single_path]:
            p.write_bytes(b"GEOTIFF_PLACEHOLDER")

        # Mock rasterio.open in app.main
        import app.main as main_mod

        class MockRasterioMod:
            @staticmethod
            def open(path, *args, **kwargs):
                path_str = str(path)
                if "singleband" in path_str:
                    return MockRasterDataset(bands=1)
                elif "after" in path_str:
                    ds = MockRasterDataset(bands=3)
                    ds._data[:, 20:40, 20:40] = 2500.0
                    return ds
                return MockRasterDataset(bands=3)

        main_mod.rasterio = MockRasterioMod()
        main_mod.RASTERIO_AVAILABLE = True

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


def test_text_search_response(client):
    """Verify offline text search execution returns valid status."""
    response = client.post("/api/v1/search/text", json={"query": "deforestation area"})
    assert response.status_code in {200, 503}
    if response.status_code == 200:
        assert "results" in response.json()


def test_sovereign_token_auth_and_security(client):
    """Verify DPAPI token authorization gates access when required."""
    settings.require_token_auth = True
    settings.api_auth_token = "test-dpapi-token-xyz"

    try:
        # Unauthorized call should return 401
        unauth_resp = client.get("/api/v1/observations")
        assert unauth_resp.status_code == 401
        assert "Unauthorized" in unauth_resp.json()["detail"]

        # Authorized call should succeed
        auth_resp = client.get(
            "/api/v1/observations",
            headers={"Authorization": "Bearer test-dpapi-token-xyz"}
        )
        assert auth_resp.status_code == 200
    finally:
        settings.require_token_auth = False
        settings.api_auth_token = ""


def test_api_v1_health_and_system_status(client):
    """Verify standard /api/v1/health and /api/v1/system/status routes."""
    h_resp = client.get("/api/v1/health")
    assert h_resp.status_code == 200
    assert h_resp.json()["status"] == "ok"
    assert h_resp.json()["offline_mode"] is True

    s_resp = client.get("/api/v1/system/status")
    assert s_resp.status_code == 200
    assert s_resp.json()["database"] == "connected"


def test_observation_advanced_filter(client):
    """Verify /api/v1/search/filter filtering by sensor and quality."""
    resp = client.post("/api/v1/search/filter", json={"sensor": "Sentinel-2", "min_quality": 0.5})
    assert resp.status_code == 200
    assert isinstance(resp.json(), list)


def test_mission_lifecycle_and_execution(client):
    """Verify Mission creation, listing, retrieval, and run pipeline."""
    # 1. Create Mission
    mission_payload = {
        "name": "Border Surveillance Sector 4",
        "description": "Continuous monitoring of defensive perimeter",
        "aoi": {"type": "Polygon", "coordinates": [[[77.1, 28.5], [77.2, 28.5], [77.2, 28.6], [77.1, 28.6], [77.1, 28.5]]]},
        "semantic_query": "military vehicles and new structures",
        "quality_threshold": 0.5,
        "change_threshold": 0.3,
        "confidence_threshold": 0.5,
        "domain_pack": "Defence",
        "enabled": True,
    }
    create_resp = client.post("/api/v1/missions", json=mission_payload)
    assert create_resp.status_code == 201
    m_data = create_resp.json()
    assert "id" in m_data
    mission_id = m_data["id"]

    # 2. List Missions
    list_resp = client.get("/api/v1/missions")
    assert list_resp.status_code == 200
    assert any(m["id"] == mission_id for m in list_resp.json())

    # 3. Get Mission
    get_resp = client.get(f"/api/v1/missions/{mission_id}")
    assert get_resp.status_code == 200
    assert get_resp.json()["name"] == "Border Surveillance Sector 4"

    # 4. Run Mission
    run_resp = client.post(f"/api/v1/missions/{mission_id}/run")
    assert run_resp.status_code == 200
    run_data = run_resp.json()
    assert run_data["status"] == "completed"
    assert "alerts_generated" in run_data


def test_feedback_rocchio_tuning(client):
    """Verify analyst relevance feedback adjustment endpoint."""
    feedback_payload = {
        "query": "unauthorized construction near coastline",
        "positive_observation_ids": [],
        "negative_observation_ids": [],
        "alpha": 1.0,
        "beta": 0.75,
        "gamma": 0.25,
    }
    resp = client.post("/api/v1/feedback", json=feedback_payload)
    # May return 200 or 503 depending on whether active neural weights are staged
    assert resp.status_code in {200, 503}
    if resp.status_code == 200:
        assert resp.json()["status"] == "relevance_tuned"


def test_export_evidence_package(client, tmp_path):
    """Verify evidence data product generation and SHA-256 Merkle manifest."""
    export_payload = {
        "format": "all",
        "mission_id": "TEST_MISSION_ALPHA",
        "output_directory": str(tmp_path / "test_export"),
    }
    resp = client.post("/api/v1/export", json=export_payload)
    assert resp.status_code == 200
    data = resp.json()
    assert data["status"] == "success"
    assert "merkle_root_hash" in data
    assert len(data["merkle_root_hash"]) == 64
    assert "geojson" in data["files"]
    assert "stac" in data["files"]
    assert "html_briefing" in data["files"]
    assert "manifest" in data["files"]
    assert Path(data["files"]["manifest"]).is_file()
    geojson_path = Path(data["files"]["geojson"])
    geo = json.loads(geojson_path.read_text(encoding="utf-8"))
    for feat in geo.get("features", []):
        if feat.get("properties", {}).get("before_raster"):
            assert feat["geometry"]["coordinates"] != [0.0, 0.0]


def test_text_search_filters_and_spec_queries(client):
    """Semantic-axis text search plus date/sensor/AOI filters (not catalogue keyword match)."""
    river = client.post(
        "/api/v1/search/text",
        json={"query": "Newly built structures near a river", "top_k": 5},
    )
    assert river.status_code == 200
    assert isinstance(river.json()["results"], list)

    vehicles = client.post(
        "/api/v1/search/text",
        json={"query": "Large vehicle concentrations on open ground", "top_k": 5},
    )
    assert vehicles.status_code == 200

    dated = client.post(
        "/api/v1/search/text",
        json={
            "query": "new road construction",
            "top_k": 10,
            "sensor": "Sentinel-2",
            "date_from": "2025-01-01",
            "date_to": "2025-01-31",
        },
    )
    assert dated.status_code == 200
    for item in dated.json()["results"]:
        assert item["sensor"] == "Sentinel-2"
        assert item["acquisition_date"] <= "2025-01-31"

    bad_range = client.post(
        "/api/v1/search/text",
        json={"query": "water", "date_from": "2026-01-01", "date_to": "2025-01-01"},
    )
    assert bad_range.status_code == 400

    aoi = client.post(
        "/api/v1/search/text",
        json={
            "query": "recently cleared land",
            "aoi": {"min_lon": 91.9, "min_lat": 26.9, "max_lon": 92.2, "max_lat": 27.2},
        },
    )
    assert aoi.status_code == 200

    miss = client.post(
        "/api/v1/search/text",
        json={
            "query": "expanded water body",
            "aoi": {"min_lon": 0.0, "min_lat": 0.0, "max_lon": 0.1, "max_lat": 0.1},
        },
    )
    assert miss.status_code == 200
    assert miss.json()["results"] == []


def test_invalid_aoi_fails_safely(client):
    resp = client.post("/api/v1/search/text", json={"query": "river", "aoi": "not-a-geometry"})
    assert resp.status_code == 400


def test_duplicate_ingest_does_not_rebuild(client):
    first = client.post(
        "/api/v1/ingest",
        json={
            "path": "test_before.tif",
            "sensor": "Sentinel-2",
            "acquisition_date": "2025-01-01",
            "location_name": "Test Site Alpha",
            "source": "ESA",
        },
    )
    assert first.status_code == 201
    assert first.json()["status"] == "duplicate"
    obs_id = first.json()["observation_id"]

    ids_file = settings.index_root / "observation_ids.txt"
    if ids_file.is_file():
        ids = ids_file.read_text(encoding="utf-8").splitlines()
        assert ids.count(obs_id) <= 1


def test_change_evidence_includes_ndwi_and_onset(client):
    obs_list = client.get("/api/v1/observations").json()
    loc_groups: dict[str, list[str]] = {}
    for obs in obs_list:
        loc_groups.setdefault(obs["location_id"], []).append(obs["id"])
    same_loc_obs = next(ids for ids in loc_groups.values() if len(ids) >= 2)
    resp = client.post(
        "/api/v1/change/analyze",
        json={
            "before_observation_id": same_loc_obs[1],
            "after_observation_id": same_loc_obs[0],
            "use_temporal_sequence": True,
        },
    )
    assert resp.status_code == 201
    evidence = resp.json()["evidence"]
    assert "delta_ndwi" in evidence
    assert "onset" in evidence
    assert "observations_used" in evidence["onset"]
    assert evidence["mask_available"] is True


