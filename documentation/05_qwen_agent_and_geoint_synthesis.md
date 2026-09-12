# AI-oriented backend features

The backend includes services for Qwen interaction, grounding, insight generation, before/after selection, and agent tool orchestration. The configured default model path is `models/qwen3_8b`; response schemas label the model `Qwen/Qwen3-8B`.

Routes include chat, insight generation, unified-search synthesis, comparison, analyst reports, and agent tasks. A response model name or local model path is not evidence that model files are available, loaded, or that output is independently verified.

The agent-task response records intent, plan, tool calls, results, optional map action, evidence strings, uncertainties, and suggested follow-ups. Inspect evidence/provenance routes where available.