# ============================================================================
#  GPUse IMAGE RUNNER  (Python)
#  ----------------------------------------------------------------------------
#  Called by agent.js like this:
#      python3 image_ai.py "a lighthouse at sunset" result.png
#
#  It uses Hugging Face "diffusers" + Stable Diffusion to REALLY generate
#  the picture on this computer's GPU.
#
#  First run is slow: it downloads the model (about 2-4 GB) once.
#  After that the model is cached on disk and runs are fast.
#
#  Needs (on the HOST computer only):
#      pip install torch diffusers transformers accelerate
#      (install the CUDA build of torch if you have an NVIDIA GPU)
# ============================================================================

import sys


def main():
    # Step 1: read the two words agent.js gave us: prompt text + output file
    if len(sys.argv) != 3:
        print("Usage: python3 image_ai.py '<prompt>' <output.png>", file=sys.stderr)
        sys.exit(1)

    prompt = sys.argv[1]
    output_file = sys.argv[2]

    # Step 2: load the AI libraries (here so a missing install fails clearly)
    try:
        import torch
        from diffusers import StableDiffusionPipeline
    except ImportError as error:
        print("Missing AI libraries. On the host run:", file=sys.stderr)
        print("  pip install torch diffusers transformers accelerate", file=sys.stderr)
        print("Original error:", error, file=sys.stderr)
        sys.exit(1)

    # Step 3: pick GPU if it is new enough, otherwise CPU (CPU works, slower)
    # Modern torch dropped very old GPUs (compute capability below 6.0,
    # e.g. Quadro M3000M). Asking CUDA to run on one just crashes, so we
    # check the capability number first and honestly fall back to CPU.
    device = "cpu"
    if torch.cuda.is_available():
        major, _minor = torch.cuda.get_device_capability(0)
        if major >= 6:
            device = "cuda"
        else:
            print(f"GPU is too old for modern torch (capability {major}.x), using CPU")
    print(f"Using device: {device}")

    # Step 4: load the model. small + fast model, good for demos.
    # "CompVis/stable-diffusion-v1-4" also works; turbo models need other pipes.
    model_name = "runwayml/stable-diffusion-v1-5"

    pipe = StableDiffusionPipeline.from_pretrained(
        model_name,
        torch_dtype=torch.float16 if device == "cuda" else torch.float32,
    )
    pipe = pipe.to(device)

    # Step 5: generate ONE image from the prompt text
    # 20 steps + 512x512 is the CPU-friendly setting: a few minutes here,
    # a few seconds on a modern GPU. Same code path either way.
    image = pipe(prompt, num_inference_steps=20, width=512, height=512).images[0]

    # Step 6: save it exactly where agent.js told us to
    image.save(output_file)
    print(f"Saved: {output_file}")


if __name__ == "__main__":
    main()
