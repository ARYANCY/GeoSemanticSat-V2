"""Build a local FAISS cosine-similarity index from persisted embeddings."""
from __future__ import annotations

import sys
from pathlib import Path

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import faiss
import numpy as np
from app.core.config import settings
from app.core.logging import logger
from app.db.session import SessionLocal
from app.models.entities import Embedding


def build_faiss_index() -> None:
    """Read stored embeddings from the database and write a FAISS FlatIP index."""
    settings.index_root.mkdir(parents=True, exist_ok=True)

    with SessionLocal() as db:
        rows = db.query(Embedding).all()
        if not rows:
            logger.warning("No embeddings available in database. Ingest GeoTIFFs first.")
            print("No embeddings available. Ingest GeoTIFFs first.")
            return

        vectors_list: list[list[float]] = []
        valid_obs_ids: list[str] = []
        expected_dim = None

        for row in rows:
            if not row.vector:
                continue
            dim = len(row.vector)
            if expected_dim is None:
                expected_dim = dim
            elif dim != expected_dim:
                logger.warning(
                    f"Skipping observation {row.observation_id} due to dimension mismatch ({dim} != {expected_dim})"
                )
                continue

            vectors_list.append(row.vector)
            valid_obs_ids.append(row.observation_id)

        if not vectors_list:
            print("No valid embedding vectors found.")
            return

        vectors = np.asarray(vectors_list, dtype=np.float32)
        faiss.normalize_L2(vectors)

        index = faiss.IndexFlatIP(vectors.shape[1])
        index.add(vectors)

        index_file = settings.index_root / "observations.faiss"
        ids_file = settings.index_root / "observation_ids.txt"

        faiss.write_index(index, str(index_file))
        ids_file.write_text("\n".join(valid_obs_ids), encoding="utf-8")

        print(f"Successfully wrote {index.ntotal} vectors (dim={expected_dim}) to {index_file}")


if __name__ == "__main__":
    build_faiss_index()
