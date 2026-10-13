#!/usr/bin/env python3
"""Build a self-contained linux-x64 release: a .zip and an AppImage.

Mirrors the pattern used by the reference Crash Bandicoot recomp launcher: a plain publish script
rather than baking RuntimeIdentifier/SelfContained into the csproj, so a normal `dotnet build`
during development is unaffected and still targets whatever platform you're actually on.

The AppImage is the primary distributable -- download, mark executable, double-click, no
extraction or terminal required. The .zip is kept alongside it for anyone who wants the loose
files instead.

Usage:
    DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH python3 publish_release.py

Requires the RecompOne-fork engine checked out as a sibling directory (../RecompOne-fork), same as
a normal build -- see the RecompOneDir override in AlienResurrectionLauncher.csproj if yours lives
elsewhere. Also requires `appimagetool` on PATH (e.g. `paru -S appimagetool-bin` on Arch), and
`zsyncmake` (the `zsync` package) for the update information below.

Update information: the AppImage embeds UPDATE_INFO, which tells AppImageUpdate and similar tools
to look for newer builds in this repo's latest GitHub release. appimagetool writes a matching
`<AppImage>.zsync` next to the AppImage, and BOTH files must be uploaded to every release -- the
.zsync is what the updater downloads to work out which parts of the file changed.
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
APPDIR = ROOT / "publish" / "AppDir"
APPIMAGE_ASSETS = ROOT / "appimage"
APP_NAME = "AlienResurrectionRecompilation"
UPDATE_INFO = (
    "gh-releases-zsync|Heromachine|AlienResurrection-Recompilation|latest"
    f"|{APP_NAME}-*-x86_64.AppImage.zsync"
)


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


def build_appimage(version: str) -> Path:
    if APPDIR.exists():
        shutil.rmtree(APPDIR)
    bin_dir = APPDIR / "usr" / "bin"
    bin_dir.mkdir(parents=True)
    for item in PUBLISH_DIR.iterdir():
        dest = bin_dir / item.name
        if item.is_dir():
            shutil.copytree(item, dest)
        else:
            shutil.copy2(item, dest)

    # AppImage requires the .desktop file and icon at the AppDir root (not inside usr/).
    shutil.copy2(APPIMAGE_ASSETS / f"{APP_NAME}.desktop", APPDIR / f"{APP_NAME}.desktop")
    shutil.copy2(APPIMAGE_ASSETS / f"{APP_NAME}.png", APPDIR / f"{APP_NAME}.png")

    # AppRun does NOT need to cd anywhere: the app resolves assets/, GameTemplate/, and the
    # display font via AppContext.BaseDirectory (see Program.cs), which .NET sets from the
    # actual executable's location -- not the caller's cwd. So just exec it directly.
    apprun = APPDIR / "AppRun"
    apprun.write_text(
        "#!/bin/sh\n"
        'HERE="$(dirname "$(readlink -f "${0}")")"\n'
        'exec "${HERE}/usr/bin/AlienResurrectionLauncher" "$@"\n'
    )
    apprun.chmod(0o755)

    DIST_DIR.mkdir(exist_ok=True)
    appimage_path = DIST_DIR / f"{APP_NAME}-v{version}-x86_64.AppImage"
    zsync_path = appimage_path.with_name(appimage_path.name + ".zsync")
    for stale in (appimage_path, zsync_path):
        if stale.exists():
            stale.unlink()

    # appimagetool only warns and carries on when zsyncmake is missing, which would ship an
    # AppImage that points at a .zsync nobody can download. Fail before building instead.
    if shutil.which("zsyncmake") is None:
        sys.exit("error: zsyncmake not found on PATH (install the `zsync` package)")

    cmd = ["appimagetool", "-u", UPDATE_INFO, str(APPDIR), str(appimage_path)]
    print(f"[publish_release] {' '.join(cmd)}")
    subprocess.run(cmd, check=True, cwd=ROOT)

    if not zsync_path.exists():
        sys.exit(f"error: appimagetool did not write {zsync_path}")

    print(f"[publish_release] wrote {appimage_path} ({appimage_path.stat().st_size:,} bytes)")
    print(f"[publish_release] wrote {zsync_path} -- upload it to the release with the AppImage")
    return appimage_path


def main() -> None:
    version = read_version()
    print(f"[publish_release] version {version}, target {RID}")
    publish(version)
    package(version)
    build_appimage(version)


if __name__ == "__main__":
    main()
