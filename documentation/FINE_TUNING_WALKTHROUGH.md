# Fine-tuning walkthrough boundary

This repository contains training and staging scripts but no source-controlled, universally valid walkthrough for producing model weights. Inputs, model artifacts, checkpoints, and available compute are local environment state.

For a source-based inspection, identify the intended script in `scripts/`, inspect its command-line behavior and path requirements, then verify its outputs are compatible with the adapter/configuration selected in `app/core/config.py`. Do not assume an artifact exists because a configuration path names it.