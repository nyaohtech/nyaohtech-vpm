"""VCC を使わない人向けに、まとめ版の .unitypackage を作る。

  python scripts/make_unitypackage.py        # dist/Nt_AvatorSupports_v<バージョン>.unitypackage

- チェッカーと軽量化の 2 つを Assets/nyaohtech/ に入れる。
- VCC 版(VPM パッケージ)と同じプロジェクトに入っても衝突しないよう、
  GUID と asmdef の名前を VCC 版とは別にし、VCC 版が入っているときは、こちらを無効にする。
- 同じ内容から、いつも同じファイルができる(日時・GUID を固定している)。
"""
import gzip
import hashlib
import io
import json
import os
import re
import sys
import tarfile

BASE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PKG_ROOT = os.path.join(BASE, "Packages")
DIST = os.path.join(BASE, "dist")

# (VPM のパッケージ名, Assets 内のフォルダ名, 衝突を避けるための define)
TOOLS = [
    ("com.nyaohtech.avatorperformancechecker", "Nt_AvatorPerformanceChecker", "NT_VPM_AVATORPERFORMANCECHECKER"),
    ("com.nyaohtech.simplematresize", "Nt_SimpleMatResize", "NT_VPM_SIMPLEMATRESIZE"),
]
ROOT = "Assets/nyaohtech"
NAME = "Nt_AvatorSupports"


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


def asmdef_for_unitypackage(raw, pkg_name, define):
    d = json.loads(raw.decode("utf-8-sig"))
    d["name"] = d["name"] + ".Unitypackage"          # VCC 版のアセンブリ名と重ならないように
    d["versionDefines"] = [{"name": pkg_name, "expression": "", "define": define}]
    d["defineConstraints"] = ["!" + define]          # VCC 版が入っているときは、こちらをコンパイルしない
    return (json.dumps(d, indent=4, ensure_ascii=False) + "\n").encode("utf-8")


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

    for pkg, folder, define in TOOLS:
        src = os.path.join(PKG_ROOT, pkg, "Editor")
        for name in sorted(os.listdir(src)):
            if name.endswith(".meta"):
                continue
            raw = read_text(os.path.join(src, name))
            dest = f"{ROOT}/{folder}/Editor/{name}"
            add_dirs(dest)
            if name.endswith(".asmdef"):
                entries[dest] = ("asmdef", asmdef_for_unitypackage(raw, pkg, define))
            elif name.endswith(".cs"):
                entries[dest] = ("cs", raw)
            else:
                entries[dest] = ("other", raw)

    for fname, src in (("README.txt", os.path.join(BASE, "scripts", "unitypackage_files", "README.txt")),
                       ("CHANGELOG.txt", os.path.join(BASE, "CHANGELOG.md"))):   # 更新履歴は、リポジトリの CHANGELOG.md と同じもの
        dest = f"{ROOT}/{fname}"
        add_dirs(dest)
        entries[dest] = ("text", read_text(src))
    entries[ROOT] = ("folder", None)

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
