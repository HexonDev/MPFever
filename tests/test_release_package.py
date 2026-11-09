"""Exercise the real packager with a fixture publish tree, including nested runtime resources and incomplete output."""
import importlib.util
import json
import pathlib
import sys
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
spec = importlib.util.spec_from_file_location("package_release", ROOT / "tools/package_release.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def main():
    with tempfile.TemporaryDirectory(prefix="mpfever-package-") as tmp:
        root = pathlib.Path(tmp)
        publish = root / "launcher/bin/ReleasePublish"
        publish.mkdir(parents=True)
        try:
            module.package(root, "0.7.13-beta.1")
            raise AssertionError("incomplete runtime was accepted")
        except ValueError as error:
            assert "incomplete" in str(error)
        for name in module.RUNTIME_FILES + ("fr/System.Windows.Forms.resources.dll", "LICENSE.txt", "ThirdPartyNotices.txt", "MPFever.pdb",
                                           "logs/selftest.log", "mpfever_settings.txt", "autotest_result.txt"):
            file = publish / name
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("fixture " + name)
        (publish / "MPFever.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"includedFrameworks": [
            {"name": "Microsoft.NETCore.App", "version": "10.0.12"}, {"name": "Microsoft.WindowsDesktop.App", "version": "10.0.12"}]}}))
        assets = root / "launcher/obj/project.assets.json"
        assets.parent.mkdir(parents=True)
        assets.write_text(json.dumps({"packageFolders": {str(root / "nuget"): {}}}))
        for runtime in ("microsoft.netcore.app", "microsoft.windowsdesktop.app"):
            restored = root / "nuget" / (runtime + ".runtime.win-x64") / "10.0.12"
            restored.mkdir(parents=True)
            (restored / "LICENSE.TXT").write_text(runtime + " license")
            if runtime == "microsoft.netcore.app":
                (restored / "THIRD-PARTY-NOTICES.TXT").write_text("runtime third party notices")
        for source in ("native/out/winhttp.dll", "native/mpfever_sigs.txt", "mod/mpfever_1/mod.json", "LICENSE", "NOTICE.txt",
                       "docs/README.txt", "docs/INSTALL.txt", "docs/ROADMAP.txt", "docs/CHANGELOG.txt", "docs/PRIVACY.txt",
                       "docs/LUA-OPTIMIZATIONS.md", "docs/PIPE-PROBE.md", "docs/SERVER.txt", "docs/server-edition.txt",
                       "launcher/start-server.bat", "serverlist/bin/Release/MPFeverServerList.exe",
                       "serverlist/bin/Release/MPFeverServerList.exe.config", "serverlist/install-service.bat",
                       "serverlist/uninstall-service.bat", "serverlist/README.md"):
            file = root / source
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("fixture " + source)
        module.package(root, "0.7.13-beta.1")
        with zipfile.ZipFile(root / "release/MPFever-0.7.13-beta.1.zip") as archive:
            names = set(archive.namelist())
            assert "MPFever/fr/System.Windows.Forms.resources.dll" in names
            assert "MPFever/coreclr.dll" in names and "MPFever/MPFever.dll" in names
            assert "MPFever/MPFever.pdb" not in names
            assert not any("/logs/" in name or name.endswith("mpfever_settings.txt") or name.endswith("autotest_result.txt") for name in names)
            assert archive.read("MPFever/licenses/dotnet-LICENSE.txt") == b"fixture LICENSE.txt"
            assert archive.read("MPFever/LICENSE.txt") == b"fixture LICENSE"
            assert archive.read("MPFever/licenses/Microsoft.NETCore.App-THIRD-PARTY-NOTICES.txt") == b"runtime third party notices"
            assert "MPFever/licenses/Microsoft.WindowsDesktop.App-LICENSE.txt" in names
            assert b'%~dp0MPFever.exe' in archive.read("MPFever/start-pipe-probe.bat")
            assert not any("server-edition" in name for name in names)
        with zipfile.ZipFile(root / "release/MPFever-0.7.13-beta.1-server.zip") as archive:
            assert "MPFever-Server/server-edition.txt" in archive.namelist()
            assert "MPFever-Server/coreclr.dll" in archive.namelist()
        assert len((root / "release/SHA256SUMS-0.7.13-beta.1.txt").read_text().splitlines()) == 3
        (root / "release/dist/stale.txt").write_text("stale")
        module.package(root, "0.7.13-beta.1")
        assert not (root / "release/dist/stale.txt").exists()
    print("OK: complete runtime, nested resources, license preservation, player/server distinction and clean staging")


if __name__ == "__main__":
    main()
