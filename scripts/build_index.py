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


def add_vector_to_faiss(
    index_file: Path | str | None = None,
    ids_file: Path | str | None = None,
    vector: np.ndarray | list[float] | None = None,
    observation_id: str | None = None,
) -> bool:
    """Incrementally appends a single 128-d L2-normalized vector and observation ID to FAISS index."""
    if vector is None or observation_id is None:
        return False

    index_path = Path(index_file) if index_file else settings.index_root / "observations.faiss"
    ids_path = Path(ids_file) if ids_file else settings.index_root / "observation_ids.txt"
    index_path.parent.mkdir(parents=True, exist_ok=True)

    vec = np.asarray(vector, dtype=np.float32).reshape(1, -1)
    faiss.normalize_L2(vec)
    dim = vec.shape[1]

    if index_path.exists():
        index = faiss.read_index(str(index_path))
        if index.d != dim:
            logger.error(f"Dimension mismatch: index has {index.d}, new vector has {dim}")
            return False
    else:
        index = faiss.IndexFlatIP(dim)

    # Read existing IDs if present
    existing_ids = ids_path.read_text(encoding="utf-8").splitlines() if ids_path.exists() else []
    if observation_id in existing_ids:
        # Already indexed
        return True

    index.add(vec)
    existing_ids.append(observation_id)

    faiss.write_index(index, str(index_path))
    ids_path.write_text("\n".join(existing_ids), encoding="utf-8")
    logger.info(f"Incrementally indexed {observation_id}. Total vectors: {index.ntotal}")
    return True


if __name__ == "__main__":
    build_faiss_index()
