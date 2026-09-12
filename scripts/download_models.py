"""Download and stage Pretrained Geospatial Foundation Models and Qwen3-8B.

Supported models:
1. Qwen/Qwen3-8B (Reasoning & Conversational GEOINT Assistant)
2. ibm-esa-geospatial/TerraMind-1.0-base (Any-to-Any Multimodal)
3. BiliSakura/SATMAE-PP-transformers (Grouped Multi-Spectral MAE)
4. 05kashyap/GFM_Composition_Pretraining (SAR + Optical Composition)
5. ibm-nasa-geospatial/Prithvi-EO-2.0-300M (Spatio-Temporal Sequence ViT)

Supports:
- Hugging Face CLI ('hf download')
- Git LFS / Git Xet clone (full or pointer clone with GIT_LFS_SKIP_SMUDGE=1)
- Model inspection and local status reporting
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from app.core.config import settings

MODEL_SPECS = {
    "qwen3_8b": {
        "name": "Qwen3-8B",
        "repo_id": "Qwen/Qwen3-8B",
        "url": "https://huggingface.co/Qwen/Qwen3-8B",
        "target_dir": settings.model_root / "qwen3_8b",
        "role": "Conversational GEOINT intelligence agent, analytical report generation, and natural language query interpretation.",
        "type": "llm",
    },
    "terramind": {
        "name": "TerraMind-1.0-base",
        "repo_id": "ibm-esa-geospatial/TerraMind-1.0-base",
        "url": "https://huggingface.co/ibm-esa-geospatial/TerraMind-1.0-base",
        "target_dir": settings.model_root / "terramind",
        "weights_file": settings.terramind_model_path,
        "role": "Any-to-any multimodal cross-modal embedding, text-to-satellite query retrieval.",
        "type": "vision_multimodal",
    },
    "satmae_pp": {
        "name": "SATMAE-PP-transformers",
        "repo_id": "BiliSakura/SATMAE-PP-transformers",
        "url": "https://huggingface.co/BiliSakura/SATMAE-PP-transformers",
        "target_dir": settings.model_root / "satmae_pp",
        "weights_file": settings.satmae_pp_model_path,
        "role": "Grouped multi-spectral vision transformer patch encoder (RGB, RedEdge, NIR, SWIR).",
        "type": "vision_multispectral",
    },
    "gfm_composition": {
        "name": "GFM_Composition_Pretraining",
        "repo_id": "05kashyap/GFM_Composition_Pretraining",
        "url": "https://github.com/05kashyap/GFM_Composition_Pretraining.git",
        "target_dir": settings.model_root / "gfm_composition",
        "weights_file": settings.gfm_model_path,
        "role": "Compositional Sentinel-1 SAR and Sentinel-2 optical multi-sensor feature fusion.",
        "type": "vision_sar_optical",
    },
    "prithvi_300m": {
        "name": "Prithvi-EO-2.0-300M",
        "repo_id": "ibm-nasa-geospatial/Prithvi-EO-2.0-300M",
        "url": "https://huggingface.co/ibm-nasa-geospatial/Prithvi-EO-2.0-300M",
        "target_dir": settings.model_root / "prithvi",
        "weights_file": settings.prithvi_300m_model_path,
        "role": "3D Spatio-temporal time series transformer for change trajectory and onset detection.",
        "type": "vision_temporal",
    },
}


def check_tool_available(tool_name: str) -> bool:
    """Check if a CLI tool is available on PATH."""
    return shutil.which(tool_name) is not None


def download_with_hf_cli(repo_id: str, target_dir: Path) -> bool:
    """Download model repository using huggingface_hub CLI."""
    if not check_tool_available("hf"):
        print("  [Notice] 'hf' CLI is not installed or not in PATH.")
        return False
    print(f"  -> Downloading {repo_id} via hf download to {target_dir}...")
    target_dir.mkdir(parents=True, exist_ok=True)
    cmd = ["hf", "download", repo_id, "--local-dir", str(target_dir)]
    try:
        subprocess.run(cmd, check=True)
        return True
    except Exception as e:
        print(f"  [Error] hf download failed: {e}")
        return False


def clone_with_git(url: str, target_dir: Path, skip_smudge: bool = False) -> bool:
    """Clone repository using git, optionally skipping LFS smudge."""
    if not check_tool_available("git"):
        print("  [Error] 'git' is not installed or not in PATH.")
        return False
    if target_dir.exists() and any(target_dir.iterdir()):
        print(f"  [Notice] Target directory already exists and is not empty: {target_dir}")
        return True

    env = os.environ.copy()
    if skip_smudge:
        env["GIT_LFS_SKIP_SMUDGE"] = "1"
        print(f"  -> Pointer cloning (without large binary files) {url}...")
    else:
        print(f"  -> Full git cloning {url}...")

    cmd = ["git", "clone", url, str(target_dir)]
    try:
        subprocess.run(cmd, check=True, env=env)
        return True
    except Exception as e:
        print(f"  [Error] git clone failed: {e}")
        return False


def report_status() -> None:
    """Print status of all models and write manifest."""
    settings.ensure_directories()
    manifest_path = settings.model_root / "foundation_models_manifest.json"
    manifest: dict[str, dict] = {}

    print("=" * 80)
    print("UPAGRAHA-V2 / GEOSEMANTICSAT - AI & FOUNDATION MODEL INVENTORY")
    print("=" * 80)

    for key, spec in MODEL_SPECS.items():
        target_dir: Path = spec["target_dir"]
        is_dir_present = target_dir.is_dir() and any(target_dir.iterdir())

        total_size_mb = 0.0
        if is_dir_present:
            total_bytes = sum(f.stat().st_size for f in target_dir.rglob("*") if f.is_file())
            total_size_mb = round(total_bytes / (1024 * 1024), 2)

        weights_file = spec.get("weights_file")
        has_weights = (weights_file.is_file() if weights_file else False) or (is_dir_present and total_size_mb > 1.0)

        status_str = f"STAGED ({total_size_mb} MB)" if has_weights else "NOT STAGED (Fallback Adapter Ready)"

        print(f"\n[{spec['name']}] ({spec['type']})")
        print(f"  - Repository:   {spec['repo_id']}")
        print(f"  - Target Path:  {target_dir}")
        print(f"  - Primary Role: {spec['role']}")
        print(f"  - Status:       {status_str}")

        manifest[key] = {
            "name": spec["name"],
            "repo_id": spec["repo_id"],
            "url": spec["url"],
            "type": spec["type"],
            "role": spec["role"],
            "target_dir": str(target_dir),
            "staged": has_weights,
            "size_mb": total_size_mb,
            "fallback_available": True,
        }

    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"\nManifest successfully written to: {manifest_path}")


def main():
    parser = argparse.ArgumentParser(description="Download and stage Geospatial Foundation Models & Qwen3-8B.")
    parser.add_argument("--model", choices=list(MODEL_SPECS.keys()) + ["all"], default="all", help="Model to download")
    parser.add_argument("--method", choices=["hf", "git", "git-pointers", "status-only"], default="status-only", help="Download method")
    args = parser.parse_args()

    settings.ensure_directories()

    if args.method == "status-only":
        report_status()
        return

    models_to_download = list(MODEL_SPECS.keys()) if args.model == "all" else [args.model]

    for m_key in models_to_download:
        spec = MODEL_SPECS[m_key]
        print(f"\nProcessing {spec['name']}...")
        if args.method == "hf":
            success = download_with_hf_cli(spec["repo_id"], spec["target_dir"])
            if not success and "github.com" in spec["url"]:
                clone_with_git(spec["url"], spec["target_dir"], skip_smudge=False)
        elif args.method == "git":
            clone_with_git(spec["url"], spec["target_dir"], skip_smudge=False)
        elif args.method == "git-pointers":
            clone_with_git(spec["url"], spec["target_dir"], skip_smudge=True)

    report_status()


if __name__ == "__main__":
    main()
