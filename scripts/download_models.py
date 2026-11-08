"""Download only pinned model assets. No code is loaded from model repositories."""

import hashlib
import json
import os
import shutil
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODELS = ROOT / "models"
os.environ["HF_HOME"] = str(ROOT / ".tools" / "huggingface")
os.environ["HF_XET_CACHE"] = str(ROOT / ".tools" / "huggingface" / "xet")
os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"


def download(url: str, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        return
    print(f"Downloading {target.name}", flush=True)
    part = target.with_suffix(target.suffix + ".part")
    with urllib.request.urlopen(url, timeout=120) as response, part.open("wb") as out:
        shutil.copyfileobj(response, out)
    part.replace(target)


def main() -> None:
    from huggingface_hub import snapshot_download

    manifest = json.loads((ROOT / "models.lock.json").read_text(encoding="utf-8"))
    MODELS.mkdir(exist_ok=True)
    asr = manifest["asr"]
    snapshot_download(
        repo_id=asr["repo"],
        revision=asr["revision"],
        local_dir=MODELS / "whisper-small",
        allow_patterns=[
            "config.json",
            "model.bin",
            "tokenizer.json",
            "vocabulary.*",
            "preprocessor_config.json",
        ],
    )
    voice = manifest["voice"]
    for filename in voice["files"]:
        download(
            f"https://huggingface.co/{voice['repo']}/resolve/{voice['revision']}/{filename}",
            MODELS / "piper" / Path(filename).name,
        )
    download(manifest["vad"]["url"], MODELS / "silero_vad.onnx")
    wake = manifest["wake"]
    archive = MODELS / "vosk.zip"
    target = MODELS / wake["directory"]
    if not (target / "am" / "final.mdl").exists():
        download(wake["url"], archive)
        with zipfile.ZipFile(archive) as zf:
            for entry in zf.infolist():
                dest = (MODELS / entry.filename).resolve()
                if not dest.is_relative_to(MODELS.resolve()):
                    raise ValueError("Unexpected archive path")
            zf.extractall(MODELS)
    checksums = {}
    for path in MODELS.rglob("*"):
        if path.is_file() and ".cache" not in path.parts and path.name != "checksums.json":
            with path.open("rb") as source:
                checksums[str(path.relative_to(MODELS)).replace("\\", "/")] = hashlib.file_digest(
                    source, "sha256"
                ).hexdigest()
    for filename, expected in manifest.get("sha256", {}).items():
        if checksums.get(filename) != expected:
            raise ValueError(f"Model checksum mismatch: {filename}. Restore this asset before running.")
    (MODELS / "checksums.json").write_text(json.dumps(checksums, indent=2), encoding="utf-8")
    print("Models are ready.", flush=True)


if __name__ == "__main__":
    main()
