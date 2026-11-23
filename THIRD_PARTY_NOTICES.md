# Third-party notices

IdleTrim downloads or bundles the following. None of the model weights are stored in this repository. Models you add from Hugging Face come under their own licenses.

| Component | Author | License | How it's used |
|---|---|---|---|
| [SigLIP 2](https://huggingface.co/google/siglip2-base-patch16-224) (base, large, so400m, giant), ONNX exports by [onnx-community](https://huggingface.co/onnx-community) | Google | Apache 2.0 | Downloaded when you first Detect with one. `src/prompts.txt` holds the built-in prompts' embeddings from SigLIP 2 base. |
| [DINOv2](https://huggingface.co/facebook/dinov2-small) (small to giant), ONNX exports by [onnx-community](https://huggingface.co/onnx-community) | Meta | Apache 2.0 | Downloaded when you first Detect with one. |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) with DirectML | Microsoft | MIT | Bundled DLLs that run the models. |
| [NAudio](https://github.com/naudio/NAudio) | Mark Heath and contributors | MIT | Bundled DLLs that play the preview's sound. |
| [FFmpeg](https://ffmpeg.org/) builds from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | FFmpeg developers | GPL | Found on the PC or downloaded on first use; run as a separate program. |

The Apache 2.0 license text is at https://www.apache.org/licenses/LICENSE-2.0.
