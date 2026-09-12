# Fine-tuning risk and rollback note

Fine-tuning changes are outside the default backend startup path. The source has configurable paths for local model artifacts and checkpoints, and a `use_fine_tuned_weights` setting. It does not implement a universal transaction, version registry, or automatic rollback workflow for training artifacts.

Before changing local artifacts, retain a separately named copy of the existing artifact and record the configuration used. Verify the selected embedding/model route with the model-status endpoint or source-level tests appropriate to the change. This is operational caution, not evidence of a completed rollback system.