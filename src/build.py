"""Build with the existing Windows .NET Framework compiler; no SDK install needed."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / "work" / "build"
DOWNLOADS = ROOT / "work" / "downloads"
OUT = ROOT / "outputs"
CSC = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
PACKAGES = {
    "BepInExPack_IL2CPP-6.0.788.zip": "ce25d925f9eaba817214f5d6b2083b8e135ed0592b5a61b9f75ea5d4c02f5300",
    "ThirdPersonComfort-1.1.0.zip": "5bdc30039a8a763907f84b7248ca18d201456cf444656411bdce1c5433743cec",
}
URLS = {
    "BepInExPack_IL2CPP-6.0.788.zip": "https://thunderstore.io/package/download/BepInEx/BepInExPack_IL2CPP/6.0.788/",
    "ThirdPersonComfort-1.1.0.zip": "https://thunderstore.io/package/download/Ranats/ThirdPersonComfort/1.1.0/",
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def prepare():
    WORK.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(exist_ok=True)
    DOWNLOADS.mkdir(parents=True, exist_ok=True)
    records = []
    for name in PACKAGES:
        dest = DOWNLOADS / name
        if not dest.exists():
            temp = dest.with_suffix(".part")
            request = urllib.request.Request(URLS[name], headers={"User-Agent": "BigWalkThirdPersonInstaller/1.0.2"})
            try:
                with urllib.request.urlopen(request, timeout=60) as response, temp.open("wb") as output:
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        output.write(chunk)
                if sha(temp.read_bytes()) != PACKAGES[name]:
                    raise RuntimeError("Downloaded package hash mismatch: " + name)
                temp.replace(dest)
            finally:
                if temp.exists():
                    temp.unlink()
        actual = sha((DOWNLOADS / name).read_bytes())
        if actual != PACKAGES[name]:
            raise RuntimeError("Downloaded package hash mismatch: " + name)
        records.append({"file": name, "url": URLS[name], "sha256": actual, "bytes": dest.stat().st_size})
    (DOWNLOADS / "packages.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
    files = {}
    with zipfile.ZipFile(DOWNLOADS / "BepInExPack_IL2CPP-6.0.788.zip") as z:
        for entry in z.infolist():
            if entry.is_dir() or not entry.filename.startswith("BepInExPack/"):
                continue
            name = entry.filename[len("BepInExPack/"):]
            if name == "changelog.txt":
                continue
            data = z.read(entry)
            if name == "BepInEx/config/BepInEx.cfg":
                # Avoid a separate console window. Keep normal disk diagnostics.
                text = data.decode("utf-8-sig").replace("\r\r\n", "\n").replace("\r\n", "\n")
                import re
                text = re.sub(r"(\[Logging.Console\][\s\S]*?\nEnabled = )true", r"\g<1>false", text)
                data = text.replace("\n", "\r\n").encode("utf-8")
            files[name] = (data, "loader")
    with zipfile.ZipFile(DOWNLOADS / "ThirdPersonComfort-1.1.0.zip") as z:
        files["BepInEx/plugins/ThirdPersonComfort/BigWalkThirdPerson.dll"] = (z.read("plugins/BigWalkThirdPerson.dll"), "mod")

    # Licenses travel with the loader and are removed with an owned loader only.
    lic = ROOT / "third_party/licenses"
    for path in lic.glob("*.txt"):
        files["BepInEx/licenses/" + path.name] = (path.read_bytes(), "loader")
    files["BepInEx/licenses/ThirdPersonComfort-MIT.txt"] = ((ROOT / "third_party/ThirdPersonComfort/LICENSE.md").read_bytes(), "loader")

    index = []
    with zipfile.ZipFile(WORK / "payload.zip", "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, (data, kind) in sorted(files.items()):
            if name.startswith("/") or ".." in name.split("/") or ":" in name:
                raise RuntimeError("Unsafe payload path")
            info = zipfile.ZipInfo(name, (2026, 10, 19, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
            index.append({"Path": name, "Hash": sha(data), "Size": len(data), "Kind": kind})
    (WORK / "payload-index.json").write_text(json.dumps(index, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    (WORK / "BuildData.cs").write_text('namespace BigWalkInstaller { static class BuildData { public const string PayloadHash = "' + sha((WORK / "payload.zip").read_bytes()) + '"; } }', encoding="utf-8")

    return records, index


def build(tests=False):
    output = WORK / "InstallerTests.exe" if tests else OUT / "BigWalk第三人称安装器.exe"
    args = [str(CSC), "/nologo", "/optimize+", "/platform:x64", "/codepage:65001", "/target:exe" if tests else "/target:winexe", "/out:" + str(output)]
    for assembly in ["System.dll", "System.Core.dll", "System.Web.Extensions.dll", "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll"]:
        args.append("/reference:" + assembly)
    if not tests:
        args.extend(["/reference:System.Windows.Forms.dll", "/reference:System.Drawing.dll", "/win32manifest:" + str(ROOT / "src/app.manifest")])
        if (WORK / "app.ico").exists():
            args.append("/win32icon:" + str(WORK / "app.ico"))
    args.extend(["/resource:" + str(WORK / "payload.zip") + ",payload.zip", "/resource:" + str(WORK / "payload-index.json") + ",payload-index.json", str(ROOT / "src/InstallerCore.cs"), str(WORK / "BuildData.cs"), str(ROOT / ("src/InstallerTests.cs" if tests else "src/Program.cs"))])
    env = os.environ.copy()
    env["TEMP"] = env["TMP"] = str(WORK)
    subprocess.run(args, check=True, cwd=ROOT, env=env)
    print(output, output.stat().st_size, sha(output.read_bytes()), flush=True)
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--tests", action="store_true")
    parser.add_argument("--skip-payload", action="store_true")
    args = parser.parse_args()
    if not args.skip_payload:
        prepare()
    build(args.tests)
