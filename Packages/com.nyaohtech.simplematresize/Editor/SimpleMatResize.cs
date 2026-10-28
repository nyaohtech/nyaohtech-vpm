// SimpleMatResize v1.1
// アバターを複製し、テクスチャを半分(上限=選択サイズ)に縮めた別PNG/マテリアルに差し替えて
// 新しいプレファブとして保存する。元アバターは非表示にするだけで一切書き換えない。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimpleMatResize
{
    public static class SimpleMatResizeMenu
    {
        const int MinSize = 32;
        const string OutRoot = "Assets/SimpleMatResize/Generated";
        const string Menu = "nyaohtech/アバター軽量化/";
        const string Tag = "[Nt_SimpleMatResize] ";

        [MenuItem(Menu + "高 (1024)", false, 100)] static void High() => Run(1024);
        [MenuItem(Menu + "中 (2048)", false, 101)] static void Mid() => Run(2048);
        [MenuItem(Menu + "低 (4096)", false, 102)] static void Low() => Run(4096);

        [MenuItem(Menu + "高 (1024)", true)]
        [MenuItem(Menu + "中 (2048)", true)]
        [MenuItem(Menu + "低 (4096)", true)]
        static bool HasSelection() => Selection.activeGameObject != null;

        static void Run(int max)
        {
            var root = Selection.activeGameObject;
            try { Process(root, max); }
            catch (Exception e) { Debug.LogError(Tag + "エラーが発生しました: " + e); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static void Process(GameObject root, int max)
        {
            EnsureFolder(OutRoot); // 親が無いと GenerateUniqueAssetPath が空文字を返すため先に作る
            string dir = AssetDatabase.GenerateUniqueAssetPath($"{OutRoot}/{Safe(root.name)}_{max}");
            string texDir = dir + "/Textures", matDir = dir + "/Materials";
            EnsureFolder(texDir);
            EnsureFolder(matDir);
            var texMap = new Dictionary<Texture, Texture>();
            var matMap = new Dictionary<Material, Material>();

            // 差し替えは複製側だけ。元アバターのアセットは書き換えない
            var clone = Object.Instantiate(root, root.transform.parent, false);
            clone.name = root.name + "_Resized" + max;
            clone.SetActive(true);
            Undo.RegisterCreatedObjectUndo(clone, "SimpleMatResize");

            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                EditorUtility.DisplayProgressBar("SimpleMatResize", "処理中: " + r.name, (float)i / renderers.Length);
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null) continue;
                    var small = ShrinkMaterial(mats[m], max, texDir, matDir, texMap, matMap);
                    if (small == mats[m]) continue;
                    mats[m] = small;
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string prefab = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{Safe(clone.name)}.prefab");
            PrefabUtility.SaveAsPrefabAssetAndConnect(clone, prefab, InteractionMode.UserAction);

            Undo.RecordObject(root, "SimpleMatResize Hide Original");
            root.SetActive(false);

            Selection.activeGameObject = clone;
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(prefab));
            Debug.Log($"{Tag}完了: テクスチャ {texMap.Count} 枚 / マテリアル {matMap.Count} 個 を生成 -> {dir}");
        }

        static Material ShrinkMaterial(Material src, int max, string texDir, string matDir,
            Dictionary<Texture, Texture> texMap, Dictionary<Material, Material> matMap)
        {
            if (matMap.TryGetValue(src, out var done)) return done;

            var copy = new Material(src) { name = src.name + "_Resized" };
            bool changed = false;
            foreach (var prop in src.GetTexturePropertyNames())
            {
                var tex = src.GetTexture(prop);
                if (tex == null) continue;
                var small = ShrinkTexture(tex, max, texDir, texMap);
                if (small == tex) continue;
                copy.SetTexture(prop, small);
                changed = true;
            }

            // 縮小対象が無いマテリアルは元を使い、無駄なアセットを作らない
            if (!changed)
            {
                Object.DestroyImmediate(copy);
                return matMap[src] = src;
            }
            AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath($"{matDir}/{Safe(copy.name)}.mat"));
            return matMap[src] = copy;
        }

        static Texture ShrinkTexture(Texture src, int max, string texDir, Dictionary<Texture, Texture> cache)
        {
            if (cache.TryGetValue(src, out var done)) return done;
            cache[src] = src; // 対象外・失敗は元のまま使う。成功時だけ末尾で差し替える

            // RenderTexture や組み込みテクスチャはImporterが無いので対象外
            string srcPath = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(srcPath) || !(AssetImporter.GetAtPath(srcPath) is TextureImporter srcImp)) return src;

            int cur = Mathf.Max(src.width, src.height);
            int target = TargetSize(cur, max);
            if (target >= cur) return src;
            float k = (float)target / cur;
            int w = Mathf.Max(1, Mathf.RoundToInt(src.width * k));
            int h = Mathf.Max(1, Mathf.RoundToInt(src.height * k));

            // 元アセットの設定を変えないよう、コピー側だけ無圧縮・Readableにして生ピクセルを読む
            string tmp = AssetDatabase.GenerateUniqueAssetPath(texDir + "/__smr_tmp" + Path.GetExtension(srcPath));
            Texture2D dst = null;
            try
            {
                if (!AssetDatabase.CopyAsset(srcPath, tmp))
                {
                    Debug.LogWarning(Tag + "コピー失敗のためスキップ: " + srcPath);
                    return src;
                }
                AssetDatabase.ImportAsset(tmp, ImportAssetOptions.ForceSynchronousImport);
                var tmpImp = (TextureImporter)AssetImporter.GetAtPath(tmp);
                tmpImp.textureType = TextureImporterType.Default; // ノーマルマップ変換を外して生データで読む
                tmpImp.isReadable = true;
                tmpImp.mipmapEnabled = false;
                tmpImp.alphaIsTransparency = false;
                tmpImp.npotScale = TextureImporterNPOTScale.None;
                tmpImp.maxTextureSize = 16384;
                tmpImp.textureCompression = TextureImporterCompression.Uncompressed;
                tmpImp.SaveAndReimport();

                var raw = AssetDatabase.LoadAssetAtPath<Texture2D>(tmp);
                dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
                dst.SetPixels32(Downsample(raw.GetPixels32(), raw.width, raw.height, w, h));
                dst.Apply();

                string outPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{texDir}/{Safe(Path.GetFileNameWithoutExtension(srcPath))}_{target}.png");
                File.WriteAllBytes(outPath, dst.EncodeToPNG());
                AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);

                // sRGB・ノーマルマップ等の設定が変わると見た目が崩れるので元から引き継ぐ
                if (AssetImporter.GetAtPath(outPath) is TextureImporter outImp)
                {
                    var settings = new TextureImporterSettings();
                    srcImp.ReadTextureSettings(settings);
                    settings.readable = false;
                    outImp.SetTextureSettings(settings);
                    var platform = srcImp.GetDefaultPlatformTextureSettings();
                    platform.maxTextureSize = target;
                    outImp.SetPlatformTextureSettings(platform);
                    outImp.maxTextureSize = target;
                    outImp.SaveAndReimport();
                }

                var loaded = AssetDatabase.LoadAssetAtPath<Texture>(outPath);
                if (loaded != null) cache[src] = loaded;
            }
            finally
            {
                if (dst != null) Object.DestroyImmediate(dst);
                AssetDatabase.DeleteAsset(tmp);
            }
            return cache[src];
        }

        // 生ピクセルの面積平均。色空間をいじらないので元の色味が崩れない
        static Color32[] Downsample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            float kx = (float)sw / dw, ky = (float)sh / dh;
            for (int y = 0; y < dh; y++)
            {
                int y0 = Mathf.FloorToInt(y * ky), y1 = Mathf.Min(sh, Mathf.CeilToInt((y + 1) * ky));
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Mathf.FloorToInt(x * kx), x1 = Mathf.Min(sw, Mathf.CeilToInt((x + 1) * kx));
                    long r = 0, g = 0, b = 0, a = 0;
                    for (int sy = y0; sy < y1; sy++)
                        for (int sx = x0; sx < x1; sx++)
                        {
                            var c = src[sy * sw + sx];
                            r += c.r; g += c.g; b += c.b; a += c.a;
                        }
                    int n = Mathf.Max(1, (x1 - x0) * (y1 - y0));
                    dst[y * dw + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }
            return dst;
        }

        // 2のべき乗に丸めた現在サイズの半分。上限は選択サイズ、下限は32
        static int TargetSize(int cur, int max) =>
            Mathf.Max(MinSize, Mathf.Min(Mathf.NextPowerOfTwo(cur) / 2, max));

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static string Safe(string name) => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }
}
#endif
