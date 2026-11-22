# Third-party notices

IdleTrim downloads or bundles the following. None of the model weights are stored in this repository.

| Component | Author | License | How it's used |
|---|---|---|---|
| [SigLIP 2 base patch16-224](https://huggingface.co/google/siglip2-base-patch16-224), ONNX export by [onnx-community](https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX) | Google | Apache 2.0 | Vision model downloaded on first use. `src/prompts.txt` holds text embeddings computed with its text model. |
| [DINOv2 small](https://huggingface.co/facebook/dinov2-small), ONNX export by [onnx-community](https://huggingface.co/onnx-community/dinov2-small-ONNX) | Meta | Apache 2.0 | Downloaded on first use. |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) with DirectML | Microsoft | MIT | Bundled DLLs that run the models. |
| [FFmpeg](https://ffmpeg.org/) builds from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | FFmpeg developers | GPL | Found on the PC or downloaded on first use; run as a separate program. |

The Apache 2.0 license text is at https://www.apache.org/licenses/LICENSE-2.0.
