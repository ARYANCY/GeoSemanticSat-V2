# Pretrained Geospatial Model Fine-Tuning Decision Matrix

**Project:** UPAGRAHA / GeoSemanticSat Sovereign Geospatial Intelligence  
**Hardware Baseline:** NVIDIA GeForce RTX 5070 Laptop GPU (8,151 MiB VRAM), 31.43 GB RAM, Windows 11  
**Scope:** Non-Qwen Earth Observation Foundation Models  

---

## 1. Comprehensive 27-Column Model Decision Matrix

| # | Column Name | Model 1: TerraMind-1.0-base | Model 2: Prithvi-EO-2.0-300M (Local) / 600M-TL | Model 3: SatMAE++ Transformers | Model 4: GFM Composition Pretraining |
|---|---|---|---|---|---|
| 1 | **Model** | **TerraMind-1.0-base** | **Prithvi-EO-2.0-300M** *(Upstream: 600M-TL)* | **SatMAE++ (FMoW-Sentinel / RGB)** | **GFM Composition Pretraining** |
| 2 | **Present Locally** | **YES** | **YES (300M checkpoint)**; *(600M-TL: NOT_PRESENT)* | **YES (both Sentinel-10 and RGB)** | **NO (codebase present, weights pending)** |
| 3 | **Exact Checkpoint** | `models/terramind/TerraMind_v1_base.pt` (1.52 GB) | `models/prithvi/Prithvi_EO_V2_300M.pt` (1.33 GB) | `models/satmae_pp/satmae-pp-vit-large-patch8-fmow-sentinel-pretrain/model.safetensors` (1.21 GB) | `models/gfm_composition/gfm_composition.pt` (Target: 405 MB) |
| 4 | **Architecture** | Dual-Scale Multimodal Transformer (FSQ-VAE + WordPiece) | 3D Spatio-Temporal ViT (3D Conv Patches + Time/Loc Encodings) | Grouped-Channel Multi-Spectral ViT-Large | DynamicVis (Mamba SSM) + Prithvi2 MAE Adapter |
| 5 | **Current UPAGRAHA Role** | Any-to-any cross-modal embedding; natural language text-to-satellite search | 3D multi-temporal sequence modeling, temporal CVA trajectory, CUSUM onset | Deep optical multispectral patch representation (RGB, RedEdge, NIR, SWIR) | Multi-sensor Sentinel-1 SAR + Sentinel-2 Optical fusion for cloud resilience |
| 6 | **Primary Fine-Tuning Task** | Cross-Modal Semantic Satellite & Site Retrieval (Text-to-Image & Image-to-Image) | Multi-Temporal Infrastructure Change & Rapid Activity Detection | Fine-Grained Facility & Land-Use Semantic Classification / Retrieval | Compositional SAR+Optical All-Weather Feature Alignment |
| 7 | **Training Objective** | Symmetric InfoNCE Contrastive Loss (Text-to-Raster Alignment) | Supervised Bi-Temporal Contrastive Loss with Margin + Change BCE | Supervised Contrastive Loss + Multiclass Cross-Entropy on Feature Patches | `CompositionAwareLoss` (MSE + VICReg Variance/Covariance + QSACL Slots) |
| 8 | **Recommended Method** | **LoRA (r=16, alpha=16) + 128d Contrastive Projection Head** | **IBM `peft-geofm` LoRA on QKV/MLP + Temporal Change Head** | **Hugging Face PEFT LoRA on `self.blocks.attn.qkv` + 128d Head** | **Frozen Backbone + Trainable QuerySlotDecoder & Composition Head** |
| 9 | **Full FT Feasible** | **NO** (Requires >28 GB VRAM; OOM on 8 GB GPU) | **NO** (Requires >26 GB VRAM; OOM on 8 GB GPU) | **NO** (Requires >26 GB VRAM; OOM on 8 GB GPU) | **NO** (Requires >18 GB VRAM; OOM on 8 GB GPU) |
| 10 | **LoRA Feasible** | **YES** (Target: multi-modal cross-attention projections) | **YES** (Target: `qkv.q_linear`, `qkv.v_linear`, `mlp.fc1`, `mlp.fc2`) | **YES** (Target: `attn.qkv`, `attn.proj`) | **YES** (Optional for DynamicVis Mamba projections) |
| 11 | **PEFT Feasible** | **YES** (TerraTorch PEFT / Hugging Face PEFT) | **YES** (Official IBM `peft-geofm` / TerraTorch) | **YES** (Native Hugging Face `peft` integration) | **YES** (Adapter-based tuning supported) |
| 12 | **QLoRA Feasible** | **NO** (BitsAndBytes 4-bit CUDA kernels unstable on Windows/custom EO layers) | **NO** (BitsAndBytes 4-bit not supported in official `peft-geofm` 3D Conv) | **NO** (Wavelength grouping requires standard unquantized floats) | **NO** (Mamba SSM custom CUDA ops incompatible with NF4) |
| 13 | **Recommended Dataset** | UPAGRAHA Semantic Query-AOI Triplet Archive + Curated fMoW/Geo-Bench | Pre-registered Bi-temporal Sentinel-2 pairs (AOI change events + HLS Burn/Flood) | fMoW-Sentinel (10-band multispectral) + Local Monitored Facility Tiles | Paired Sentinel-1 SAR (GRD) & Sentinel-2 MSI Cloud/Clear Scenes |
| 14 | **Input Format** | Multimodal Dict: `{'S2L2A': (B, 12, 224, 224), 'text': str}` | Spatio-temporal Tensor: `(B, 6, T, 224, 224)` or `(B, T, 6, 224, 224)` | Multispectral Raster Tensor: `(B, 10, 96, 96)` or `(B, 10, 224, 224)` | Dual Sensor Pair: `optical: (B, 6, 224, 224)`, `sar: (B, 2, 224, 224)` |
| 15 | **Required Bands** | 12 Bands (B01-B12) for S2L2A; 2 Bands (VV, VH) for S1GRD | 6 Core Bands: B02, B03, B04, B8A, B11, B12 | 10 Bands: B02, B03, B04, B05, B06, B07, B08, B8A, B11, B12 | S2: Blue, Green, Red, NIR, SWIR1, SWIR2; S1: VV, VH |
| 16 | **Temporal Input** | Single epoch (or stacked dual epoch) | Multi-temporal sequence: $T \in [2, 4]$ frames | Single epoch static patch | Co-registered contemporaneous optical & SAR acquisitions |
| 17 | **Resolution** | 10m / 20m Ground Sample Distance (GSD) | 30m HLS native / 10m Sentinel-2 resampled | 10m - 20m Sentinel-2 GSD | 10m (Sentinel-1 GRD & Sentinel-2 L2A) |
| 18 | **Normalization** | TerraMesh pretraining statistics per modality | HLS Means: `[1087, 1342, 1433, 2734, 1958, 1363]`; Stds: `[2248, 2179, 2178, 1850, 1242, 1049]` | FMoW Sentinel Means `[1184, 1120, ...]` and Stds `[650, 712, ...]` | Min-max decibel scaling for SAR `[-25dB, 0dB]`; reflectance `[0, 10000]` for S2 |
| 19 | **Recommended LR** | $1 \times 10^{-4}$ (LoRA), $5 \times 10^{-4}$ (Projection Head) | $2 \times 10^{-4}$ (LoRA), $1 \times 10^{-3}$ (Change Head) | $1.5 \times 10^{-4}$ (LoRA), $5 \times 10^{-4}$ (Classifier Head) | $2 \times 10^{-4}$ (QuerySlotDecoder), Cosine Annealing |
| 20 | **Batch Size** | 4 (Effective BS = 16 via Gradient Accumulation = 4) | 2 (Effective BS = 16 via Gradient Accumulation = 8) | 8 (Effective BS = 16 via Gradient Accumulation = 2) | 4 (Effective BS = 16 via Gradient Accumulation = 4) |
| 21 | **Epochs** | 25 - 35 epochs (Early stopping patience = 5) | 30 - 50 epochs (Early stopping patience = 6) | 20 - 30 epochs (Early stopping patience = 5) | 30 - 40 epochs (Early stopping patience = 6) |
| 22 | **GPU VRAM** | **~5.4 GB** (Fits safely within 8,151 MiB limit) | **~5.8 GB** (Fits safely within 8,151 MiB limit) | **~5.2 GB** (Fits safely within 8,151 MiB limit) | **~4.5 GB** (Fits safely within 8,151 MiB limit) |
| 23 | **Expected Training Time** | ~1.5 - 2.5 hours on 1,000 paired query-tile samples | ~2.0 - 3.5 hours on 1,500 temporal tile sequences | ~1.0 - 1.8 hours on 2,000 multispectral tiles | ~2.0 - 3.0 hours on 1,200 SAR-optical pairs |
| 24 | **Evaluation Metrics** | Recall@1, Recall@5, Mean Average Precision (mAP), Cosine Alignment | Precision, Recall, F1-Score, Change mIoU, False Alarm Rate | Top-1 Accuracy, Top-5 Accuracy, F1-Macro, Recall@5 | CBIR Recall@1, Recall@5, Cloud-Penetration Robustness Ratio |
| 25 | **Output Checkpoint** | `checkpoints/terramind_retrieval_lora_best.pt` | `checkpoints/prithvi_temporal_lora_best.pt` | `checkpoints/satmae_multispectral_lora_best/` | `checkpoints/gfm_composition_slots_best.pt` |
| 26 | **Integration Point** | `app/services/embeddings/models/terramind.py` | `app/services/embeddings/models/prithvi_temporal.py` | `app/services/embeddings/models/satmae_pp.py` | `app/services/embeddings/models/gfm_composition.py` |
| 27 | **Risks & Gotchas** | Catastrophic forgetting of cross-modal alignment if text head diverges; stale vector index | Frame order inversions; band mismatch (B8 vs B8A); OOM if sequence length > 4 | Mismatch in channel grouping order (G1-G4); dropping bands 0, 9, 10 | Lack of staged base checkpoint requires fallback to DynamicVis synthetic training |

---

## 2. Technical Justification of Selected Strategies

### 2.1 TerraMind-1.0-base
- **Why LoRA + Contrastive Head?** TerraMind's 300M backbone contains pre-trained foundational knowledge of Earth physics across modalities. Full fine-tuning risks catastrophic forgetting and requires 28 GB+ VRAM. A low-rank adaptation on cross-attention plus a 128-dimensional L2-normalized projection head preserves pretraining while tuning the latent space specifically for UPAGRAHA's text query syntax (e.g. *"new structures near river"*, *"runway expansion"*).

### 2.2 Prithvi-EO-2.0-300M
- **Why IBM `peft-geofm` LoRA?** Prithvi's 3D convolutional patch embedding is mathematically designed to process spatio-temporal cubes. IBM's official research (Marti Escofet et al., 2025) demonstrated that LoRA on `qkv` and `mlp` achieves equal or higher F1 than full fine-tuning with 35% less memory. This allows UPAGRAHA to perform true multi-temporal change trajectory modeling directly on consumer hardware.

### 2.3 SatMAE++
- **Why Grouped Multi-Spectral LoRA?** SatMAE++ explicitly treats different wavelength groups as distinct token sets. Injecting LoRA into `self.blocks` allows the attention mechanism to learn inter-band correlations (e.g., RedEdge to NIR vegetation stress signatures) while retaining the grouped autoencoder weights.

### 2.4 GFM Composition Pretraining
- **Why Train Slot Decoders & Composition Head?** GFM Composition formulates scene understanding as fractional land-cover histograms. Freezing the vision backbone and training the `QuerySlotDecoder` via `CompositionAwareLoss` avoids expensive backbone gradients while specializing the all-weather SAR-Optical fusion for military/civil infrastructure surveillance.
