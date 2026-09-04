"""Initialize and create all database tables."""
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from app.db.session import Base, engine
import app.models.entities  # noqa: F401


def init_database() -> None:
    """Create all schema tables."""
    Base.metadata.create_all(bind=engine)
    print(f"Database tables initialized at: {engine.url}")


if __name__ == "__main__":
    init_database()
