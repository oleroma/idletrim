@echo off
rem Builds the portable app into build\app: idletrim.exe plus the ONNX Runtime and DirectML DLLs.
rem Needs the .NET 8 SDK to build and the .NET 8 Desktop Runtime to run.
setlocal
cd /d "%~dp0"
dotnet publish IdleTrim.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o build\app -nologo -v q
if errorlevel 1 exit /b 1
del /q build\app\*.pdb build\app\DirectML.Debug.dll build\app\onnxruntime.lib 2>nul
rem Reuse models already downloaded for development, so the app doesn't fetch them again.
if exist build\models\siglip2\vision_model_fp16.onnx if not exist build\app\models\siglip2\vision_model_fp16.onnx (
  mkdir build\app\models\siglip2 build\app\models\dinov2-small 2>nul
  copy /y build\models\siglip2\vision_model_fp16.onnx build\app\models\siglip2\ >nul
  copy /y build\models\dinov2-small\model.onnx build\app\models\dinov2-small\ >nul
)
echo Built build\app\idletrim.exe
