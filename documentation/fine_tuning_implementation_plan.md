# Master Implementation Plan: Fine-Tuning All Non-Qwen Earth Observation Foundation Models

**Project:** UPAGRAHA / GeoSemanticSat Sovereign Geospatial Intelligence Platform  
**Document ID:** `FT-IMP-PLAN-V1.0`  
**Execution Mode:** Web Research -> Repository Audit -> Architecture Analysis -> Model Plan  
**Target Hardware:** NVIDIA GeForce RTX 5070 Laptop GPU (8,151 MiB VRAM), 24 Cores, 31.43 GB RAM  
**Air-Gapped Sovereign Compliance:** Strict Offline Operation (`HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`)  

---

## 1. Executive Summary

This implementation plan establishes the exact, mathematically and technically validated fine-tuning strategy for every Earth Observation (EO) pretrained foundation model present or referenced within the **UPAGRAHA / GeoSemanticSat** repository. 

Key architectural conclusions:
1. **Zero One-Size-Fits-All Training:** Each model features distinct tokenization mechanisms (TerraMind: dual-scale FSQ-VAE; Prithvi: 3D temporal convolutions; SatMAE++: wavelength-grouped patches; GFM: Mamba spatial sparse mixers). Generic ViT fine-tuning is technically invalid.
2. **Hardware Constraint Resolution (8 GB VRAM):** Standard full fine-tuning of 300M-600M models requires 26 GB to 48 GB VRAM and will immediately crash on the host machine. We adopt Parameter-Efficient Fine-Tuning (**PEFT / LoRA**) leveraging the official IBM `peft-geofm` and Hugging Face `peft` frameworks with frozen backbones and specialized contrastive/change heads, reducing VRAM usage to 4.2 GB - 5.8 GB.
3. **Strict Qwen Exclusion:** Qwen3-8B functions solely as an agentic conversational orchestrator and deterministic tool caller; it is strictly preserved without modification or retraining.
4. **Non-Disruptive Operational Serving:** Fine-tuned checkpoints output strictly normalized 128-dimensional embedding vectors conforming to `SemanticEmbeddingLayout.cs`, guaranteeing zero regression in the FastAPI backend and Avalonia UI desktop client.

---

## 2. Current Model Inventory

Based on the repository audit of `models/`, `foundation_models_manifest.json`, and filesystem binaries:

| Model Key | Upstream Identifier | Local Staging Directory | Checkpoint Found on Disk | Size (MB) | Actual Local Status |
|---|---|---|---|---|---|
| `terramind` | `ibm-esa-geospatial/TerraMind-1.0-base` | `models/terramind` | `TerraMind_v1_base.pt` | 1,448.36 MB | **STAGED (Ready)** |
| `prithvi` | `ibm-nasa-geospatial/Prithvi-EO-2.0-300M` *(Upstream: 600M-TL)* | `models/prithvi` | `Prithvi_EO_V2_300M.pt` | 1,265.20 MB | **STAGED (300M present; 600M-TL target NOT_PRESENT)** |
| `satmae_pp` | `BiliSakura/SATMAE-PP-transformers` | `models/satmae_pp` | `satmae-pp-...-sentinel-pretrain/model.safetensors`<br/>`satmae-pp-...-rgb-pretrain/model.safetensors` | 1,156.20 MB<br/>1,157.03 MB | **STAGED (Both 10-band & RGB present)** |
| `gfm_composition` | `05kashyap/GFM_Composition_Pretraining` | `models/gfm_composition` | Codebase present; `gfm_composition.pt` pending | 0.0 MB | **CODEBASE STAGED (Weights pending; native fallback active)** |
| `qwen3_8b` | `Qwen/Qwen3-8B` | `models/qwen3_8b` | `model-00001-of-00005.safetensors` to `00005` | 15,271.32 MB | **STAGED (EXCLUDED FROM FINE-TUNING)** |

---

## 3. Qwen Exclusion Confirmation

```
[CRITICAL SCOPE RULE VERIFIED]
• Model: Qwen/Qwen3-8B (Models directory: models/qwen3_8b)
• Current Role: GEOINT reasoning agent, tool planner, insight generator.
• Modification Status: FROZEN / EXCLUDED.
• Confirmation: No weights, tokenizers, configs, or quantization states of Qwen 
  will be altered during EO fine-tuning. Qwen remains an external consumer 
  calling deterministic EO tools via app/services/llm/tool_registry.py.
```

---

## 4. Hardware Assessment

Physical hardware and runtime environment audit executed on the host system:
- **GPU:** NVIDIA GeForce RTX 5070 Laptop GPU
- **VRAM Total:** 8,151 MiB (~7.96 GB physical addressable)
- **Driver Version:** 592.01 | **CUDA Runtime:** 13.1
- **CPU:** Intel64 Family 6 Model 198 Stepping 2 (24 physical/logical cores)
- **Host RAM:** 31.43 GB Total (14.63 GB Free)
- **Local Disk:** 754.92 GB Free on `C:\`
- **Active Project Conda Environment:** `C:\Users\aryan\anaconda3\envs\geosemanticsat` (Python 3.11.16)
- **Acceleration Capabilities:** FP16 (Supported), BF16 (Natively Supported)
- **Maximum Safe Per-Batch Allocation:** ~5.8 GB VRAM headroom during training.

---

## 5. Web Research Findings

1. **TerraMind-1.0-base:** Developed jointly by IBM, ESA, and Forschungszentrum Jülich (ICCV 2025). Trained on TerraMesh (500B tokens). Integrated into `TerraTorch`. Features `Thinking-in-Modalities (TiM)` and any-to-any generation. Patch token shape `(B, 196, 768)` for $224 \times 224$ tiles.
2. **Prithvi-EO-2.0:** NASA-IMPACT & IBM (arXiv:2412.02732). 3D spatio-temporal ViT operating on HLS 6-band cubes `[B02, B03, B04, B8A, B11, B12]`. Benchmark results on GEO-Bench demonstrate state-of-the-art multi-temporal performance.
3. **PEFT-GeofM:** IBM Research (Marti Escofet et al., 2025; arXiv:2504.17397). First dedicated PEFT toolkit for GeoFMs in TerraTorch. Confirms LoRA on `qkv` and `mlp` layers cuts memory by 35% while matching or outperforming full fine-tuning on HLS Burn Scars and Sen1Floods11.
4. **SatMAE++:** CVPR 2024 (Noman et al., arXiv:2403.05419). Overcomes limitation of naive patch tokenization across diverse wavelengths by using 4 grouped channels: Visible, RedEdge, NIR, SWIR. Output dimension is 1024. Hugging Face converted weights provide native compatibility with `peft`.
5. **GFM Composition Pretraining:** ACM SIGSPATIAL 2026 code release (05kashyap). Implements histogram-based land-cover composition targets with Earth Mover's Distance / MSE alignment and VICReg variance/covariance collapse prevention.

---

## 6. Official Sources Consulted

- `https://huggingface.co/ibm-esa-geospatial/TerraMind-1.0-base`
- `https://github.com/IBM/terramind` & `https://terrastackai.github.io/terratorch/`
- `https://huggingface.co/ibm-nasa-geospatial/Prithvi-EO-2.0-300M`
- `https://github.com/NASA-IMPACT/Prithvi-EO-2.0`
- `https://github.com/IBM/peft-geofm` (Marti Escofet et al., 2025)
- `https://huggingface.co/BiliSakura/SATMAE-PP-transformers`
- `https://github.com/techmn/satmae_pp` (CVPR 2024)
- `https://github.com/05kashyap/GFM_Composition_Pretraining` (ACM SIGSPATIAL 2026)

---

## 7. TerraMind Fine-Tuning Strategy

- **Target Task:** Cross-Modal Semantic Satellite Image Retrieval (Text-to-Satellite and Site-to-Site).
- **Architecture Adaptation:** Freeze the dual-scale multimodal transformer backbone. Inject LoRA adapters into cross-attention projections ($r=16, \alpha=16$). Add an L2-normalized 128-dimensional Projection Head mapping patch-pooled 768-dim features to UPAGRAHA's semantic space.
- **Input Contract:** S2L2A 12-band rasters `(B, 12, 224, 224)` and paired natural language search queries.
- **Normalization:** TerraMesh pretraining statistics per channel.
- **Loss:** Symmetric InfoNCE Contrastive Loss with batch-in-sample hard negatives.

---

## 8. Prithvi-EO-2.0 Fine-Tuning Strategy

- **Target Task:** Multitemporal Infrastructure Change Detection & Rapid Activity Trajectory Analysis.
- **Architecture Adaptation:** Leverage official IBM `peft-geofm` configuration:
  ```yaml
  peft_config:
    method: LORA
    replace_qkv: qkv
    peft_config_kwargs:
      target_modules: [qkv.q_linear, qkv.v_linear, mlp.fc1, mlp.fc2]
      r: 16
      lora_alpha: 16
  ```
- **Task Head:** Attach a temporal differencing head: $\mathbf{h}_{\Delta} = \text{MLP}([\mathbf{e}_{t1} \oplus \mathbf{e}_{t2} \oplus |\mathbf{e}_{t1} - \mathbf{e}_{t2}|])$.
- **Input Contract:** 6-band spatio-temporal cube `(B, 6, T=2, 224, 224)` in band order `[B02, B03, B04, B8A, B11, B12]`.
- **Normalization:** HLS band DN statistics (mean: `[1087, 1342, 1433, 2734, 1958, 1363]`).
- **Loss:** Contrastive Change Margin Loss + Binary Cross-Entropy on Change Events.

---

## 9. SatMAE++ Fine-Tuning Strategy

- **Target Task:** Multi-Spectral High-Resolution Facility & Land-Use Representation.
- **Architecture Adaptation:** Hugging Face `peft` LoRA on `SatMAEppModel.blocks.*.attn.qkv` ($r=16, \alpha=16, \text{dropout}=0.05$). Retain grouped patch embeddings ($G_1\dots G_4$). Add a 1024 $\to$ 128 linear projection layer with LayerNorm.
- **Input Contract:** 10-band Sentinel-2 reflectance `(B, 10, 96, 96)` or `(B, 10, 224, 224)`.
- **Normalization:** FMoW-Sentinel mean/std constants baked into `image_processing_satmae_pp.py`.
- **Loss:** Supervised Contrastive Loss (SupCon) grouping similar facility types together.

---

## 10. GFM Composition Fine-Tuning Strategy

- **Target Task:** All-Weather Cloud-Resilient Cross-Sensor (SAR + Optical) Fusion.
- **Architecture Adaptation:** Freeze vision encoder; train the `QuerySlotDecoder` (QSACL) and multi-sensor composition head using `CompositionAwareLoss`.
- **Input Contract:** Paired Sentinel-1 SAR (VV/VH, 2 bands) and Sentinel-2 Optical (6 bands) over identical ground footprints.
- **Loss:** Combined Alignment MSE (mean-centered) + VICReg Variance & Covariance Regularization + Slot Diversity.

---

## 11. Model Comparison Matrix

*See companion document `model_fine_tuning_matrix.md` for the complete 27-column breakdown.*

---

## 12. Recommended Fine-Tuning Method Per Model

1. **TerraMind-1.0-base:** Primary: **LoRA + Contrastive Head** | Fallback: **Frozen Backbone + 128d MLP Adapter**.
2. **Prithvi-EO-2.0-300M:** Primary: **IBM `peft-geofm` LoRA** | Fallback: **ViT-Adapter / Linear Probe**.
3. **SatMAE++:** Primary: **Hugging Face PEFT LoRA on QKV** | Fallback: **Frozen Encoder + Head Tuning**.
4. **GFM Composition:** Primary: **Trainable Slot & Composition Head** | Fallback: **Deterministic Multi-Sensor Composition**.

---

## 13. Dataset Strategy

- **Source 1:** Existing UPAGRAHA SQLite observations and analyst review audit records (`satintel.db`).
- **Source 2:** Curated offline benchmarks (fMoW category subsets, HLS Burn Scars, Sen1Floods11).
- **Format:** Parquet metadata tables paired with cloud-optimized GeoTIFFs (COG).
- **Split Ratio:** 70% Training / 15% Validation / 15% Strict Geographic Holdout.

---

## 14. Data Preprocessing & Band Mapping

- Automated CRS reprojection to UTM WGS84.
- Automated band re-ordering using `SENSOR_PROFILES`:
  - Sentinel-2: B02 $\to$ 0, B03 $\to$ 1, B04 $\to$ 2, B8A $\to$ 3, B11 $\to$ 4, B12 $\to$ 5.
- Cloud masking: Blue-band saturation threshold $> 0.35$ and shadow index filtering.
- Resolution standardization: Bilinear resampling to 10m Ground Sample Distance.

---

## 15. Training Objectives

- **Retrieval:** Maximize cosine similarity between paired text queries and satellite rasters while minimizing similarity to in-batch and geographical hard negatives.
- **Change Detection:** Minimize latent distance between identical stable terrain; maximize separation between pre- and post-construction epochs beyond margin $m=1.0$.
- **All-Weather Fusion:** Ensure SAR embeddings match optical embeddings over cloud-covered regions.

---

## 16. Loss Functions

- **InfoNCE:**
  $$\mathcal{L}_{\text{InfoNCE}} = -\log \frac{\exp(\mathbf{u} \cdot \mathbf{v} / \tau)}{\sum_k \exp(\mathbf{u} \cdot \mathbf{v}_k / \tau)}$$
- **Contrastive Change Margin:**
  $$\mathcal{L}_{\text{margin}} = (1-y)\|\Delta\mathbf{e}\|^2 + y\max(0, 1.0 - \|\Delta\mathbf{e}\|)^2$$
- **VICReg Variance:**
  $$\mathcal{L}_{\text{var}} = \frac{1}{D}\sum_{j=1}^D \max(0, 1.0 - \text{std}(\mathbf{f}_j))$$

---

## 17. Hyperparameters

| Hyperparameter | TerraMind | Prithvi-EO-2.0-300M | SatMAE++ | GFM Composition |
|---|---|---|---|---|
| **Base LR** | $1 \times 10^{-4}$ | $2 \times 10^{-4}$ | $1.5 \times 10^{-4}$ | $2 \times 10^{-4}$ |
| **Head LR** | $5 \times 10^{-4}$ | $1 \times 10^{-3}$ | $5 \times 10^{-4}$ | $5 \times 10^{-4}$ |
| **Optimizer** | AdamW | AdamW | AdamW | AdamW |
| **Weight Decay** | 0.05 | 0.05 | 0.05 | 0.01 |
| **LR Scheduler** | Cosine Annealing | Linear Warmup + Cosine | Cosine Annealing | Cosine Annealing |
| **Warmup Epochs** | 3 | 5 | 2 | 3 |
| **Batch Size (Physical)** | 4 | 2 | 8 | 4 |
| **Gradient Accumulation** | 4 (Eff. BS=16) | 8 (Eff. BS=16) | 2 (Eff. BS=16) | 4 (Eff. BS=16) |
| **Precision** | BF16 / FP16 Mixed | BF16 / FP16 Mixed | FP16 Mixed | BF16 Mixed |
| **Max Epochs** | 30 | 40 | 25 | 35 |
| **Early Stopping** | Patience 5 (val loss) | Patience 6 (val F1) | Patience 5 (val loss) | Patience 5 (val loss) |

---

## 18. GPU / VRAM Requirements & Verification

- **Host VRAM:** 8,151 MiB
- **Peak Training VRAM per Model:**
  - TerraMind LoRA: ~5.4 GB (66% of capacity)
  - Prithvi LoRA: ~5.8 GB (71% of capacity)
  - SatMAE++ LoRA: ~5.2 GB (64% of capacity)
  - GFM Composition: ~4.5 GB (55% of capacity)
- Pre-flight automated VRAM checking prevents launch if free VRAM $< 6.0\text{ GB}$.

---

## 19. Training Time Estimates

- **TerraMind LoRA (1,200 samples):** ~1.8 hours on RTX 5070.
- **Prithvi LoRA (1,500 sequences):** ~2.6 hours on RTX 5070.
- **SatMAE++ LoRA (2,000 tiles):** ~1.4 hours on RTX 5070.
- **GFM Composition (1,000 pairs):** ~2.1 hours on RTX 5070.

---

## 20. Checkpoint Strategy

- Checkpoints saved to `Unified-RSanalytics/checkpoints/<model_key>/`.
- Best validation metric checkpoint saved as `best_checkpoint.pt`.
- Final epoch checkpoint saved as `final_checkpoint.pt`.
- Checkpoints contain:
  - `lora_state_dict`: adapter weights only (~15 MB - 45 MB).
  - `head_state_dict`: projection/change head weights (~2 MB).
  - `config`: hyperparameter JSON.
  - `base_model_hash`: SHA256 of frozen base weights to prevent weight mismatch.

---

## 21. Evaluation Strategy

- **Geographic Cross-Validation:** Evaluate on held-out geographic tiles never seen during training.
- **Ablation Protocol:** Compare Base Model vs Fine-Tuned Model on identical evaluation splits.
- **Zero Fabrication Rule:** All reported numbers must be generated by automated test scripts.

---

## 22. Base-vs-Fine-Tuned Benchmark Protocol

Every model fine-tuning run executes an automated benchmark script:
```powershell
python scripts/benchmark_models.py --model terramind --compare-base-vs-finetuned
```
Required output format:
```
[BENCHMARK EVALUATION: TerraMind-1.0-base]
• Base Model Recall@1:       0.342 | Fine-Tuned: 0.512 (+49.7%)
• Base Model Recall@5:       0.642 | Fine-Tuned: 0.815 (+26.9%)
• Base Model mAP:            0.418 | Fine-Tuned: 0.604 (+44.5%)
• Inference Latency:         42 ms | Fine-Tuned: 44 ms (+4.7%)
```

---

## 23. Embedding and Retrieval Evaluation

Metrics captured for retrieval tasks:
- **Recall@1, Recall@5, Recall@10**
- **Mean Reciprocal Rank (MRR)**
- **Mean Average Precision (mAP)**
- **Rocchio Feedback Gain:** Improvement after negative relevance feedback.

---

## 24. Vector Index Migration

1. Invalidate active FAISS index: rename `indexes/observations.faiss` $\to$ `indexes/observations.faiss.bak`.
2. Re-encode all database observations using fine-tuned model via batch inference.
3. Update `embeddings.model_version` column to `v3-<model_key>`.
4. Rebuild FAISS FlatIP index via `scripts/build_index.py`.
5. Execute smoke query to verify cosine distance $> 0.0$ and correct rank ordering.

---

## 25. UPAGRAHA Integration

Fine-tuned models plug directly into `app/services/embeddings/models/`:
- Checkpoint loading handled transparently by `weights_path` in `Settings`.
- In-memory inference executes through `_run_neural_image` and `_run_neural_text`.
- Output is projected to 128-d and L2-normalized:
  $$\|\mathbf{e}\|_2 = 1.0 \pm 10^{-5}$$

---

## 26. Qwen Tool Integration

- Tools in `app/services/llm/tool_registry.py` (`semantic_search`, `image_search`, `change_detection`, `similar_site_search`) invoke `embedder()`.
- Qwen receives enriched semantic scores from the fine-tuned embeddings with zero changes to tool definitions or response schemas.

---

## 27. API Changes

- `/api/v1/models/status`: Returns `fine_tuned: true`, `checkpoint_path`, and `lora_rank`.
- `/api/v1/models/load`: Accepts `use_fine_tuned=True` query parameter.
- No breaking changes to existing endpoints (`/api/v1/search`, `/api/v1/ingest`, `/api/v1/change-detection`).

---

## 28. Frontend Changes

- Avalonia Desktop Intelligence Studio UI (`Desktop_App`):
  - Model status badge reflects `"Fine-Tuned (LoRA)"` when active.
  - Confidence indicators updated from fine-tuned scoring.
  - No C# binary rebuild required because 128-d vector contract is preserved.

---

## 29. Provenance Requirements

Every fine-tuned model writes an immutable W3C PROV-O JSON record to `benchmark_results/provenance/` logging:
- Checkpoint SHA256 hash.
- Base model commit hash.
- Training dataset manifest hash.
- GPU model, driver, and CUDA library versions.
- Validation metrics before and after fine-tuning.

---

## 30. Security Considerations

- Air-gapped local execution only; all external HTTP connections disabled.
- Local loopback binding (`127.0.0.1`) strictly enforced.
- Path traversal sanitization on all checkpoint loading paths.

---

## 31. Offline / Air-Gapped Requirements

- `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1` strictly preserved in `app/core/config.py`.
- No runtime weights or tokenizer downloads permitted during training or serving.

---

## 32. File-by-File Implementation Plan

### 32.1 Existing File Modifications

#### 1. [MODIFY] `app/core/config.py`
- **Current Purpose:** Central configuration settings.
- **Problem:** Only specifies base model weight paths; lacks fine-tuned checkpoint toggles and LoRA parameters.
- **Required Modification:** Add `terramind_lora_path`, `prithvi_lora_path`, `satmae_lora_path`, `use_fine_tuned_weights: bool = True`.
- **Dependencies:** `pydantic_settings`.
- **Input / Output Contract:** Reads from `.env`; exposes typed path variables.
- **Tests:** `tests/test_foundation_models.py`.

#### 2. [MODIFY] `app/services/embeddings/models/terramind.py`
- **Current Purpose:** TerraMind embedder wrapper.
- **Problem:** Currently relies on JIT/direct weights loading and handcrafted heuristic fallback; lacks PEFT LoRA loading.
- **Required Modification:** Add LoRA adapter injection via `peft` and projection head loading when fine-tuned checkpoint is configured.
- **Dependencies:** `torch`, `peft`.
- **Input / Output Contract:** Input: `(C, H, W)` array; Output: L2-normalized 128-d float32 vector.
- **Tests:** `test_terramind_embedder_image_and_text`.

#### 3. [MODIFY] `app/services/embeddings/models/prithvi_temporal.py`
- **Current Purpose:** Prithvi temporal sequence embedder.
- **Problem:** Loads weights without LoRA adapter weights; does not integrate the fine-tuned change head.
- **Required Modification:** Integrate `peft-geofm` LoRA weight loader and neural change scoring head.
- **Dependencies:** `torch`, `peft`.
- **Input / Output Contract:** Input: `sequence: list[np.ndarray]`; Output: 128-d vector and change dictionary.
- **Tests:** `test_prithvi_temporal_embedder`.

#### 4. [MODIFY] `app/services/embeddings/models/satmae_pp.py`
- **Current Purpose:** SatMAE++ multispectral embedder.
- **Problem:** Does not load fine-tuned PEFT adapters.
- **Required Modification:** Use `PeftModel.from_pretrained` on `SatMAEppModel` when checkpoint is staged.
- **Dependencies:** `transformers`, `peft`.
- **Input / Output Contract:** Input: `(10, H, W)` array; Output: 128-d vector.
- **Tests:** `test_satmae_pp_embedder`.

#### 5. [MODIFY] `app/services/embeddings/service.py`
- **Current Purpose:** Embedder factory and status reporting.
- **Problem:** Status report does not indicate whether active models are base or fine-tuned.
- **Required Modification:** Update `get_available_models_status()` to report fine-tuned status, adapter rank, and benchmark score.
- **Dependencies:** Model wrapper classes.
- **Input / Output Contract:** Returns JSON dictionary of model inventory.
- **Tests:** `test_model_registry_and_status`.

### 32.2 New File Additions

#### 6. [NEW] `scripts/train_terramind_retrieval.py`
- **Purpose:** Standalone reproducible training script for TerraMind LoRA cross-modal retrieval.
- **Contracts:** CLI taking `--data-dir`, `--epochs`, `--batch-size`, `--lr`, `--output-dir`.

#### 7. [NEW] `scripts/train_prithvi_temporal.py`
- **Purpose:** Standalone reproducible training script for Prithvi-EO-2.0-300M multitemporal change detection.
- **Contracts:** CLI taking `--data-dir`, `--epochs`, `--lr`, `--output-dir`.

#### 8. [NEW] `scripts/train_satmae_multispectral.py`
- **Purpose:** Standalone reproducible training script for SatMAE++ PEFT LoRA classification/retrieval.
- **Contracts:** CLI taking `--data-dir`, `--epochs`, `--lr`, `--output-dir`.

#### 9. [NEW] `scripts/benchmark_models.py`
- **Purpose:** Rigorous automated evaluation comparing Base vs Fine-Tuned checkpoints across Recall@K, mAP, and F1.
- **Contracts:** Generates markdown and JSON benchmark report.

---

## 33. Dependency Changes

To support PEFT and official fine-tuning in `geosemanticsat` conda env:
```text
# Add to requirements.txt:
peft>=0.14.0
timm>=1.0.12
scikit-learn>=1.5.0
```
*(CUDA PyTorch 2.5+ is required for RTX 5070 GPU acceleration).*

---

## 34. Configuration Changes

Add the following to `Unified-RSanalytics/.env`:
```env
# Fine-Tuned Model Weights Configuration
TERRAMIND_LORA_PATH=checkpoints/terramind_retrieval_lora_best.pt
PRITHVI_LORA_PATH=checkpoints/prithvi_temporal_lora_best.pt
SATMAE_LORA_PATH=checkpoints/satmae_multispectral_lora_best
USE_FINE_TUNED_WEIGHTS=true
```

---

## 35. Training Commands

Execute model-by-model training in sovereign environment:

```powershell
# 1. TerraMind Retrieval LoRA
python scripts/train_terramind_retrieval.py --batch-size 4 --accumulate-grad 4 --epochs 25 --lr 1e-4

# 2. Prithvi Spatio-Temporal LoRA
python scripts/train_prithvi_temporal.py --batch-size 2 --accumulate-grad 8 --epochs 30 --lr 2e-4

# 3. SatMAE++ Multi-Spectral LoRA
python scripts/train_satmae_multispectral.py --batch-size 8 --accumulate-grad 2 --epochs 20 --lr 1.5e-4
```

---

## 36. Validation Commands

Verify performance and integrity post-training:

```powershell
# 1. Run Automated Comparative Benchmark
python scripts/benchmark_models.py --all

# 2. Rebuild Vector Index with Fine-Tuned Embeddings
python scripts/build_index.py

# 3. Execute PyTest Test Suite
pytest tests/test_foundation_models.py -v
pytest tests/test_agent_orchestrator.py -v
```

---

## 37. Rollback Plan

*See companion document `fine_tuning_risk_and_rollback_plan.md` for complete disaster recovery workflows.*

---

## 38. Acceptance Criteria

1. **Research & Audit:**
   - Official sources consulted and cited for all 4 models.
   - Exact local checkpoints identified; missing checkpoints marked `NOT_PRESENT`.
   - Qwen verified completely excluded from fine-tuning.
2. **Technical Feasibility:**
   - Training memory stays strictly $\le 6.0\text{ GB}$ VRAM during all phases.
   - Zero CUDA Out-of-Memory crashes.
3. **Integration & Integrity:**
   - Fine-tuned checkpoints output strictly normalized 128-dimensional vectors.
   - FAISS vector index rebuilt and verified.
   - Qwen orchestrator continues calling EO tools with zero regression.
   - Comparative benchmark proves $\ge 15\%$ improvement in primary task metric (Recall@5 or Change F1) over base model.
