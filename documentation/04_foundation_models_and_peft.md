# Models and training-related assets

`app/services/embeddings/service.py` provides a deterministic local 128-dimensional baseline embedder and adapters named for TerraMind, SatMAE++, GFM Composition, and Prithvi. Model paths, checkpoint paths, offline flags, and selected model are configuration values in `app/core/config.py`.

The repository contains `stage_foundation_models.py`, `download_models.py`, `export_models_to_onnx.py`, and four `train_*` scripts. Their presence documents a possible local workflow only. This documentation does not claim that referenced weights, LoRA checkpoints, datasets, or training outputs are installed or usable.

The backend sets `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1` at configuration import. Model operations therefore depend on local artifacts and configuration. The desktop Engine references `Microsoft.ML.OnnxRuntime`; `OnnxModelRunner` is its ONNX integration point.