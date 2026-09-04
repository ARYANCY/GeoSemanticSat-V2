"""Build a local FAISS cosine-similarity index from persisted embeddings."""
import faiss
import numpy as np
from app.core.config import settings
from app.db.session import SessionLocal
from app.models.entities import Embedding

db = SessionLocal()
rows = db.query(Embedding).all()
if not rows:
    raise SystemExit("No embeddings available. Ingest GeoTIFFs first.")
vectors = np.asarray([row.vector for row in rows], dtype=np.float32)
faiss.normalize_L2(vectors)
index = faiss.IndexFlatIP(vectors.shape[1])
index.add(vectors)
faiss.write_index(index, str(settings.index_root / "observations.faiss"))
(settings.index_root / "observation_ids.txt").write_text("\n".join(row.observation_id for row in rows), encoding="utf-8")
print(f"Wrote {index.ntotal} vectors to {settings.index_root}")
