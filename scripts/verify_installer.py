"""Verify installed payload integrity without reading user profiles."""
import argparse
import hashlib
import json
from pathlib import Path


def verify(root: Path):
    root = root.resolve()
    manifest = json.loads((root / "payload-sha256.json").read_text(encoding="utf-8"))
    for relative, expected in manifest.items():
        path = (root / relative).resolve()
        if not path.is_relative_to(root):
            raise ValueError("Invalid manifest path")
        with path.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != expected:
                raise ValueError("Payload integrity failed: " + relative)
    forbidden = ["data", ".venv", ".git", ".env", "work"]
    if any((root / name).exists() for name in forbidden):
        raise ValueError("Payload contains development or private data")
    print(f"Verified {len(manifest)} files; no development profile bundled.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("payload", type=Path)
    verify(parser.parse_args().payload)
