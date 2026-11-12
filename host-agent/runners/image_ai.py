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

    # Step 3: pick GPU if we have one, otherwise CPU (CPU works, just slow)
    device = "cuda" if torch.cuda.is_available() else "cpu"
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
    image = pipe(prompt, num_inference_steps=25).images[0]

    # Step 6: save it exactly where agent.js told us to
    image.save(output_file)
    print(f"Saved: {output_file}")


if __name__ == "__main__":
    main()
