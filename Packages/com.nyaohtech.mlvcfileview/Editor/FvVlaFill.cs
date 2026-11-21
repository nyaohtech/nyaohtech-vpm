// MLVC を通さず VLA が直接取り込んだ項目を、.vla/imports.json から自分で補う。
// 名前などは imports.json、画像は VLA の library_data.json の情報から取って .mlvc/thumbs に縮小して置く。
// 展開先フォルダは、取り込み日時のあとに増えた Assets 直下のフォルダから推定する（あくまで推定）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Nyaohtech.MLVCFileView
{
    [Serializable] class ImpItem { public string id = "", name = "", author = "", url = ""; }
    [Serializable] class ImpRec { public string imported_at = ""; public ImpItem item = new ImpItem(); }
    [Serializable] class ImpFile { public ImpRec[] imports = new ImpRec[0]; }

    [Serializable] class LibItem { public string item_id = "", item_name = "", thumbnail_url = "", custom_thumbnail_path = ""; }
    [Serializable] class LibFile { public LibItem[] items = new LibItem[0]; }

    [Serializable] class BoothImg { public string original = "", resized = ""; }
    [Serializable] class BoothItem { public BoothImg[] images = new BoothImg[0]; }

    [Serializable] class FillRec
    {
        public string key = "", id = "", name = "", author = "", url = "", imported_at = "";
        public string[] roots = new string[0];
    }
    [Serializable] class FillFile { public FillRec[] items = new FillRec[0]; }

    internal static class FvVlaFill
    {
        const int ThumbSide = 256;
        const int MaxSrcBytes = 8 * 1024 * 1024;

        static List<FillRec> fills = new List<FillRec>();
        static bool loaded;
        static long stImp = -1, stAssets = -1, stJson = -1;
        static double lastCheck;

        static readonly HashSet<string> tried = new HashSet<string>();  // この起動中に試した画像
        static readonly Queue<string> jobs = new Queue<string>();
        static string curKey;
        static UnityWebRequest req;
        static int stage;  // 0=画像の取得 1=BOOTH 商品ページ(JSON)の取得
        static Dictionary<string, LibItem> lib;
        static long libStamp = -1;

        static string ImpPath => Path.Combine(FvPaths.ProjectRoot, ".vla", "imports.json");
        static string SaveFile => Path.Combine(FvPaths.ProjectRoot, "Library", "MLVCFileView", "vla_fill.json");
        static string ThumbDir => Path.Combine(FvLink.Dir, "thumbs");

        [InitializeOnLoadMethod]
        static void Init()
        {
            FvPaths.Init();
            EditorApplication.update += Tick;
        }

        // MLVC のキー規則（ファイル名に使えない文字を _ にする）に合わせる
        static string KeyOf(string id, string name)
        {
            string k = id.Length > 0 ? id : name;
            foreach (char c in Path.GetInvalidFileNameChars()) k = k.Replace(c, '_');
            return string.IsNullOrWhiteSpace(k) ? "item" : k;
        }

        static long Stamp(string p)
        {
            try { return File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : -1; } catch { return -1; }
        }

        static long AssetsStamp()
        {
            try { return Directory.GetLastWriteTimeUtc(Path.Combine(FvPaths.ProjectRoot, "Assets")).Ticks; } catch { return -1; }
        }

        // ───────── FvLink.Refresh から呼ばれる ─────────
        public static void Merge(List<FvLinkItem> items, Dictionary<string, FvLinkItem> byRoot)
        {
            Load();
            var have = new HashSet<string>(items.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var f in fills)
            {
                if (have.Contains(f.key)) continue;
                DateTime at = DateTime.MinValue;
                if (f.imported_at.Length > 0 && DateTime.TryParse(f.imported_at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    at = t.ToLocalTime();
                var it = new FvLinkItem { Key = f.key, Id = f.id, Name = f.name, Author = f.author, Url = f.url, ImportedAt = at, Roots = f.roots };
                items.Add(it);
                foreach (var r in it.Roots) if (!byRoot.ContainsKey(r)) byRoot[r] = it;
            }
            // 画像が後から置かれたものは拾う
            foreach (var it in items)
                if (string.IsNullOrEmpty(it.ThumbRel) && File.Exists(Path.Combine(ThumbDir, it.Key + ".jpg")))
                    it.ThumbRel = "thumbs/" + it.Key + ".jpg";
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (File.Exists(SaveFile))
                    fills = (JsonUtility.FromJson<FillFile>(File.ReadAllText(SaveFile))?.items ?? new FillRec[0]).ToList();
            }
            catch { fills = new List<FillRec>(); }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SaveFile));
                File.WriteAllText(SaveFile, JsonUtility.ToJson(new FillFile { items = fills.ToArray() }));
            }
            catch { }
        }

        // ───────── 定期チェック ─────────
        static void Tick()
        {
            Pump();
            double now = EditorApplication.timeSinceStartup;
            if (now - lastCheck < 2.0) return;
            lastCheck = now;

            long imp = Stamp(ImpPath), assets = AssetsStamp(), json = Stamp(FvLink.JsonPath);
            if (imp == stImp && assets == stAssets && json == stJson) return;
            stImp = imp; stAssets = assets; stJson = json;
            if (imp < 0) return;
            try { Rebuild(); }
            catch (Exception e) { Debug.LogWarning("[MLVCFileView] VLA の取り込み記録の読み込みに失敗: " + e.Message); }
        }

        static void Rebuild()
        {
            Load();
            FvLink.Refresh(true);  // fileview.json 側の最新を取る（補う対象を決めるため）
            var jsonKeys = FvLink.JsonKeys;

            ImpFile imp;
            using (var fs = new FileStream(ImpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                imp = JsonUtility.FromJson<ImpFile>(sr.ReadToEnd());
            if (imp?.imports == null) return;

            // 項目ごとに一番新しい記録を使う
            var recs = new List<(DateTime at, ImpRec r)>();
            foreach (var r in imp.imports)
            {
                if (r?.item == null) continue;
                if (!DateTime.TryParse(r.imported_at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) continue;
                recs.Add((t.ToLocalTime(), r));
            }
            recs.Sort((a, b) => a.at.CompareTo(b.at));
            var latest = new Dictionary<string, (DateTime at, ImpRec r)>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in recs) latest[KeyOf(x.r.item.id, x.r.item.name)] = x;

            var dirs = TopDirs();
            var taken = new HashSet<string>(FvLink.JsonRoots, StringComparer.OrdinalIgnoreCase);
            var list = new List<FillRec>();
            foreach (var kv in latest.OrderBy(k => k.Value.at))
            {
                if (jsonKeys.Contains(kv.Key)) continue;
                var (at, r) = kv.Value;
                var f = new FillRec
                {
                    key = kv.Key, id = r.item.id, name = r.item.name, author = r.item.author, url = r.item.url,
                    imported_at = at.ToUniversalTime().ToString("o")
                };
                f.roots = GuessRoots(at, r.item, recs, dirs, taken);
                foreach (var d in f.roots) taken.Add(d);
                list.Add(f);
            }
            fills = list;
            Save();

            // 画像が無いもの（MLVC 側の項目も含む）を取りに行く
            var all = list.Select(f => f.key).Concat(FvLink.JsonKeys).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string k in all)
                if (!File.Exists(Path.Combine(ThumbDir, k + ".jpg")) && !tried.Contains(k) && !jobs.Contains(k) && latest.ContainsKey(k))
                    jobs.Enqueue(k);
            lastRecs = latest;
            FvLink.Invalidate();
        }

        static Dictionary<string, (DateTime at, ImpRec r)> lastRecs = new Dictionary<string, (DateTime, ImpRec)>();

        // ───────── 展開先フォルダの推定 ─────────
        sealed class Dir { public string Path, Name; public DateTime Born, Written; }

        static List<Dir> TopDirs()
        {
            var l = new List<Dir>();
            try
            {
                foreach (var d in new DirectoryInfo(Path.Combine(FvPaths.ProjectRoot, "Assets")).EnumerateDirectories())
                {
                    if (d.Name.StartsWith(".")) continue;
                    l.Add(new Dir { Path = "Assets/" + d.Name, Name = d.Name, Born = d.CreationTime, Written = d.LastWriteTime });
                }
            }
            catch { }
            return l;
        }

        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // 取り込み日時のあと（次の取り込みまで）に作られた・更新された Assets 直下のフォルダを候補にする。
        // 同時に複数入れたときは、フォルダ名と項目名・作者名が似ているものだけ割り当てる
        static string[] GuessRoots(DateTime at, ImpItem item, List<(DateTime at, ImpRec r)> recs, List<Dir> dirs, HashSet<string> taken)
        {
            DateTime end = at.AddHours(3);
            foreach (var x in recs) if (x.at > at.AddSeconds(5) && x.at < end) { end = x.at; break; }
            DateTime start = at.AddSeconds(-10);

            var cands = dirs.Where(d => !taken.Contains(d.Path) && d.Name != "Editor" &&
                ((d.Born >= start && d.Born < end) || (d.Written >= start && d.Written < end))).ToList();
            if (cands.Count == 0) return new string[0];

            string nm = Norm(item.name), au = Norm(item.author);
            var like = cands.Where(d =>
            {
                string n = Norm(d.Name);
                if (n.Length < 3) return false;
                return nm.Contains(n) || (au.Length >= 3 && (au.Contains(n) || n.Contains(au))) || (nm.Length >= 3 && n.Contains(nm));
            }).ToList();

            // 同時に複数入れたときは、名前が似たものだけ。単独なら似たものがあればそれ、無ければ新しく増えたもの全部
            bool group = recs.Count(x => Math.Abs((x.at - at).TotalSeconds) <= 5) > 1;
            if (group || like.Count > 0) return like.Select(d => d.Path).ToArray();
            return cands.Select(d => d.Path).ToArray();
        }

        // ───────── 画像の取得（1 件ずつ、毎フレーム少しずつ） ─────────
        static void Pump()
        {
            if (req != null)
            {
                if (!req.isDone) return;
                bool ok = req.result == UnityWebRequest.Result.Success;
                byte[] data = ok ? req.downloadHandler.data : null;
                string text = ok && stage == 1 ? req.downloadHandler.text : null;
                req.Dispose(); req = null;
                if (stage == 1)
                {
                    // 商品ページから 1 枚目の画像 URL を得て、続けて画像を取りに行く
                    string img = null;
                    try { var b = JsonUtility.FromJson<BoothItem>(text ?? ""); if (b?.images != null && b.images.Length > 0) img = b.images[0].original.Length > 0 ? b.images[0].original : b.images[0].resized; }
                    catch { }
                    if (!string.IsNullOrEmpty(img)) { StartGet(img, 0); return; }
                    Finish(curKey, null);
                    return;
                }
                Finish(curKey, data);
                return;
            }
            if (jobs.Count == 0) return;

            curKey = jobs.Dequeue();
            tried.Add(curKey);
            if (!lastRecs.TryGetValue(curKey, out var x)) return;

            // まず VLA 自身が持っている画像（商品 ID のファイル）を使う。ライブラリに無い商品でもここにある
            byte[] bytes = null;
            string own = VlaOwnThumb(x.r.item.id);
            if (own != null) bytes = ReadSmall(own);

            var li = bytes == null ? FindLib(x.r.item) : null;
            if (li != null)
            {
                if (li.custom_thumbnail_path.Length > 0 && File.Exists(li.custom_thumbnail_path)) bytes = ReadSmall(li.custom_thumbnail_path);
                if (bytes == null && li.thumbnail_url.Length > 0)
                {
                    string c = MlvcCachePath(li.thumbnail_url);
                    if (File.Exists(c)) bytes = ReadSmall(c);
                    else if (li.thumbnail_url.StartsWith("http"))
                    {
                        StartGet(li.thumbnail_url, 0);
                        return;
                    }
                }
            }
            // VLA のライブラリに無い商品（直接取り込みなど）は、BOOTH の商品ページから画像を探す
            if (bytes == null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(x.r.item.url ?? "", @"^(https://[\w.-]*booth\.pm/[^?#]*/items/\d+)");
                if (m.Success) { StartGet(m.Groups[1].Value + ".json", 1); return; }
            }
            Finish(curKey, bytes);
        }

        static void StartGet(string url, int st)
        {
            stage = st;
            req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Referer", "https://booth.pm/");
            req.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            req.timeout = 15;
            req.SendWebRequest();
        }

        static string VlaOwnThumb(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return null;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRC-LibraryAssist", "thumbnails");
                if (!Directory.Exists(dir)) return null;
                string best = null;
                foreach (string f in Directory.GetFiles(dir, id + "*"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (n.Equals(id + "_custom", StringComparison.OrdinalIgnoreCase)) return f;
                    if (n.Equals(id, StringComparison.OrdinalIgnoreCase) && best == null) best = f;
                }
                return best;
            }
            catch { return null; }
        }

        static byte[] ReadSmall(string p)
        {
            try { var fi = new FileInfo(p); return fi.Length <= MaxSrcBytes ? File.ReadAllBytes(p) : null; } catch { return null; }
        }

        static string MlvcCachePath(string url)
        {
            string key;
            using (var sha = SHA1.Create())
                key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MLVC", "ThumbCache", key + ".jpg");
        }

        static LibItem FindLib(ImpItem it)
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRC-LibraryAssist", "library_data.json");
            long st = Stamp(p);
            if (lib == null || st != libStamp)
            {
                libStamp = st;
                lib = new Dictionary<string, LibItem>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var f = JsonUtility.FromJson<LibFile>(File.ReadAllText(p));
                    foreach (var l in f?.items ?? new LibItem[0])
                    {
                        if (l.item_id.Length > 0) lib[l.item_id] = l;
                        else if (l.item_name.Length > 0) lib["name:" + l.item_name] = l;
                    }
                }
                catch { }
            }
            if (it.id.Length > 0 && lib.TryGetValue(it.id, out var a)) return a;
            return lib.TryGetValue("name:" + it.name, out var b) ? b : null;
        }

        // 受け取った画像を縮小して .mlvc/thumbs/<key>.jpg に置く
        static void Finish(string key, byte[] data)
        {
            if (data == null || data.Length < 32) return;
            Texture2D src = null, small = null;
            RenderTexture rt = null;
            var prev = RenderTexture.active;
            try
            {
                src = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                if (!ImageConversion.LoadImage(src, data, false)) return;

                int w = src.width, h = src.height;
                float s = Mathf.Min(1f, ThumbSide / (float)Mathf.Max(w, h));
                int tw = Mathf.Max(1, Mathf.RoundToInt(w * s)), th = Mathf.Max(1, Mathf.RoundToInt(h * s));
                rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                small = new Texture2D(tw, th, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                small.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                small.Apply();

                Directory.CreateDirectory(ThumbDir);
                string dest = Path.Combine(ThumbDir, key + ".jpg"), tmp = dest + ".tmp";
                File.WriteAllBytes(tmp, small.EncodeToJPG(82));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                FvLink.Invalidate();
            }
            catch (Exception e) { Debug.LogWarning("[MLVCFileView] 画像の保存に失敗: " + e.Message); }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (src != null) UnityEngine.Object.DestroyImmediate(src);
                if (small != null) UnityEngine.Object.DestroyImmediate(small);
            }
        }
    }
}
