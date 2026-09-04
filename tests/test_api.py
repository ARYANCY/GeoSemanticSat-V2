from fastapi.testclient import TestClient
from app.db.session import Base, engine
from app.main import app

Base.metadata.create_all(engine)
client = TestClient(app)

def test_health():
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json()["offline_mode"] is True

def test_text_search_requires_local_model():
    response = client.post("/api/v1/search/text", json={"query": "new road"})
    assert response.status_code == 503
