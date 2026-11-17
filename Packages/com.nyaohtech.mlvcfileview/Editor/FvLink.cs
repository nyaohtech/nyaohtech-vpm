// MLVC / VLA 連携データ（/.mlvc/fileview.json）の読み取りとサムネイルのキャッシュ
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nyaohtech.MLVCFileView
{
    [Serializable]
    internal sealed class FvLinkItemJson
    {
        public string key = "", id = "", name = "", author = "", url = "", imported_at = "", thumb = "";
        public string[] roots = new string[0];
    }

    [Serializable]
    internal sealed class FvLinkJson
    {
        public int schema;
        public FvLinkItemJson[] items = new FvLinkItemJson[0];
    }

    public sealed class FvLinkItem
    {
        public string Key = "", Id = "", Name = "", Author = "", Url = "", ThumbRel = "";
        public DateTime ImportedAt;  // ローカル時刻
        public string[] Roots = new string[0];  // "Assets/Foo"
    }

    internal static class FvLink
    {
        public const int SupportedSchema = 1;
        public const int MaxCount = 96;  // 同時に保持するサムネイル枚数の上限
        public const long MaxFileBytes = 400 * 1024;
        public const int MaxPixels = 512;

        static List<FvLinkItem> _items = new List<FvLinkItem>();
        static Dictionary<string, FvLinkItem> _byRoot = new Dictionary<string, FvLinkItem>(StringComparer.OrdinalIgnoreCase);
        static long _stamp = -1;
        static double _lastCheck;

        // データが読み直されるたびに増える。画面側が再描画の要否を判断するのに使う。
        public static int Version { get; private set; }

        public static string Dir => Path.Combine(FvPaths.ProjectRoot, ".mlvc");
        public static string JsonPath => Path.Combine(Dir, "fileview.json");
        public static bool Available => File.Exists(JsonPath) || _items.Count > 0;

        // fileview.json にある項目のキーと展開先（VLA 直取り込みを補う側が、重複を避けるために使う）
        public static HashSet<string> JsonKeys { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static HashSet<string> JsonRoots { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 次の Refresh で必ず読み直す
        public static void Invalidate() { _stamp = long.MinValue; _lastCheck = 0; }

        public static IReadOnlyList<FvLinkItem> Items { get { Refresh(false); return _items; } }

        public static FvLinkItem ItemForRoot(string root)
        {
            Refresh(false);
            return !string.IsNullOrEmpty(root) && _byRoot.TryGetValue(root, out var it) ? it : null;
        }

        // ファイルの更新日時が変わっていたら読み直す。force=false のときは 1.5 秒に 1 回だけ確認する。
        public static void Refresh(bool force)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && now - _lastCheck < 1.5) return;
            _lastCheck = now;

            long stamp = -1;
            try { if (File.Exists(JsonPath)) stamp = File.GetLastWriteTimeUtc(JsonPath).Ticks; } catch { }
            if (!force && stamp == _stamp) return;
            _stamp = stamp;

            var items = new List<FvLinkItem>();
            var byRoot = new Dictionary<string, FvLinkItem>(StringComparer.OrdinalIgnoreCase);
            if (stamp >= 0)
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(JsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                    var j = JsonUtility.FromJson<FvLinkJson>(text);
                    if (j != null && j.schema == SupportedSchema && j.items != null)
                    {
                        foreach (var it in j.items)
                        {
                            DateTime at = DateTime.MinValue;
                            if (!string.IsNullOrEmpty(it.imported_at) &&
                                DateTime.TryParse(it.imported_at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                                at = t.ToLocalTime();
                            var item = new FvLinkItem
                            {
                                Key = it.key, Id = it.id, Name = it.name, Author = it.author, Url = it.url,
                                ThumbRel = it.thumb, ImportedAt = at, Roots = it.roots ?? new string[0]
                            };
                            items.Add(item);
                            foreach (var r in item.Roots) if (!string.IsNullOrEmpty(r)) byRoot[r] = item;
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[MLVCFileView] fileview.json を読めませんでした: " + ex.Message); }
            }
            JsonKeys = new HashSet<string>(items.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
            JsonRoots = new HashSet<string>(byRoot.Keys, StringComparer.OrdinalIgnoreCase);
            FvVlaFill.Merge(items, byRoot);
            items.Sort((a, b) => b.ImportedAt.CompareTo(a.ImportedAt));
            _items = items;
            _byRoot = byRoot;
            Version++;
            ClearThumbs();  // 画像が差し替わった可能性があるので破棄（次に見えた分だけ読み直す）
        }

        // ───────── サムネイル LRU ─────────
        sealed class Entry { public string Key; public Texture2D Tex; }
        static readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        static readonly Dictionary<string, LinkedListNode<Entry>> _map = new Dictionary<string, LinkedListNode<Entry>>();
        static readonly HashSet<string> _bad = new HashSet<string>();

        public static int CachedCount => _lru.Count;

        // サムネイルを返す。無い・読めない・大きすぎる場合は null（呼び出し側はアイコン表示にする）。
        public static Texture2D GetThumb(FvLinkItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.ThumbRel)) return null;
            string key = item.Key;
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddFirst(node);
                if (node.Value.Tex != null) return node.Value.Tex;
                _lru.Remove(node); _map.Remove(key);  // 破棄済みなら読み直し
            }
            if (_bad.Contains(key)) return null;

            Texture2D tex = null;
            try
            {
                string full = Path.GetFullPath(Path.Combine(Dir, item.ThumbRel));
                // .mlvc の外は読まない（fileview.json が壊れていても任意のファイルを読まないため）
                if (!full.StartsWith(Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase)) { _bad.Add(key); return null; }
                var fi = new FileInfo(full);
                if (!fi.Exists || fi.Length > MaxFileBytes) { _bad.Add(key); return null; }

                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(full), true) || tex.width > MaxPixels || tex.height > MaxPixels)
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    _bad.Add(key);
                    return null;
                }
            }
            catch
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                _bad.Add(key);
                return null;
            }

            var n = _lru.AddFirst(new Entry { Key = key, Tex = tex });
            _map[key] = n;
            while (_lru.Count > MaxCount)
            {
                var last = _lru.Last;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                if (last.Value.Tex != null) UnityEngine.Object.DestroyImmediate(last.Value.Tex);
            }
            return tex;
        }

        public static void ClearThumbs()
        {
            foreach (var e in _lru) if (e.Tex != null) UnityEngine.Object.DestroyImmediate(e.Tex);
            _lru.Clear();
            _map.Clear();
            _bad.Clear();
        }

        [InitializeOnLoadMethod]
        static void Hook()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ClearThumbs;
            EditorApplication.quitting += ClearThumbs;
        }
    }
}
