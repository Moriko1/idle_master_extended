"""Build and verify the x64 portable application with full Visual Studio MSBuild."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MAIN = ROOT / "Source/IdleMasterExtended/bin/x64/Release"
TESTS = ROOT / "Source/IdleMasterExtended.Tests/bin/x64/Release/IdleMasterExtended.Tests.exe"
WEBVIEW_VERSION = "1.0.4258.31"


def run(arguments):
    print("> " + subprocess.list2cmdline([str(value) for value in arguments]), flush=True)
    subprocess.run([str(value) for value in arguments], cwd=ROOT, check=True)


def msbuild():
    configured = os.environ.get("MSBUILD_EXE_PATH")
    if configured and Path(configured).is_file():
        return Path(configured)
    found = shutil.which("MSBuild.exe") or shutil.which("msbuild")
    if found and "Microsoft.NET" not in found:
        return Path(found)
    candidates = []
    for variable in ("ProgramFiles", "ProgramFiles(x86)"):
        base = Path(os.environ.get(variable, "C:/Program Files")) / "Microsoft Visual Studio"
        if base.exists():
            candidates.extend(base.glob("*/MSBuild/Current/Bin/amd64/MSBuild.exe"))
            candidates.extend(base.glob("*/*/MSBuild/Current/Bin/amd64/MSBuild.exe"))
    if candidates:
        return sorted(candidates, reverse=True)[0]
    raise RuntimeError("Install Visual Studio with the .NET desktop workload and .NET Framework 4.8 targeting pack.")


def require_x64(path):
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", data, pe + 4)[0] != 0x8664:
        raise RuntimeError("Expected an x64 PE executable: " + str(path))


def package(version):
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", version):
        raise RuntimeError("Version must be a semantic version such as 1.12.0-preview.1.")
    changes = subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True)
    if changes.strip():
        raise RuntimeError("Commit all changes before packaging so the source archive matches the built binaries.")
    required = (
        "IdleMasterExtended.exe", "IdleMasterExtended.exe.config",
        "steam-idle.exe", "steam-idle.exe.config",
        "steam_api64.dll", "Steamworks.NET.dll", "HtmlAgilityPack.dll",
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
    )
    contents = {}
    for name in required:
        path = MAIN / name
        if not path.is_file():
            raise RuntimeError("Missing packaged dependency: " + str(path))
        contents[name] = path
    for name in ("IdleMasterExtended.exe", "steam-idle.exe"):
        require_x64(contents[name])
    # Include only the x64 WebView loader; never bundle a full browser runtime.
    loader = next((path for path in (
        MAIN / "WebView2Loader.dll", MAIN / "x64/WebView2Loader.dll",
        MAIN / "runtimes/win-x64/native/WebView2Loader.dll"
    ) if path.is_file()), None)
    if loader is None:
        raise RuntimeError("The x64 WebView2Loader.dll is missing.")
    require_x64(loader)
    contents["WebView2Loader.dll"] = loader
    language_root = MAIN / "Languages"
    languages = list(language_root.glob("*/IdleMasterExtended.resources.dll"))
    if not languages:
        raise RuntimeError("No localization satellite assemblies were generated.")
    for path in languages:
        contents[path.relative_to(MAIN).as_posix()] = path
    for source, destination in (
        ("README.md", "README.md"), ("LICENSE", "LICENSE"),
        ("docs/BUILD.md", "docs/BUILD.md"),
        ("docs/VALIDATION.md", "docs/VALIDATION.md"),
        ("docs/THIRD_PARTY.md", "docs/THIRD_PARTY.md"),
    ):
        contents[destination] = ROOT / source
    for path in (ROOT / "docs/licenses").glob("*.txt"):
        contents["licenses/" + path.name] = path
    nuget = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    webview = nuget / "microsoft.web.webview2" / WEBVIEW_VERSION
    license_file = next((path for path in (webview / "LICENSE.txt", webview / "LICENSE") if path.is_file()), None)
    if license_file is None:
        raise RuntimeError("The WebView2 redistributable license was not found in the restored package.")
    contents["licenses/WebView2-LICENSE.txt"] = license_file
    if (webview / "NOTICE.txt").is_file():
        contents["licenses/WebView2-NOTICE.txt"] = webview / "NOTICE.txt"
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    portable = dist / ("IdleMasterExtended-" + version + "-win-x64.zip")
    source = dist / ("idle_master_extended-" + version + "-source.zip")
    with zipfile.ZipFile(portable, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, path in sorted(contents.items()):
            archive.write(path, name)
    run(["git", "archive", "--format=zip", "--prefix=idle_master_extended-" + version + "/",
         "-o", source, "HEAD"])
    checksum = dist / "SHA256SUMS.txt"
    checksum.write_text("".join(
        hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name + "\n"
        for path in (portable, source)
    ), encoding="ascii")
    print("Packaged " + str(portable), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", action="store_true")
    parser.add_argument("--version", default="1.12.0-preview.1")
    args = parser.parse_args()
    run([msbuild(), ROOT / "Source/IdleMasterExtended.sln", "/restore",
         "/t:Rebuild", "/m", "/p:Configuration=Release", "/p:Platform=x64", "/verbosity:minimal"])
    require_x64(MAIN / "IdleMasterExtended.exe")
    require_x64(MAIN / "steam-idle.exe")
    run([TESTS])
    if args.package:
        package(args.version)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print("Build failed: " + str(error), file=sys.stderr)
        sys.exit(1)
