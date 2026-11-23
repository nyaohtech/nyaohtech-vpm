"""BOOTH 配布用の、Nt_MLVCFileView の導入用 .unitypackage を作る。

  python scripts/make_fileview_unitypackage.py   # dist/Nt_MLVCFileView_Setup_v<バージョン>.unitypackage

- 本体は入れない。取り込むと、案内ウィンドウが最新版を VPM リポジトリから取ってきて Packages に入れる。
- 案内ウィンドウは Assets/nyaohtech/Nt_MLVCFileView_Setup に入る。
- バージョンは scripts/unitypackage_fv.json。
"""
import json
import os
import sys

from make_unitypackage import BASE, DIST, read_text, write_package

ROOT = "Assets/nyaohtech/Nt_MLVCFileView_Setup"
NAME = "Nt_MLVCFileView_Setup"
FILES = os.path.join(BASE, "scripts", "unitypackage_files_fv")


def main():
    version = json.load(open(os.path.join(BASE, "scripts", "unitypackage_fv.json"), encoding="utf-8"))["version"]
    entries = {}

    asmdef = {"name": "Nyaohtech.FvSetup.Editor", "rootNamespace": "Nyaohtech.FvSetup", "includePlatforms": ["Editor"]}
    src = [
        (f"{ROOT}/Editor/FvSetup.cs", "cs", read_text(os.path.join(FILES, "FvSetup.cs"))),
        (f"{ROOT}/Editor/Nyaohtech.FvSetup.Editor.asmdef", "asmdef", (json.dumps(asmdef, indent=4) + chr(10)).encode("utf-8")),
        (f"{ROOT}/README.txt", "text", read_text(os.path.join(FILES, "README.txt"))),
    ]
    for dest, kind, data in src:
        parts = dest.split("/")
        for i in range(2, len(parts)):
            entries.setdefault("/".join(parts[:i]), ("folder", None))
        entries[dest] = (kind, data)
    for d in ("Assets/nyaohtech", ROOT):
        entries[d] = ("folder", None)

    out = os.path.join(DIST, f"{NAME}_v{version}.unitypackage")
    write_package(entries, out)
    print(f"made: {out}  ({os.path.getsize(out)//1024} KB, {len(entries)} items)")
    for p in sorted(entries):
        print("  ", p)


if __name__ == "__main__":
    sys.exit(main())
