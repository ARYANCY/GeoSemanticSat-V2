import os
from pathlib import Path
from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

# Strict sovereign offline assurance: disable external Hugging Face and telemetry calls
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

PROJECT_ROOT = Path(__file__).resolve().parents[2]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=str(PROJECT_ROOT / ".env"),
        env_file_encoding="utf-8",
        extra="ignore"
    )

    offline_mode: bool = True
    hf_hub_offline: bool = True
    transformers_offline: bool = True
    database_url: str = Field(default=f"sqlite:///{PROJECT_ROOT}/data/satintel.db")
    data_root: Path = Field(default=PROJECT_ROOT / "data")
    model_root: Path = Field(default=PROJECT_ROOT / "models")
    index_root: Path = Field(default=PROJECT_ROOT / "indexes")
    remoteclip_weights_path: Path = Field(default=PROJECT_ROOT / "models" / "remoteclip" / "weights.pt")
    terramind_model_path: Path = Field(default=PROJECT_ROOT / "models" / "terramind" / "TerraMind_v1_base.pt")
    satmae_pp_model_path: Path = Field(default=PROJECT_ROOT / "models" / "satmae_pp" / "satmae-pp-vit-large-patch8-fmow-sentinel-pretrain" / "model.safetensors")
    gfm_model_path: Path = Field(default=PROJECT_ROOT / "models" / "gfm_composition" / "gfm_composition.pt")
    prithvi_model_path: Path = Field(default=PROJECT_ROOT / "models" / "prithvi" / "prithvi_eo_2_600m_tl.pt")
    prithvi_300m_model_path: Path = Field(default=PROJECT_ROOT / "models" / "prithvi" / "Prithvi_EO_V2_300M.pt")
    # Fine-Tuned Model Weights Configuration
    use_fine_tuned_weights: bool = Field(default=True)
    checkpoint_root: Path = Field(default=PROJECT_ROOT / "checkpoints")
    terramind_lora_path: Path = Field(default=PROJECT_ROOT / "checkpoints" / "terramind" / "terramind_retrieval_lora_best.pt")
    prithvi_lora_path: Path = Field(default=PROJECT_ROOT / "checkpoints" / "prithvi" / "prithvi_temporal_lora_best.pt")
    satmae_lora_path: Path = Field(default=PROJECT_ROOT / "checkpoints" / "satmae_pp" / "satmae_multispectral_lora_best.pt")
    gfm_slots_path: Path = Field(default=PROJECT_ROOT / "checkpoints" / "gfm_composition" / "gfm_composition_slots_best.pt")

    qwen_model_path: Path = Field(default=PROJECT_ROOT / "models" / "qwen3_8b")
    qwen_device: str = Field(default="auto")
    qwen_quantization: str = Field(default="none")
    eo_model_name: str = "baseline"
    eo_model_weights_path: str = ""
    log_level: str = "INFO"
    max_ingest_raster_pixels: int = 100_000_000
    host: str = Field(default="127.0.0.1")
    port: int = Field(default=8000)
    enforce_loopback_only: bool = Field(default=True)
    require_token_auth: bool = Field(default=False)
    api_auth_token: str = Field(default="")

    def ensure_directories(self) -> None:
        """Ensure runtime directories exist."""
        for p in (
            self.data_root,
            self.model_root,
            self.index_root,
            self.checkpoint_root,
            self.checkpoint_root / "terramind",
            self.checkpoint_root / "prithvi",
            self.checkpoint_root / "satmae_pp",
            self.checkpoint_root / "gfm_composition",
            self.model_root / "terramind",
            self.model_root / "satmae_pp",
            self.model_root / "gfm_composition",
            self.model_root / "prithvi",
            self.model_root / "qwen3_8b",
        ):
            p.mkdir(parents=True, exist_ok=True)


settings = Settings()
settings.ensure_directories()
