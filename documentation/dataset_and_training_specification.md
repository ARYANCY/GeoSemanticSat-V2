# Dataset and training source inventory

No dataset manifest, dataset contents, trained checkpoint, or measured result is asserted by this document. This repository contains configuration paths under `data/`, `models/`, and `checkpoints/`, plus local model/training scripts.

The Python training scripts are `train_terramind_retrieval.py`, `train_prithvi_temporal.py`, `train_satmae_multispectral.py`, and `train_gfm_composition.py`. Before running any one, inspect its required local paths and dependencies. The backend's production embedding service is selected by configuration and may fall back according to its implementation.

Sample rasters created by `scripts/create_sample_data.py` are small synthetic fixtures. They are not a training dataset.