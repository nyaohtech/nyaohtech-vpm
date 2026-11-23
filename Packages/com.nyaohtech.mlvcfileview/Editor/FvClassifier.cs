// プレハブの役割判定（アバター / 衣装 / 小物 / ギミック）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Nyaohtech.MLVCFileView
{
    [Serializable]
    internal sealed class FvRoleRec
    {
        public string path = "";
        public long len;
        public long mtime;
        public int flags;
        public string[] sources = new string[0];  // m_SourcePrefab の GUID
    }

    [Serializable]
    internal sealed class FvRoleCacheJson { public int ver; public bool ma; public List<FvRoleRec> recs = new List<FvRoleRec>(); }

    internal static class FvClassifier
    {
        const int CacheVer = 2;  // 判定ロジックを変えたら上げる（古い解析結果を捨てる）
        const int FDesc = 1, FArmature = 2, FProp = 4, FGimmick = 8;

        static readonly string[] DescNames = { "VRCAvatarDescriptor", "PipelineManager" };  // どちらかが付いていればアバター
        static readonly string[] ArmatureNames = { "ModularAvatarMergeArmature" };
        static readonly string[] PropNames = { "ModularAvatarBoneProxy", "ModularAvatarVisibleHeadAccessory" };
        static readonly string[] GimmickNames =
        {
            "ModularAvatarMergeAnimator", "ModularAvatarMenuInstaller", "ModularAvatarParameters",
            "ModularAvatarObjectToggle", "ModularAvatarMenuItem", "ModularAvatarShapeChanger",
            "ModularAvatarMaterialSetter", "ModularAvatarMaterialSwap"
        };

        // Modular Avatar が入っているか。衣装・小物・ギミックの判定は、この部品に頼っている
        static bool? _hasMa;
        public static bool HasModularAvatar
        {
            get
            {
                if (_hasMa.HasValue) return _hasMa.Value;
                bool found = false;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!a.GetName().Name.StartsWith("nadena.dev.modular-avatar", StringComparison.OrdinalIgnoreCase)) continue;
                    found = true; break;
                }
                if (!found) found = Directory.Exists("Packages/nadena.dev.modular-avatar");
                _hasMa = found;
                return found;
            }
        }

        static Dictionary<string, int> _guidFlags;  // "スクリプト GUID/fileID" → フラグ
        static readonly Dictionary<string, FvRoleRec> Cache = new Dictionary<string, FvRoleRec>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, FvRole> Result = new Dictionary<string, FvRole>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> Pending = new Queue<string>();
        static bool _cacheLoaded, _dirty;

        public static int Total { get; private set; }
        public static int Done { get; private set; }
        public static bool Running => Pending.Count > 0;
        public static int Version { get; private set; }

        static string CacheFile => Path.Combine(FvPaths.ProjectRoot, "Library", "MLVCFileView", "roles.json");

        public static FvRole RoleOf(string path) => Result.TryGetValue(path, out var r) ? r : FvRole.None;
        public static bool HasResult(string path) => Result.ContainsKey(path);

        // ───────── スクリプト GUID の解決 ─────────
        static void ResolveGuids()
        {
            _guidFlags = new Dictionary<string, int>();
            Add(DescNames, FDesc); Add(ArmatureNames, FArmature); Add(PropNames, FProp); Add(GimmickNames, FGimmick);
        }

        static void Add(string[] names, int flag)
        {
            foreach (var n in names)
            {
                // ① ソース(.cs)で入っているクラス: YAML の m_Script は fileID 11500000
                foreach (var g in AssetDatabase.FindAssets(n + " t:MonoScript"))
                {
                    var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(g));
                    var cls = ms != null ? ms.GetClass() : null;
                    if (cls != null && cls.Name == n) _guidFlags[g + "/11500000"] = flag;
                }
                foreach (var t in TypeCache.GetTypesDerivedFrom<UnityEngine.MonoBehaviour>())
                {
                    if (t.Name != n || t.IsAbstract) continue;
                    var key = KeyOfType(t);
                    if (key != null) _guidFlags[key] = flag;
                }
            }
        }

        static string KeyOfType(Type t)
        {
            UnityEngine.GameObject go = null;
            try
            {
                go = EditorUtility.CreateGameObjectWithHideFlags("MLVCFileView_tmp", UnityEngine.HideFlags.HideAndDontSave);
                var mb = go.AddComponent(t) as UnityEngine.MonoBehaviour;
                var ms = mb != null ? MonoScript.FromMonoBehaviour(mb) : null;
                if (ms != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ms, out string guid, out long local))
                    return guid + "/" + local;
            }
            catch { }
            finally { if (go != null) UnityEngine.Object.DestroyImmediate(go); }
            return null;
        }

        // ───────── 実行 ─────────
        public static void Begin(List<string> prefabPaths)
        {
            LoadCache();
            if (_guidFlags == null || _guidFlags.Count == 0) ResolveGuids();
            Pending.Clear();
            Total = prefabPaths.Count;
            Done = 0;
            foreach (var p in prefabPaths)
            {
                if (Cache.TryGetValue(p, out var rec) && Fresh(p, rec)) { Result[p] = Compute(p, 0); Done++; }
                else Pending.Enqueue(p);
            }
            Version++;
        }

        // 1 フレームの持ち時間 budgetMs の範囲で進める。変化があれば true。
        public static bool Tick(int budgetMs)
        {
            if (Pending.Count == 0) return false;
            var sw = Stopwatch.StartNew();
            while (Pending.Count > 0 && sw.ElapsedMilliseconds < budgetMs)
            {
                string p = Pending.Dequeue();
                Result[p] = Compute(p, 0);
                Done++;
            }
            Version++;
            if (Pending.Count == 0) SaveCache();
            return true;
        }

        static bool Fresh(string path, FvRoleRec rec)
        {
            try
            {
                var fi = new FileInfo(FvPaths.Abs(path));
                return fi.Exists && fi.Length == rec.len && fi.LastWriteTimeUtc.Ticks == rec.mtime;
            }
            catch { return false; }
        }

        static FvRole Compute(string path, int depth)
        {
            int flags = FlagsOf(path, depth);
            if ((flags & FDesc) != 0) return FvRole.Avatar;
            if ((flags & FArmature) != 0) return FvRole.Costume;
            if ((flags & FProp) != 0) return FvRole.Prop;
            if ((flags & FGimmick) != 0) return FvRole.Gimmick;
            return FvRole.None;
        }

        static int FlagsOf(string path, int depth)
        {
            if (depth > 6) return 0;
            if (!Cache.TryGetValue(path, out var rec) || !Fresh(path, rec))
            {
                rec = Scan(path);
                Cache[path] = rec;
                _dirty = true;
            }
            int flags = rec.flags;
            foreach (var g in rec.sources)
            {
                string sp = AssetDatabase.GUIDToAssetPath(g);
                if (sp.Length > 0 && sp.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !sp.Equals(path, StringComparison.OrdinalIgnoreCase))
                    flags |= FlagsOf(sp, depth + 1);
            }
            return flags;
        }

        const string ScriptKey = "m_Script: {fileID: ";
        const string SourceKey = "m_SourcePrefab: {fileID: 100100000, guid: ";

        static FvRoleRec Scan(string path)
        {
            var rec = new FvRoleRec { path = path };
            try
            {
                var fi = new FileInfo(FvPaths.Abs(path));
                if (!fi.Exists) return rec;
                rec.len = fi.Length;
                rec.mtime = fi.LastWriteTimeUtc.Ticks;
                var src = new HashSet<string>();
                using (var sr = new StreamReader(fi.FullName))
                {
                    string first = sr.ReadLine();
                    if (first == null || !first.StartsWith("%YAML")) return rec;  // バイナリ保存は対象外
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        int i = line.IndexOf(ScriptKey, StringComparison.Ordinal);
                        if (i >= 0)
                        {
                            int a = i + ScriptKey.Length;
                            int comma = line.IndexOf(',', a);
                            int gi = line.IndexOf("guid: ", a, StringComparison.Ordinal);
                            if (comma > a && gi > comma)
                            {
                                string g = Guid32(line, gi + 6);
                                if (g != null && _guidFlags.TryGetValue(g + "/" + line.Substring(a, comma - a), out var f)) rec.flags |= f;
                            }
                            continue;
                        }
                        i = line.IndexOf(SourceKey, StringComparison.Ordinal);
                        if (i >= 0)
                        {
                            string g = Guid32(line, i + SourceKey.Length);
                            if (g != null) src.Add(g);
                        }
                    }
                }
                rec.sources = new string[src.Count];
                src.CopyTo(rec.sources);
            }
            catch (Exception ex) { Debug.LogWarning("[MLVCFileView] 解析に失敗: " + path + " " + ex.Message); }
            return rec;
        }

        static string Guid32(string s, int start) => start + 32 <= s.Length ? s.Substring(start, 32) : null;

        // ───────── 保存 ─────────
        static void LoadCache()
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;
            try
            {
                if (!File.Exists(CacheFile)) return;
                var j = JsonUtility_FromJson(File.ReadAllText(CacheFile));
                // 保存時と Modular Avatar の有無が変わっていたら（あとから入れた・外した）、解析結果を捨てて引き直す
                if (j != null && j.ver == CacheVer && j.ma == HasModularAvatar) foreach (var r in j.recs) Cache[r.path] = r;
            }
            catch { }
        }

        static FvRoleCacheJson JsonUtility_FromJson(string s) => UnityEngine.JsonUtility.FromJson<FvRoleCacheJson>(s);

        static void SaveCache()
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
                var j = new FvRoleCacheJson { ver = CacheVer, ma = HasModularAvatar };
                // 実在するものだけを残す（削除済みプレハブの記録が溜まらないように）
                foreach (var kv in Cache) if (File.Exists(FvPaths.Abs(kv.Key))) j.recs.Add(kv.Value);
                File.WriteAllText(CacheFile, UnityEngine.JsonUtility.ToJson(j));
            }
            catch { }
        }

        // プラグインの追加インストール直後など、GUID を引き直したいとき。
        public static void ResetGuids() { _guidFlags = null; Cache.Clear(); Result.Clear(); _dirty = true; }
    }
}
