"""Packages/ 以下の全ファイル・フォルダに、決定的な GUID の .meta を作る（既存はスキップ）。"""
import hashlib, os, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Packages")

def guid(rel):
    return hashlib.md5(("nyaohtech-vpm/" + rel).encode("utf-8")).hexdigest()

def meta_for(path, rel):
    g = guid(rel)
    if os.path.isdir(path):
        return f"fileFormatVersion: 2\nguid: {g}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    ext = os.path.splitext(path)[1].lower()
    if ext == ".cs":
        return (f"fileFormatVersion: 2\nguid: {g}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n"
                "  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
    if ext == ".asmdef":
        return f"fileFormatVersion: 2\nguid: {g}\nAssemblyDefinitionImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    if os.path.basename(path) == "package.json":
        return f"fileFormatVersion: 2\nguid: {g}\nPackageManifestImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
    return f"fileFormatVersion: 2\nguid: {g}\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

def main():
    made = 0
    for pkg in sorted(os.listdir(ROOT)):
        pdir = os.path.join(ROOT, pkg)
        if not os.path.isdir(pdir):
            continue
        for dp, dns, fns in os.walk(pdir):
            for name in dns + fns:
                if name.endswith(".meta"):
                    continue
                p = os.path.join(dp, name)
                m = p + ".meta"
                if os.path.exists(m):
                    continue
                rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
                with open(m, "w", encoding="utf-8", newline="\n") as f:
                    f.write(meta_for(p, rel))
                made += 1
    print(f"meta created: {made}")

if __name__ == "__main__":
    sys.exit(main())
