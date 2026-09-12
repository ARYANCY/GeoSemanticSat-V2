# UPAGRAHA / GeoSemanticSat — Comprehensive Engineering Documentation

Welcome to the formal engineering and operational documentation for **UPAGRAHA (GeoSemanticSat-V2)**.

---

## 📑 Complete Document Directory

| Doc Number | Title | Purpose & Content |
|---|---|---|
| **00** | [00_index_and_system_overview.md](00_index_and_system_overview.md) | Master overview, system pillars, performance SLA metrics, and architecture summary. |
| **01** | [01_system_architecture.md](01_system_architecture.md) | Two-pillar architecture (.NET 10 desktop + FastAPI backend), loopback IPC boundaries. |
| **02** | [02_algorithms_and_mathematics.md](02_algorithms_and_mathematics.md) | CVA, Sequential CUSUM, Tukey biweight PIF, 9-point parabolic jitter, DBSCAN, 128-d layout. |
| **03** | [03_remote_sensing_and_sensors.md](03_remote_sensing_and_sensors.md) | Sentinel-2 MSI bands B01-B12, Sentinel-1 SAR, optical quality masking, solar ray casting. |
| **04** | [04_foundation_models_and_peft.md](04_foundation_models_and_peft.md) | TerraMind, Prithvi, SatMAE++, GFM Composition; PEFT LoRA, loss functions, benchmarks. |
| **05** | [05_qwen_agent_and_geoint_synthesis.md](05_qwen_agent_and_geoint_synthesis.md) | Frozen local Qwen3-8B orchestrator, tool schemas, SITREP generation, false-alarm reasoning. |
| **06** | [06_desktop_application_guide.md](06_desktop_application_guide.md) | Avalonia UI studio, 5-stage stepper, 3-panel synchronized inspector, keyboard shortcuts. |
| **07** | [07_api_reference.md](07_api_reference.md) | Exhaustive REST API specification for all 47 sovereign backend endpoints. |
| **08** | [08_data_flows_and_contracts.md](08_data_flows_and_contracts.md) | 18-stage data flow trace, `TilePatch`, `ChangeRecord`, `GSSV` v2 binary format, W3C PROV-O. |
| **09** | [09_deployment_security_and_offline.md](09_deployment_security_and_offline.md) | Sovereign air-gap deployment, zero-network enforcement, hardware sizing, containerization. |
| **10** | [10_developer_and_testing_guide.md](10_developer_and_testing_guide.md) | Build instructions, xUnit suite (82 tests), Pytest suite (43 tests), verification workflows. |

---

## 🔬 Specialized Fine-Tuning Specifications (Historical & Reference)

- [dataset_and_training_specification.md](dataset_and_training_specification.md) — Sentinel-2 / Sentinel-1 dataset schema and tensor preparation.
- [fine_tuning_implementation_plan.md](fine_tuning_implementation_plan.md) — Fine-tuning execution roadmap for non-Qwen EO models.
- [model_fine_tuning_matrix.md](model_fine_tuning_matrix.md) — Comprehensive architecture and training parameter matrix.
- [fine_tuning_risk_and_rollback_plan.md](fine_tuning_risk_and_rollback_plan.md) — Failure mode mitigation and rollback strategies.
- [FINE_TUNING_WALKTHROUGH.md](FINE_TUNING_WALKTHROUGH.md) — End-to-end execution walkthrough of the fine-tuning process.
- [web_research_sources.md](web_research_sources.md) — Official academic papers, TerraTorch/TorchGeo documentation citations.
