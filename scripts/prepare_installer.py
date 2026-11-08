"""Allowlisted offline payload. Never copy the developer's data, accounts or catalogs."""
import argparse
import concurrent.futures
import hashlib
import importlib.metadata
import json
import os
import shutil
import ssl
import subprocess
import sys
import urllib.request
from pathlib import Path

import certifi
from python_utf8_manifest import enable_utf8

ROOT = Path(__file__).resolve().parents[1]


def download(url, path):
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        part = path.with_name(path.name + ".part")
        with urllib.request.urlopen(url, timeout=60, context=ssl.create_default_context(cafile=certifi.where())) as response, part.open("wb") as out:
            shutil.copyfileobj(response, out)
        part.replace(path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("payload", type=Path)
    args = parser.parse_args()
    payload = args.payload.resolve()
    if not payload.is_relative_to((ROOT / "artifacts").resolve()) or not (payload / "Jarvis.exe").is_file():
        raise ValueError("Publish the app into an artifacts payload first")
    if (payload / "data").exists() or (payload / "jarvis.install.json").exists():
        raise ValueError("Use a fresh payload; private data and reused packages are forbidden")
    runtime = payload / "runtime" / "python"
    base = Path(sys.base_prefix)
    # UV's relocatable CPython distribution, with its license. No venv, registry or user Python needed.
    shutil.copytree(base, runtime, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "Scripts", "site-packages"))
    enable_utf8(runtime / "python.exe")
    requirements = payload / "third-party" / "python-requirements.txt"
    requirements.parent.mkdir(parents=True, exist_ok=True)
    uv = shutil.which("uv")
    if not uv:
        raise RuntimeError("uv is required on the build machine")
    subprocess.run([uv, "export", "--frozen", "--no-dev", "--no-emit-project", "--output-file", str(requirements)],
                   cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    environment = os.environ.copy()
    environment["UV_CACHE_DIR"] = str(ROOT / ".tools" / "uv-cache")
    subprocess.run([uv, "pip", "install", "--python", str(runtime / "python.exe"), "--target",
                    str(runtime / "Lib" / "site-packages"), "--require-hashes", "-r", str(requirements)],
                   env=environment, check=True)
    (runtime / "python312._pth").write_text(
        ".\nDLLs\nLib\nLib/site-packages\n../../speech\nimport site\n", encoding="utf-8")
    shutil.copytree(ROOT / "speech", payload / "speech", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    manifest = json.loads((ROOT / "models.lock.json").read_text(encoding="utf-8"))
    for relative, expected in manifest["sha256"].items():
        if relative == "vosk.zip":
            continue
        source = ROOT / "models" / relative
        with source.open("rb") as f:
            actual = hashlib.file_digest(f, "sha256").hexdigest()
        if actual != expected:
            raise ValueError(f"Model integrity failed: {relative}")
        target = payload / "models" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    shutil.copy2(ROOT / "models.lock.json", payload / "third-party" / "models.lock.json")
    # Native ML wheels need the MSVC runtime. Use signed Microsoft redistributable DLLs app-locally.
    for name in ["msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "vcruntime140.dll", "vcruntime140_1.dll", "concrt140.dll"]:
        source = Path(os.environ["SystemRoot"]) / "System32" / name
        if not source.is_file():
            raise RuntimeError(f"Install the Microsoft Visual C++ x64 runtime on the build machine: {name}")
        shutil.copy2(source, runtime / name)
    sources = payload / "third-party" / "sources"
    for name in ["piper-tts", "av"]:
        version = importlib.metadata.version(name)
        with urllib.request.urlopen(f"https://pypi.org/pypi/{name}/{version}/json", timeout=30) as response:
            metadata = json.load(response)
        sdist = next(file for file in metadata["urls"] if file["packagetype"] == "sdist")
        target = sources / sdist["filename"]
        download(sdist["url"], target)
        with target.open("rb") as f:
            if hashlib.file_digest(f, "sha256").hexdigest() != sdist["digests"]["sha256"]:
                raise ValueError("Source archive hash mismatch")
    download("https://codeload.github.com/espeak-ng/espeak-ng/zip/724808c", sources / "espeak-ng-724808c.zip")
    ffmpeg_sources = json.loads((ROOT / "installer" / "ffmpeg-sources.lock.json").read_text(encoding="utf-8"))
    def fetch_source(item):
        target = sources / (item["name"] + "-" + item["source_url"].rsplit("/", 1)[-1])
        download(item["source_url"], target)
        with target.open("rb") as f:
            if hashlib.file_digest(f, "sha256").hexdigest() != item["sha256"]:
                raise ValueError("FFmpeg source hash mismatch: " + item["name"])
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(fetch_source, ffmpeg_sources))
    download("https://codeload.github.com/PyAV-Org/pyav-ffmpeg/zip/refs/tags/9.0.2-3", sources / "pyav-ffmpeg-9.0.2-3-build-recipes.zip")
    shutil.copy2(ROOT / "installer" / "ffmpeg-sources.lock.json", sources / "ffmpeg-sources.lock.json")
    # Include the unmodified worker source next to its separate-process dependencies.
    shutil.copytree(ROOT / "speech", sources / "jarvis-speech", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    download("https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/LICENSE.TXT", payload / "third-party" / "dotnet-LICENSE.txt")
    download("https://raw.githubusercontent.com/snakers4/silero-vad/066409f3d9db45a30d21dfcbf9147826ffdd7052/LICENSE", payload / "third-party" / "silero-LICENSE.txt")
    shutil.copy2(ROOT / "scripts" / "python_utf8_manifest.py", sources / "python_utf8_manifest.py")
    shutil.copy2(ROOT / "installer" / "THIRD-PARTY.md", payload / "third-party" / "README.md")
    (payload / "jarvis.install.json").write_text(json.dumps({"version": "0.10.0", "architecture": "win-x64"}), encoding="utf-8")
    hashes = {}
    for path in payload.rglob("*"):
        if path.is_file():
            with path.open("rb") as f:
                hashes[path.relative_to(payload).as_posix()] = hashlib.file_digest(f, "sha256").hexdigest()
    (payload / "payload-sha256.json").write_text(json.dumps(hashes, indent=2), encoding="utf-8")
    print(f"Prepared {len(hashes)} payload files; no profile data included.")


if __name__ == "__main__":
    main()
