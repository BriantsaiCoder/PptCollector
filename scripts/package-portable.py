"""Build the Windows x64 portable distribution and its separate updater payload."""
import argparse
import json
from pathlib import Path
import re
from zipfile import ZIP_DEFLATED, ZipFile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="Version embedded in the Windows executable")
    parser.add_argument("--exe", type=Path, default=Path("target/release/ppt-collector.exe"))
    parser.add_argument("--engine", type=Path, default=Path("engine/publish"))
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    version = args.version
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)?", version, flags=re.ASCII):
        parser.error("--version must be a release or preview version without build metadata")
    if not args.exe.is_file() or not (args.engine / "PptCollector.Engine.exe").is_file():
        parser.error("build the Windows x64 app and publish its Windows engine first")
    engine_files = sorted(args.engine.iterdir())
    if any(not f.is_file() or f.is_symlink() for f in engine_files):
        parser.error("the Windows engine publish folder must contain regular files only")

    args.out.mkdir(parents=True, exist_ok=True)
    prefix = f"PptCollector-v{version}-windows-x64"
    slot = f"build-{version}"
    files = [(args.exe, "ppt-collector.exe"), *((f, f"engine/{f.name}") for f in engine_files)]

    portable = args.out / f"{prefix}-portable.zip"
    with ZipFile(portable, "w", ZIP_DEFLATED) as archive:
        archive.write(args.exe, f"{prefix}/PptCollector.exe")
        archive.writestr(f"{prefix}/portable-state.json", json.dumps({"current": slot, "previous": None}))
        for file, name in files:
            archive.write(file, f"{prefix}/versions/{slot}/{name}")

    update = args.out / f"{prefix}-portable-update.zip"
    with ZipFile(update, "w", ZIP_DEFLATED) as archive:
        for file, name in files:
            archive.write(file, name)

    print(portable)
    print(update)


if __name__ == "__main__":
    main()
