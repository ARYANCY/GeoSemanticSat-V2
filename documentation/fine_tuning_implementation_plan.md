# Fine-tuning implementation inventory

The checked-in implementation provides script entry points, model/checkpoint path settings, and adapter modules. It does not provide a repository-level record proving that a fine-tuning run completed.

Relevant source locations:

- `scripts/train_*.py` for training entry points.
- `app/services/embeddings/models/` for Python adapters.
- `app/core/config.py` for model, checkpoint, and fine-tuned-weight paths.
- `scripts/stage_foundation_models.py` and `scripts/export_models_to_onnx.py` for artifact staging/export work.

Treat any execution plan as an operator decision made after checking local data, artifacts, storage, and the script itself.