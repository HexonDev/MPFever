"""Package the complete .NET 10 publish output, mod, native DLL and documentation. Run only after release tests pass."""
import hashlib
import json
import pathlib
import re
import shutil
import sys

from makezip import pack

ROOT = pathlib.Path(__file__).resolve().parents[1]
RUNTIME_FILES = (
    "MPFever.exe", "MPFever.dll", "MPFever.deps.json", "MPFever.runtimeconfig.json",
    "System.Private.CoreLib.dll", "System.Windows.Forms.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
)


def runtime_notices(root, frameworks):
    # Runtime-pack notices are not copied by dotnet publish. Preserve the exact restored packages' notices.
    assets = json.loads((root / "launcher/obj/project.assets.json").read_text(encoding="utf-8"))
    folders = [pathlib.Path(path) for path in assets["packageFolders"]]
    result = []
    for framework in frameworks:
        name, version = framework["name"], framework["version"]
        package_name = name.lower() + ".runtime.win-x64"
        package_dir = next((folder / package_name / version for folder in folders if (folder / package_name / version).is_dir()), None)
        if package_dir is None:
            raise ValueError("runtime license package not found: " + package_name + "/" + version)
        license_file = next((package_dir / file for file in ("LICENSE.TXT", "LICENSE") if (package_dir / file).is_file()), None)
        if license_file is None:
            raise ValueError("runtime license missing: " + str(package_dir))
        result.append((license_file, name + "-LICENSE.txt"))
        notices = package_dir / "THIRD-PARTY-NOTICES.TXT"
        if notices.is_file():
            result.append((notices, name + "-THIRD-PARTY-NOTICES.txt"))
        elif name == "Microsoft.NETCore.App":
            raise ValueError("runtime third-party notices missing: " + str(package_dir))
    return result


def package(root, version):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?", version):
        raise ValueError("invalid release version")
    root = pathlib.Path(root).resolve()
    publish = root / "launcher/bin/ReleasePublish"
    missing = [name for name in RUNTIME_FILES if not (publish / name).is_file()]
    if missing:
        raise ValueError("incomplete self-contained publish: " + ", ".join(missing))
    config = json.loads((publish / "MPFever.runtimeconfig.json").read_text(encoding="utf-8"))
    frameworks = config.get("runtimeOptions", {}).get("includedFrameworks", [])
    if not any(f.get("name") == "Microsoft.WindowsDesktop.App" and f.get("version", "").startswith("10.") for f in frameworks):
        raise ValueError("publish must include the .NET 10 Windows Desktop runtime")
    notices = runtime_notices(root, frameworks)
    for source in ("native/out/winhttp.dll", "native/mpfever_sigs.txt", "serverlist/bin/Release/MPFeverServerList.exe"):
        if not (root / source).is_file():
            raise ValueError("missing build input: " + source)
    release = root / "release"
    # Never follow a staging-root symlink/junction into an unrelated directory when replacing generated outputs.
    if release.resolve() != root / "release":
        raise ValueError("release directory resolves outside the repository's release directory")
    release.mkdir(exist_ok=True)
    for name in ("dist", "dist-server", "dist-serverlist"):
        stage = release / name
        if stage.resolve() != release / name:
            raise ValueError("staging directory redirects outside its expected path: " + str(stage))
        if stage.exists():
            shutil.rmtree(stage)
        stage.mkdir()

    player = release / "dist/MPFever"
    shutil.copytree(publish, player, ignore=shutil.ignore_patterns("*.pdb"))
    licenses = player / "licenses"
    licenses.mkdir(exist_ok=True)
    for source, name in notices:
        shutil.copy2(source, licenses / name)
    # Keep the bundled runtime's license when giving MPFever's GPL license its normal archive filename.
    if (player / "LICENSE.txt").is_file():
        shutil.copy2(player / "LICENSE.txt", licenses / "dotnet-LICENSE.txt")
    shutil.copy2(root / "LICENSE", player / "LICENSE.txt")
    shutil.copy2(root / "NOTICE.txt", player / "NOTICE.txt")
    shutil.copy2(root / "native/out/winhttp.dll", player / "winhttp.dll")
    shutil.copy2(root / "native/mpfever_sigs.txt", player / "mpfever_sigs.txt")
    shutil.copytree(root / "mod/mpfever_1", player / "mod/mpfever_1")
    for name in ("README.txt", "INSTALL.txt", "ROADMAP.txt", "CHANGELOG.txt", "PRIVACY.txt", "LUA-OPTIMIZATIONS.md", "PIPE-PROBE.md"):
        shutil.copy2(root / "docs" / name, player / name)
    (player / "start-pipe-probe.bat").write_text(
        '@echo off\nsetlocal\nset "MPFEVER_IPC_PROBE=1"\nset "MPFEVER_PIPE="\n'
        'start "" /D "%~dp0" "%~dp0MPFever.exe" --dev\n', encoding="ascii")

    server = release / "dist-server/MPFever-Server"
    shutil.copytree(player, server)
    for source in ("docs/SERVER.txt", "docs/server-edition.txt", "launcher/start-server.bat"):
        shutil.copy2(root / source, server / pathlib.Path(source).name)

    listing = release / "dist-serverlist/MPFever-ServerList"
    listing.mkdir()
    for source in ("serverlist/bin/Release/MPFeverServerList.exe", "serverlist/bin/Release/MPFeverServerList.exe.config",
                   "serverlist/install-service.bat", "serverlist/uninstall-service.bat", "serverlist/README.md"):
        shutil.copy2(root / source, listing / pathlib.Path(source).name)

    hashes = []
    for folder, name in ((player, f"MPFever-{version}.zip"), (server, f"MPFever-{version}-server.zip"),
                         (listing, f"MPFever-ServerList-{version}.zip")):
        archive = release / name
        count = pack(folder, archive)
        hashes.append(hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + name)
        print(f"BUILD OK: {archive} ({count} entries)", flush=True)
    (release / f"SHA256SUMS-{version}.txt").write_text("\n".join(hashes) + "\n", encoding="ascii")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: python tools/package_release.py VERSION")
    package(ROOT, sys.argv[1])
