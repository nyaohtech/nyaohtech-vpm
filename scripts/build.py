"""VPM リスティング (index.json) と各パッケージの zip を生成する。

使い方:
  python scripts/build.py                      # 全パッケージの現行バージョンを dist/ に zip 化し Website/index.json を生成
  python scripts/build.py --previous URL_OR_FILE   # 公開済み index.json の旧バージョン項目を引き継ぐ

zip の URL は GitHub Releases（タグ名 = <パッケージ名>-<バージョン>）を指す。
"""
import argparse
import hashlib
import json
import os
import sys
import urllib.request
import zipfile

BASE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PKG_ROOT = os.path.join(BASE, "Packages")
DIST = os.path.join(BASE, "dist")
SITE = os.path.join(BASE, "Website")


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def make_zip(pkg_dir, zip_path):
    """パッケージのルート直下に package.json が来る zip を作る（再現性のため日時を固定）。"""
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for dp, dns, fns in os.walk(pkg_dir):
            dns.sort()
            for name in sorted(fns):
                full = os.path.join(dp, name)
                arc = os.path.relpath(full, pkg_dir).replace(os.sep, "/")
                info = zipfile.ZipInfo(arc, date_time=(2020, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                with open(full, "rb") as f:
                    z.writestr(info, f.read())


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_previous(src):
    if not src:
        return {}
    try:
        if src.startswith("http"):
            with urllib.request.urlopen(src, timeout=30) as r:
                return json.loads(r.read().decode("utf-8")).get("packages", {})
        return load_json(src).get("packages", {})
    except Exception as ex:  # 初回公開時など、取得できなければ空で続行
        print(f"[warn] previous index not loaded: {ex}", file=sys.stderr)
        return {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--previous", default="")
    args = ap.parse_args()

    src = load_json(os.path.join(BASE, "source.json"))
    repo = src["repo"]
    packages = load_previous(args.previous)

    os.makedirs(DIST, exist_ok=True)
    os.makedirs(SITE, exist_ok=True)

    for name in sorted(os.listdir(PKG_ROOT)):
        pdir = os.path.join(PKG_ROOT, name)
        pj = os.path.join(pdir, "package.json")
        if not os.path.isfile(pj):
            continue
        manifest = load_json(pj)
        ver = manifest["version"]
        zip_name = f"{manifest['name']}-{ver}.zip"
        zip_path = os.path.join(DIST, zip_name)
        make_zip(pdir, zip_path)

        entry = dict(manifest)
        entry["url"] = f"https://github.com/{repo}/releases/download/{manifest['name']}-{ver}/{zip_name}"
        entry["zipSHA256"] = sha256(zip_path)

        node = packages.setdefault(manifest["name"], {"versions": {}})
        node["versions"][ver] = entry
        print(f"built {zip_name}  sha256={entry['zipSHA256'][:12]}…")

    listing = {
        "name": src["name"],
        "id": src["id"],
        "author": src["author"],
        "url": f"{src['pagesUrl']}/index.json",
        "packages": packages,
    }
    with open(os.path.join(SITE, "index.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(listing, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("wrote Website/index.json")


if __name__ == "__main__":
    main()
