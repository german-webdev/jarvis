# JARVIS third-party components

The desktop application and speech worker communicate through a separate JSON Lines process. Bundled third-party components retain their original licenses. Python package licenses and metadata are included in runtime/python/Lib/site-packages/*.dist-info. CPython's license is in runtime/python/LICENSE.txt. Source archives for Piper, eSpeak NG and PyAV are included in sources; their build scripts and upstream licenses are in those archives. The speech libraries are unmodified. The bundled CPython executable has an added Windows UTF-8 activeCodePage manifest entry so native speech libraries can load models from Unicode installation paths; CPython code is otherwise unmodified. The adjustment is reproducible in scripts/python_utf8_manifest.py.

- .NET and WPF: Microsoft / .NET Foundation, MIT; https://github.com/dotnet/runtime and https://github.com/dotnet/wpf.
- Python: Python Software Foundation; https://www.python.org/. The relocatable runtime comes from https://github.com/astral-sh/python-build-standalone.
- Piper 1.8.0: GPL-3.0, https://github.com/OHF-Voice/piper1-gpl/tree/v1.8.0. eSpeak NG source revision 724808c matches Piper's CMake build.
- PyAV / FFmpeg: see the bundled package licenses and source archives; https://github.com/PyAV-Org/PyAV and https://ffmpeg.org/.
- faster-whisper / CTranslate2: MIT, https://github.com/SYSTRAN/faster-whisper and https://github.com/OpenNMT/CTranslate2.
- ONNX Runtime: MIT, https://github.com/microsoft/onnxruntime.
- Vosk and the small Russian 0.22 model: Apache-2.0, https://alphacephei.com/vosk/models.
- Silero VAD: MIT, https://github.com/snakers4/silero-vad.
- Whisper small: MIT, https://github.com/openai/whisper. Converted weights: https://huggingface.co/Systran/faster-whisper-small.
- Piper Russian Dmitri / Denis voice model cards are in models/piper; their training datasets are marked CC0 by the publisher.
- Microsoft Visual C++ runtime DLLs: Microsoft redistributable components, licensed separately by Microsoft. https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/.
- Node.js v22.22.3 win-x64: https://nodejs.org/dist/v22.22.3/. License and third-party notices in `runtime/browser/LICENSE`.
- Microsoft Playwright MCP 0.0.84 and Playwright: Apache-2.0, https://github.com/microsoft/playwright-mcp. Exact versions, tarballs and hashes are in `runtime/browser/package-lock.json`. Package license and notice files are preserved in `runtime/browser/node_modules`. The tool uses installed Microsoft Edge.

Exact Python dependencies and model revisions are recorded alongside this document. The complete payload has a SHA-256 manifest. Personal settings, API keys, OAuth sessions and developer application/game catalogs are not part of the package.
