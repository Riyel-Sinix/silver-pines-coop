"""Refresh release documentation without changing the tested plugin or runtime.

Usage: python tools/prepare_release.py [--full-package path] [--plugin path]
The full package must already contain a clean BepInEx be.790 installation.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path, PurePosixPath
import re
import uuid
import zipfile


ROOT = Path(__file__).resolve().parents[1]
PLUGIN_DIR = "BepInEx/plugins/SilverPinesCoop/"
PLUGIN_NAME = PLUGIN_DIR + "SilverPinesCoop.dll"


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify(path: Path, plugin_hash: str, *, full: bool) -> None:
    with zipfile.ZipFile(path) as package:
        names = package.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate ZIP entries")
        for name in names:
            parts = PurePosixPath(name).parts
            if "\\" in name or name.startswith("/") or ".." in parts or ":" in name:
                raise ValueError(f"Invalid ZIP path: {name}")
            if name.startswith(("BepInEx/interop/", "BepInEx/coop-test/", "Carpenter_Data/")) or name.lower().endswith(".log"):
                raise ValueError(f"Local game/generated data in release: {name}")
        if package.testzip() is not None:
            raise ValueError("Corrupt ZIP entry")
        actual = hashlib.sha256(package.read(PLUGIN_NAME)).hexdigest()
        if actual != plugin_hash:
            raise ValueError("Packaged plugin differs from verified DLL")
        required = ["README.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "安装说明.txt",
                    PLUGIN_DIR + "coop_local_test.bat", PLUGIN_DIR + "coop_local_test.ps1"]
        if full:
            required += ["winhttp.dll", "doorstop_config.ini", "dotnet/coreclr.dll",
                         "BepInEx/core/BepInEx.Unity.IL2CPP.dll", "BepInEx/unity-libs/6000.3.15.zip"]
        elif any(name.startswith(("dotnet/", "BepInEx/core/", "BepInEx/unity-libs/")) for name in names):
            raise ValueError("Runtime dependency in plugin-only package")
        for name in required:
            package.getinfo(name)


def main() -> None:
    version = re.search(r'public const string Version = "([^"]+)"',
                        (ROOT / "src/Plugin.cs").read_text(encoding="utf-8-sig")).group(1)
    out = ROOT / "dist"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--full-package", type=Path,
                        default=out / f"SilverPinesCoop-{version}-BepInEx-be790.zip")
    parser.add_argument("--plugin", type=Path, default=ROOT / "bin/Release/net6.0/SilverPinesCoop.dll")
    args = parser.parse_args()
    plugin_hash = digest(args.plugin)
    full_path = out / f"SilverPinesCoop-{version}-BepInEx-be790.zip"
    plugin_path = out / f"SilverPinesCoop-{version}-plugin-only.zip"
    documents = {"README.md": ROOT / "README.md", "LICENSE": ROOT / "LICENSE",
                 "THIRD_PARTY_NOTICES.md": ROOT / "THIRD_PARTY_NOTICES.md",
                 "安装说明.txt": ROOT / "docs/安装说明.txt"}
    documents.update({p.relative_to(ROOT).as_posix(): p for p in (ROOT / "licenses").rglob("*") if p.is_file()})
    if not documents or not (ROOT / "licenses").is_dir():
        raise ValueError("Third-party license files are missing")
    # Check all inputs before creating output. Preserve the tested plugin bytes.
    for path in documents.values():
        if not path.is_file():
            raise FileNotFoundError(path)
    with zipfile.ZipFile(args.full_package) as source:
        if hashlib.sha256(source.read(PLUGIN_NAME)).hexdigest() != plugin_hash:
            raise ValueError("Existing full package does not contain the selected verified DLL")
    out.mkdir(exist_ok=True)
    staged = []
    try:
        for destination, full in ((full_path, True), (plugin_path, False)):
            temporary = destination.with_name("." + destination.name + "." + uuid.uuid4().hex + ".tmp")
            staged.append((temporary, destination))
            with zipfile.ZipFile(temporary, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as package:
                if full:
                    with zipfile.ZipFile(args.full_package) as source:
                        for info in source.infolist():
                            if info.filename in documents or info.filename.startswith("licenses/"):
                                continue
                            package.writestr(info, source.read(info))
                else:
                    package.write(args.plugin, PLUGIN_NAME)
                    for name in ("coop_local_test.bat", "coop_local_test.ps1"):
                        package.write(ROOT / "tools" / name, PLUGIN_DIR + name)
                for name, path in documents.items():
                    package.write(path, name)
            verify(temporary, plugin_hash, full=full)
        for temporary, destination in staged:
            temporary.replace(destination)
    finally:
        for temporary, _ in staged:
            temporary.unlink(missing_ok=True)
    (out / "安装说明.txt").write_bytes((ROOT / "docs/安装说明.txt").read_bytes())
    checksums = [digest(path) + "  " + path.name for path in (full_path, plugin_path)]
    (out / "SHA256SUMS.txt").write_text("\n".join(checksums) + "\n", encoding="ascii")
    print("Verified plugin SHA256:", plugin_hash)
    print("\n".join(checksums))


if __name__ == "__main__":
    main()
