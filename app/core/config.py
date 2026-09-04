from pathlib import Path
from pydantic_settings import BaseSettings, SettingsConfigDict

class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")
    offline_mode: bool = True
    database_url: str = "sqlite:///./data/satintel.db"
    data_root: Path = Path("./data")
    model_root: Path = Path("./models")
    index_root: Path = Path("./indexes")
    remoteclip_weights_path: Path = Path("./models/remoteclip/weights.pt")
    eo_model_name: str = "baseline"
    eo_model_weights_path: str = ""
    log_level: str = "INFO"
    max_ingest_raster_pixels: int = 100_000_000

settings = Settings()
for _p in (settings.data_root, settings.model_root, settings.index_root): _p.mkdir(parents=True, exist_ok=True)
