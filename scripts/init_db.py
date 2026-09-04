import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from app.db.session import Base,engine
import app.models.entities
Base.metadata.create_all(engine)
print(f"Database initialized: {engine.url}")
