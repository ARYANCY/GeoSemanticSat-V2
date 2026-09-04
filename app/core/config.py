"""Application configuration settings."""
from pathlib import Path
from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

PROJECT_ROOT = Path(__file__).resolve().parents[2]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=str(PROJECT_ROOT / ".env"),
        env_file_encoding="utf-8",
        extra="ignore"
    )

    offline_mode: bool = True
    database_url: str = Field(default=f"sqlite:///{PROJECT_ROOT}/data/satintel.db")
    data_root: Path = Field(default=PROJECT_ROOT / "data")
    model_root: Path = Field(default=PROJECT_ROOT / "models")
    index_root: Path = Field(default=PROJECT_ROOT / "indexes")
    remoteclip_weights_path: Path = Field(default=PROJECT_ROOT / "models" / "remoteclip" / "weights.pt")
    eo_model_name: str = "baseline"
    eo_model_weights_path: str = ""
    log_level: str = "INFO"
    max_ingest_raster_pixels: int = 100_000_000

    def ensure_directories(self) -> None:
        """Ensure runtime directories exist."""
        for p in (self.data_root, self.model_root, self.index_root):
            p.mkdir(parents=True, exist_ok=True)


settings = Settings()
settings.ensure_directories()
