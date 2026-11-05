"""BOOTH 配布用の、VCC への導入案内の .unitypackage を作る。

  python scripts/make_unitypackage.py        # dist/Nt_AvatorSupports_Setup_v<バージョン>.unitypackage

- ツール本体は入れない。VCC にリポジトリを追加するウィンドウと README だけ。
- 同じ内容から、いつも同じファイルができる(日時・GUID を固定している)。
"""
import gzip
import hashlib
import io
import json
import os
import sys
import tarfile

BASE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PKG_ROOT = os.path.join(BASE, "Packages")
DIST = os.path.join(BASE, "dist")

ROOT = "Assets/nyaohtech/Nt_AvatorSupports_Setup"
NAME = "Nt_AvatorSupports_Setup"
FILES = os.path.join(BASE, "scripts", "unitypackage_files")


def guid(path):
    return hashlib.md5(("nyaohtech-unitypackage/" + path).encode("utf-8")).hexdigest()


def meta(g, kind):
    tail = "  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    if kind == "folder":
        return f"fileFormatVersion: 2\nguid: {g}\nfolderAsset: yes\nDefaultImporter:\n{tail}"
    if kind == "cs":
        return (f"fileFormatVersion: 2\nguid: {g}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n"
                "  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    if kind == "asmdef":
        return f"fileFormatVersion: 2\nguid: {g}\nAssemblyDefinitionImporter:\n{tail}"
    if kind == "text":
        return f"fileFormatVersion: 2\nguid: {g}\nTextScriptImporter:\n{tail}"
    return f"fileFormatVersion: 2\nguid: {g}\nDefaultImporter:\n{tail}"


def lf(data):
    return data.replace(b"\r\n", b"\n")


def read_text(path):
    with open(path, "rb") as f:
        return lf(f.read())


def main():
    cfg = json.load(open(os.path.join(BASE, "scripts", "unitypackage.json"), encoding="utf-8"))
    version = cfg["version"]
    entries = {}   # Assets からのパス -> (種類, 中身 or None)

    def add_dirs(path):
        parts = path.split("/")
        for i in range(2, len(parts)):
            entries.setdefault("/".join(parts[:i]), ("folder", None))

    asmdef = {"name": "Nyaohtech.Setup.Editor", "rootNamespace": "Nyaohtech.Setup", "includePlatforms": ["Editor"]}
    src = [
        (f"{ROOT}/Editor/NtSetup.cs", "cs", read_text(os.path.join(FILES, "NtSetup.cs"))),
        (f"{ROOT}/Editor/Nyaohtech.Setup.Editor.asmdef", "asmdef", (json.dumps(asmdef, indent=4) + chr(10)).encode("utf-8")),
        (f"{ROOT}/README.txt", "text", read_text(os.path.join(FILES, "README.txt"))),
    ]
    for dest, kind, data in src:
        add_dirs(dest)
        entries[dest] = (kind, data)
    for d in ("Assets/nyaohtech", ROOT):
        entries[d] = ("folder", None)

    os.makedirs(DIST, exist_ok=True)
    out = os.path.join(DIST, f"{NAME}_v{version}.unitypackage")
    buf = io.BytesIO()
    MTIME = 1735689600   # 固定の日時(同じ内容から同じファイルができるように)
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as tar:
        def put(name, data):
            ti = tarfile.TarInfo(name)
            ti.size = len(data)
            ti.mtime = MTIME
            ti.mode = 0o777
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = ""
            tar.addfile(ti, io.BytesIO(data))

        def put_dir(name):
            ti = tarfile.TarInfo(name)
            ti.type = tarfile.DIRTYPE
            ti.mtime = MTIME
            ti.mode = 0o777
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = ""
            tar.addfile(ti)

        # Unity が書き出すパッケージと同じ並び: <guid>/ , asset , asset.meta , pathname
        for path in sorted(entries):
            kind, data = entries[path]
            g = guid(path)
            put_dir(g)
            if data is not None:
                put(f"{g}/asset", data)
            put(f"{g}/asset.meta", meta(g, kind).encode("utf-8"))
            put(f"{g}/pathname", path.encode("utf-8"))
    with open(out, "wb") as f:
        with gzip.GzipFile(filename="archtemp.tar", fileobj=f, mode="wb", mtime=MTIME) as gz:   # Unity は archtemp.tar の名前がないと読まない
            gz.write(buf.getvalue())
    print(f"made: {out}  ({os.path.getsize(out)//1024} KB, {len(entries)} items)")
    for p in sorted(entries):
        print("  ", p)


if __name__ == "__main__":
    sys.exit(main())
