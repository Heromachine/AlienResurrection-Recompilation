#!/usr/bin/env python3
"""Build a self-contained, single-file linux-x64 release artifact and zip it.

Mirrors the pattern used by the reference Crash Bandicoot recomp launcher: a plain publish script
rather than baking RuntimeIdentifier/SelfContained into the csproj, so a normal `dotnet build`
during development is unaffected and still targets whatever platform you're actually on.

Usage:
    DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH python3 publish_release.py

Requires the RecompOne-fork engine checked out as a sibling directory (../RecompOne-fork), same as
a normal build -- see the RecompOneDir override in AlienResurrectionLauncher.csproj if yours lives
elsewhere.
"""
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CSPROJ = ROOT / "AlienResurrectionLauncher.csproj"
RID = "linux-x64"
PUBLISH_DIR = ROOT / "publish" / RID
DIST_DIR = ROOT / "dist"


def read_version() -> str:
    text = CSPROJ.read_text()
    match = re.search(r"<Version>([^<]+)</Version>", text)
    if not match:
        sys.exit(f"error: no <Version> found in {CSPROJ}")
    return match.group(1)


def publish(version: str) -> None:
    if PUBLISH_DIR.exists():
        shutil.rmtree(PUBLISH_DIR)
    # NOT single-file. PublishSingleFile extracts native libs (libglfw.so.3 etc.) to a
    # randomized temp directory at startup, and Silk.NET's native library resolver does not
    # know to look there -- confirmed 2026-08-19 on a clean machine with no system-wide glfw:
    # the loader only searched the OS's standard library paths and never found the extracted
    # copy. It "worked" during dev only because the dev machine happened to already have glfw
    # installed as a system package, masking the bug. Plain self-contained publish instead
    # copies the native .so files as loose files next to the exe, which .NET's default
    # resolver DOES check automatically -- still no separate .NET runtime install required,
    # just not literally one file.
    cmd = [
        "dotnet", "publish", str(CSPROJ),
        "-c", "Release",
        "-r", RID,
        "--self-contained", "true",
        "-o", str(PUBLISH_DIR),
    ]
    print(f"[publish_release] {' '.join(cmd)}")
    subprocess.run(cmd, check=True, cwd=ROOT)


def package(version: str) -> Path:
    DIST_DIR.mkdir(exist_ok=True)
    archive_name = f"AlienResurrectionRecompilation-v{version}-{RID}"
    archive_path = DIST_DIR / f"{archive_name}.zip"
    if archive_path.exists():
        archive_path.unlink()

    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for file in PUBLISH_DIR.rglob("*"):
            if file.is_file():
                zf.write(file, arcname=Path(archive_name) / file.relative_to(PUBLISH_DIR))

    print(f"[publish_release] wrote {archive_path} ({archive_path.stat().st_size:,} bytes)")
    return archive_path


def main() -> None:
    version = read_version()
    print(f"[publish_release] version {version}, target {RID}")
    publish(version)
    package(version)


if __name__ == "__main__":
    main()
