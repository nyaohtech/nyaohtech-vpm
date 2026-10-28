// AvatorPerformanceChecker v1.0 (by nyaohtech) - based on VAP v3.3
// VRChatアバターのパフォーマンスランクをSceneビューのオーバーレイとダッシュボードで確認する。
// 集計はアップロード相当: 非アクティブな子孫と、EditorOnlyタグ付きは配下ごと除外する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AnimatedValues;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace AvatorPerformanceChecker
{
    public enum PerfRank { Excellent, Good, Medium, Poor, VeryPoor }
    public enum TargetPlatform { PC, Mobile }

    // ---------------------------------------------------------------- 表示文字列

    public static class L
    {
        public static bool IsJP => Prefs.Language == 0;
        public static string T(string ja, string en) => IsJP ? ja : en;

        static readonly string[] RankIcon = { "★", "★", "○", "⚠", "✖" };
        static readonly string[] RankEn = { "Excellent", "Good", "Medium", "Poor", "Very Poor" };
        static readonly string[] RankJa = { "非常に良い", "良い", "普通", "重い", "非常に重い" };

        public static string RankName(PerfRank r) => RankEn[(int)r];
        public static string RankLabel(PerfRank r)
        {
            int i = (int)r;
            return IsJP ? $"{RankIcon[i]} {RankEn[i]}（{RankJa[i]}）" : $"{RankIcon[i]} {RankEn[i]}";
        }

        public static string Triangles          => T("ポリゴン数", "Triangles");
        public static string BoundsSize         => T("バウンズサイズ", "Bounds Size");
        public static string TextureMemory      => T("テクスチャメモリ", "Texture Memory");
        public static string SkinnedMeshes      => T("スキンメッシュ数", "Skinned Meshes");
        public static string BasicMeshes        => T("メッシュ数", "Basic Meshes");
        public static string MaterialSlots      => T("マテリアルスロット", "Material Slots");
        public static string PhysBoneComps      => T("PhysBoneコンポーネント", "PhysBone Components");
        public static string PhysBoneTransforms => T("PhysBone対象Transform", "PhysBone Transforms");
        public static string PhysBoneColliders  => T("PhysBoneコライダー", "PhysBone Colliders");
        public static string Contacts           => T("コンタクト", "Contacts");
        public static string Constraints        => T("コンストレイント", "Constraints");
        public static string Animators          => T("Animator数", "Animators");
        public static string Bones              => T("ボーン数", "Bones");
        public static string Lights             => T("ライト", "Lights");
        public static string ParticleSystems    => T("パーティクルシステム", "Particle Systems");
        public static string TotalParticles     => T("総パーティクル数", "Total Particles Active");
        public static string MeshParticlePolys  => T("メッシュパーティクルポリ", "Mesh Particle Polys");
        public static string ParticleTrails     => T("パーティクルトレイル", "Particle Trails");
        public static string ParticleCollision  => T("パーティクル衝突", "Particle Collision");
        public static string TrailRenderers     => T("トレイルレンダラー", "Trail Renderers");
        public static string LineRenderers      => T("ラインレンダラー", "Line Renderers");
        public static string AudioSources       => T("オーディオソース", "Audio Sources");
        public static string Cloths             => T("クロス", "Cloths");
        public static string ClothVertices      => T("クロス頂点数", "Cloth Vertices");
        public static string PhysicsColliders   => T("物理コライダー", "Physics Colliders");
        public static string PhysicsRigidbodies => T("物理リジッドボディ", "Physics Rigidbodies");
        public static string AvatarTotalSize    => T("アバター推定総容量", "Est. Avatar Total Size");
        public static string MeshVRAM           => T("メッシュVRAM", "Mesh VRAM");
        public static string TotalVRAM          => T("総VRAM（テクスチャ+メッシュ）", "Total VRAM (Tex+Mesh)");
        public static string Compressed         => T("圧縮", "Compressed");
        public static string Uncompressed       => T("非圧縮", "Uncompressed");

        public static string DashboardTitle => T("AvatorPerformanceChecker ダッシュボード", "AvatorPerformanceChecker Dashboard");
        public static string AvatarRoot     => T("アバタールート", "Avatar Root");
        public static string AutoDetect     => T("自動検出", "Auto-Detect");
        public static string Refresh        => T("更新", "Refresh");
        public static string ProblemsOnly   => T("問題のみ表示", "Problems only");
        public static string SelectAvatar   => T("アバタールートを選択してください。", "Select an avatar root.");
        public static string SelectAvatarHint => T(
            "アバタールート（VRCAvatarDescriptor付き・アクティブ）を選択するか、自動検出をクリックしてください。\n※ 非アクティブ / EditorOnly タグのオブジェクトは集計されません。",
            "Select an active avatar root (with VRCAvatarDescriptor) or click Auto-Detect.\nInactive / EditorOnly-tagged objects are excluded from all counts.");
        public static string PerfRankBanner(string platform, PerfRank r) =>
            T($"{platform} パフォーマンスランク:  {RankLabel(r)}", $"{platform} Performance Rank:  {RankLabel(r)}");
        public static string MobileBlockingWarn => T(
            "Mobileデフォルトブロック: Medium\nPoorアバターはデフォルトで非表示。Very Poorは「アバターを表示」が必要。\n将来Very PoorのMobileアバターが削除される可能性があります。",
            "Mobile default blocking: Medium.\nPoor avatars hidden by default. Very Poor requires 'Show Avatar'.\nVRChat may remove Very Poor mobile avatars in future.");
        public static string FileSizeWarn => T(
            "⚠ アバター推定容量がVRChat PC版の上限に近いか超えています。\n圧縮後上限: 200MB / 非圧縮上限: 500MB\nアップロードに失敗する可能性があります。テクスチャ解像度の見直しを推奨します。",
            "⚠ Estimated avatar size is near or exceeds VRChat PC limits.\nCompressed limit: 200MB / Uncompressed limit: 500MB\nUpload may fail. Consider reducing texture resolutions.");
        public static string FileSizeOK => T(
            "✔ 推定容量はVRChat PC版の上限（圧縮200MB/非圧縮500MB）以内です。",
            "✔ Estimated size is within VRChat PC limits (compressed 200MB / uncompressed 500MB).");

        public static string ColMetric => T("項目", "Metric");
        public static string ColValue  => T("現在値", "Value");
        public static string ColRank   => T("ランク", "Rank");
        public static string ColLimits => T("制限値 ( Excellent / Good / Medium / Poor )", "Limits ( Excellent / Good / Medium / Poor )");

        public static string VRAMTitle     => T("VRAM テクスチャレポート", "VRAM Texture Report");
        public static string ColTexture    => T("テクスチャ", "Texture");
        public static string ColResolution => T("解像度", "Resolution");
        public static string ColFormat     => T("形式", "Format");
        public static string ColPath       => T("パス", "Path");

        public static string NoAvatarOverlay => T("アクティブなアバターなし", "No active avatar");
        public static string TexUnit         => T("枚", " tex");

        public static string SettingsTitle    => T("AvatorPerformanceChecker 設定", "AvatorPerformanceChecker Settings");
        public static string Language         => T("言語 / Language", "Language");
        public static string Platform         => T("対象プラットフォーム", "Target Platform");
        public static string PCLimitsInfo     => T("PC制限値を使用。デフォルトブロック: Very Poor", "PC limits active. Default blocking: Very Poor.");
        public static string MobileLimitsInfo => T(
            "Mobile制限値を使用。デフォルトブロック: Medium\nVery Poorアバターは将来削除される可能性があります。",
            "Mobile limits active. Default blocking: Medium.\nVery Poor avatars may be removed in future.");
        public static string SceneOverlay     => T("Sceneビューオーバーレイ", "Scene View Overlay");
        public static string SceneOverlayHint => T(
            "Unity標準のオーバーレイとして動作します。\n表示のON/OFFはSceneビューのOverlaysメニュー（`キー / 右上メニュー）から\n「AvatorPerformanceChecker」を切り替えてください。",
            "Runs as a standard Unity Overlay.\nToggle it from the Scene view Overlays menu ( ` key / top-right menu )\nunder \"AvatorPerformanceChecker\".");
        public static string HierarchyOverlay => T("ヒエラルキーオーバーレイ", "Hierarchy Overlay");
        public static string EnableOverlay    => T("オーバーレイ有効", "Enable Overlay");
        public static string ShowTriangles    => T("ポリゴン数を表示 (Δ)", "Triangles (Δ)");
        public static string ShowMaterials    => T("マテリアル数を表示 (M)", "Materials (M)");
        public static string ShowVRAM         => T("VRAM表示 (V)", "VRAM (V)");
        public static string ShowCompIcons    => T("コンポーネントアイコン", "Component Icons");
        public static string IncludeChildren  => T("子オブジェクトを含む", "Include Children");
        public static string CacheSection     => T("キャッシュ", "Cache");
        public static string CacheLifetime    => T("有効期間（秒）", "Lifetime (sec)");
        public static string ForceRefresh     => T("強制更新", "Force Refresh");
        public static string About => T(
            "AvatorPerformanceChecker v1.0 (by nyaohtech)\n\nVRChat公式ドキュメント準拠のパフォーマンスチェッカー。\nSceneビューオーバーレイ + PC/Mobile全項目 + VRAMレポート。\n集計対象: アクティブかつ非EditorOnlyのオブジェクトのみ（アップロード相当）。\n日本語/英語切替。",
            "AvatorPerformanceChecker v1.0 (by nyaohtech)\n\nOfficial VRChat thresholds (PC + Mobile).\nScene view overlay + full metrics + VRAM report.\nCounts only active, non-EditorOnly objects (upload-equivalent).\nJP/EN.");
    }

    // ---------------------------------------------------------------- 設定 (EditorPrefs)

    static class Prefs
    {
        const string K = "APC_";
        public static int Language           { get => EditorPrefs.GetInt(K + "Lang", 0);        set => EditorPrefs.SetInt(K + "Lang", value); }
        public static int PlatformIndex      { get => EditorPrefs.GetInt(K + "Plat", 0);        set => EditorPrefs.SetInt(K + "Plat", value); }
        public static float CacheLifetime    { get => EditorPrefs.GetFloat(K + "Cache", 2f);    set => EditorPrefs.SetFloat(K + "Cache", value); }
        public static bool EnableOverlay     { get => EditorPrefs.GetBool(K + "Over", true);    set => EditorPrefs.SetBool(K + "Over", value); }
        public static bool IncludeChildren   { get => EditorPrefs.GetBool(K + "Child", true);   set => EditorPrefs.SetBool(K + "Child", value); }
        public static bool OverlayCompact    { get => EditorPrefs.GetBool(K + "OvCompact", false); set => EditorPrefs.SetBool(K + "OvCompact", value); }
        // ヒエラルキーの表示項目は重くなるので既定ではすべてOFF
        public static bool ShowTriangles     { get => EditorPrefs.GetBool(K + "Tri", false);    set => EditorPrefs.SetBool(K + "Tri", value); }
        public static bool ShowMaterials     { get => EditorPrefs.GetBool(K + "Mat", false);    set => EditorPrefs.SetBool(K + "Mat", value); }
        public static bool ShowVRAM          { get => EditorPrefs.GetBool(K + "VRAM", false);   set => EditorPrefs.SetBool(K + "VRAM", value); }
        public static bool ShowCompIcons     { get => EditorPrefs.GetBool(K + "Comp", false);   set => EditorPrefs.SetBool(K + "Comp", value); }
        public static TargetPlatform Platform => PlatformIndex == 0 ? TargetPlatform.PC : TargetPlatform.Mobile;
    }

    // ---------------------------------------------------------------- 制限値 (VRChat公式)

    // 4要素は Excellent / Good / Medium / Poor の上限。null の項目はそのプラットフォームでは評価しない
    class Limits
    {
        public int[] Tris, Skinned, Basic, Slots, PbComps, PbTransforms, PbColliders, Contacts, Constraints,
            Animators, Bones, Lights, Particles, ParticleTotal, MeshParticlePolys, TrailRend, LineRend,
            Audio, Cloths, ClothVerts, Colliders, Bodies;
        public float[] BoundsX, BoundsY, BoundsZ, TexMB;
        public bool[] ParticleTrails, ParticleCollision;
    }

    static class Thresholds
    {
        public static readonly Limits Pc = new Limits
        {
            Tris = new[] { 32000, 70000, 70000, 70000 },
            BoundsX = new[] { 2.5f, 4f, 5f, 5f }, BoundsY = new[] { 2.5f, 4f, 6f, 6f }, BoundsZ = new[] { 2.5f, 4f, 5f, 5f },
            TexMB = new[] { 40f, 75f, 110f, 150f },
            Skinned = new[] { 1, 2, 8, 16 }, Basic = new[] { 4, 8, 16, 24 }, Slots = new[] { 4, 8, 16, 32 },
            PbComps = new[] { 4, 8, 16, 32 }, PbTransforms = new[] { 16, 64, 128, 256 }, PbColliders = new[] { 4, 8, 16, 32 },
            Contacts = new[] { 8, 16, 24, 32 }, Constraints = new[] { 100, 250, 300, 350 },
            Animators = new[] { 1, 4, 16, 32 }, Bones = new[] { 75, 150, 256, 400 }, Lights = new[] { 0, 0, 0, 1 },
            Particles = new[] { 0, 4, 8, 16 }, ParticleTotal = new[] { 0, 300, 1000, 2500 }, MeshParticlePolys = new[] { 0, 1000, 2000, 5000 },
            ParticleTrails = new[] { false, false, true, true }, ParticleCollision = new[] { false, false, true, true },
            TrailRend = new[] { 1, 2, 4, 8 }, LineRend = new[] { 1, 2, 4, 8 }, Audio = new[] { 1, 4, 8, 8 },
            Cloths = new[] { 0, 1, 1, 1 }, ClothVerts = new[] { 0, 50, 100, 200 },
            Colliders = new[] { 0, 1, 8, 8 }, Bodies = new[] { 0, 1, 8, 8 },
        };

        public static readonly Limits Mobile = new Limits
        {
            Tris = new[] { 7500, 10000, 15000, 20000 },
            BoundsX = Pc.BoundsX, BoundsY = Pc.BoundsY, BoundsZ = Pc.BoundsZ,
            TexMB = new[] { 10f, 18f, 25f, 40f },
            Skinned = new[] { 1, 1, 2, 2 }, Basic = new[] { 1, 1, 2, 2 }, Slots = new[] { 1, 1, 2, 4 },
            PbComps = new[] { 0, 4, 6, 8 }, PbTransforms = new[] { 0, 16, 32, 64 }, PbColliders = new[] { 0, 4, 8, 16 },
            Contacts = new[] { 2, 4, 8, 16 }, Constraints = new[] { 30, 60, 120, 150 },
            Animators = new[] { 1, 1, 1, 2 }, Bones = new[] { 75, 90, 150, 150 },
            Particles = new[] { 0, 0, 0, 2 }, ParticleTotal = new[] { 0, 0, 0, 200 }, MeshParticlePolys = new[] { 0, 0, 0, 400 },
            ParticleTrails = new[] { false, false, false, true }, ParticleCollision = new[] { false, false, false, true },
            TrailRend = new[] { 0, 0, 0, 1 }, LineRend = new[] { 0, 0, 0, 1 },
        };

        public static Limits Current => Prefs.Platform == TargetPlatform.PC ? Pc : Mobile;

        // アップロードサイズの上限 (PC)
        public const float CompressedLimitMB = 200f;
        public const float UncompressedLimitMB = 500f;

        public static PerfRank Classify<T>(T v, T[] lim) where T : IComparable<T>
        {
            for (int i = 0; i < 4; i++)
                if (v.CompareTo(lim[i]) <= 0) return (PerfRank)i;
            return PerfRank.VeryPoor;
        }

        public static PerfRank ClassifyFlag(bool on, bool[] lim)
        {
            if (!on) return PerfRank.Excellent;
            for (int i = 0; i < 4; i++)
                if (lim[i]) return (PerfRank)i;
            return PerfRank.VeryPoor;
        }

        // 絶対上限に対する割合で判定する (アップロード容量用)
        public static PerfRank ClassifyRatio(float v, float limit)
        {
            float r = limit > 0 ? v / limit : 0;
            return r <= 0.5f ? PerfRank.Excellent : r <= 0.75f ? PerfRank.Good : r <= 0.9f ? PerfRank.Medium
                : r <= 1f ? PerfRank.Poor : PerfRank.VeryPoor;
        }
    }

    static class Palette
    {
        static readonly string[] hex = { "4DD964", "999999", "F2D926", "FF9926", "FF4033" };
        static readonly Color[] tint =
        {
            new Color(.30f, .85f, .39f), new Color(.70f, .85f, .35f), new Color(.95f, .85f, .15f),
            new Color(1f, .60f, .15f), new Color(1f, .25f, .20f),
        };
        public static string Hex(PerfRank r) => hex[(int)r];
        public static Color Tint(PerfRank r) => tint[(int)r];
    }

    static class Fmt
    {
        public static string Short(int v) => v >= 1000000 ? $"{v / 1000000f:F1}M" : v >= 1000 ? $"{v / 1000f:F1}K" : v.ToString("N0");
        public static string Full(int v) => v >= 1000000 ? $"{v / 1000000f:F2}M" : v >= 1000 ? v.ToString("N0") : v.ToString();
    }

    // ---------------------------------------------------------------- データ

    class AvatarStats
    {
        public string Name;
        public int Triangles;
        public Vector3 BoundsSize;
        public float TextureMemoryMB, TotalVRAMMB, CompressedSizeMB, UncompressedSizeMB;
        public long MeshVRAMBytes;
        public int TextureCount;
        public int SkinnedMeshes, BasicMeshes, MaterialSlots;
        public int PhysBoneComponents, PhysBoneTransforms, PhysBoneColliders;
        public int Contacts, ConstraintCount, Animators, Bones, Lights;
        public int ParticleSystems, TotalParticlesActive, MeshParticlePolys;
        public bool ParticleTrailsEnabled, ParticleCollisionEnabled;
        public int TrailRenderers, LineRenderers, AudioSources;
        public int Cloths, ClothVertices, PhysicsColliders, PhysicsRigidbodies;
        public List<MetricResult> PCResults, MobileResults;
        public PerfRank OverallPC, OverallMobile;
        public readonly List<TextureEntry> Textures = new List<TextureEntry>();
    }

    struct MetricResult { public string Label, Value, Limits; public PerfRank Rank; }
    struct TextureEntry { public string Name, Path, Format; public int Width, Height; public long VRAMBytes; public Texture Ref; }

    class ObjectInfo
    {
        public int SelfTris, TotalTris, TotalMats;
        public long SelfVramBytes;
        public bool HasPhysBone, HasConstraint, HasLight, HasParticles, HasTrail, HasAudio, HasCloth;
        public PerfRank TriRank, MatRank, VramRank;
    }

    // 解析結果は設定した秒数だけ使い回す (ヒエラルキーの描画ごとに再計算しないため)
    static class StatsCache
    {
        static readonly Dictionary<int, ObjectInfo> objects = new Dictionary<int, ObjectInfo>();
        static readonly Dictionary<int, AvatarStats> avatars = new Dictionary<int, AvatarStats>();
        static double last;
        static bool dirty = true;

        public static void MarkDirty() => dirty = true;
        public static void Clear() { objects.Clear(); avatars.Clear(); dirty = true; last = 0; }

        static void Expire()
        {
            double now = EditorApplication.timeSinceStartup;
            if (!dirty && now - last <= Prefs.CacheLifetime) return;
            objects.Clear();
            avatars.Clear();
            dirty = false;
            last = now;
        }

        public static ObjectInfo Object(GameObject go)
        {
            if (!go) return null;
            Expire();
            int id = go.GetInstanceID();
            if (!objects.TryGetValue(id, out var info)) objects[id] = info = Analyzer.Object(go);
            return info;
        }

        public static AvatarStats Avatar(GameObject root)
        {
            if (!root) return null;
            Expire();
            int id = root.GetInstanceID();
            if (!avatars.TryGetValue(id, out var stats)) avatars[id] = stats = Analyzer.Avatar(root);
            return stats;
        }
    }

    // ---------------------------------------------------------------- 解析

    static class Analyzer
    {
        const float MB = 1024f * 1024f;

        // ルートと、アクティブかつ非EditorOnlyの子孫だけを辿る (非該当は配下ごとスキップ)
        public static void ForEachUploaded(GameObject root, Action<GameObject> act)
        {
            if (root) Walk(root.transform, true, act);
        }

        static void Walk(Transform t, bool isRoot, Action<GameObject> act)
        {
            var go = t.gameObject;
            if ((!isRoot && !go.activeSelf) || go.CompareTag("EditorOnly")) return;
            act(go);
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), false, act);
        }

        static int CountUploadedTransforms(Transform t)
        {
            int n = 0;
            Walk(t, true, _ => n++);
            return n;
        }

        // VRC SDKへの参照を持たなくて済むよう、型名で判定する
        public static bool IsAvatar(GameObject go) =>
            go.GetComponents<Component>().Any(c => c && c.GetType().Name == "VRCAvatarDescriptor");

        public static GameObject FindAvatarRoot()
        {
            foreach (var r in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (!r.activeInHierarchy) continue;
                GameObject found = null;
                ForEachUploaded(r, go => { if (!found && IsAvatar(go)) found = go; });
                if (found) return found;
            }
            return null;
        }

        public static ObjectInfo Object(GameObject go)
        {
            var d = new ObjectInfo();
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr && smr.sharedMesh) { d.SelfTris += Tris(smr.sharedMesh); d.TotalMats += smr.sharedMaterials.Length; }
            var mf = go.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh)
            {
                d.SelfTris += Tris(mf.sharedMesh);
                var mr = go.GetComponent<MeshRenderer>();
                if (mr) d.TotalMats += mr.sharedMaterials.Length;
            }
            d.TotalTris = d.SelfTris;

            if (Prefs.IncludeChildren)
            {
                foreach (var s in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (s.gameObject == go || !s.sharedMesh) continue;
                    d.TotalTris += Tris(s.sharedMesh);
                    d.TotalMats += s.sharedMaterials.Length;
                }
                foreach (var m in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var f = m.GetComponent<MeshFilter>();
                    if (m.gameObject == go || !f || !f.sharedMesh) continue;
                    d.TotalTris += Tris(f.sharedMesh);
                    d.TotalMats += m.sharedMaterials.Length;
                }
            }

            d.SelfVramBytes = ObjectVram(go);
            foreach (var c in go.GetComponents<Component>())
            {
                if (!c) continue;
                string tn = c.GetType().Name;
                d.HasPhysBone |= tn == "VRCPhysBone";
                d.HasConstraint |= tn.Contains("Constraint");
                d.HasLight |= c is Light;
                d.HasParticles |= c is ParticleSystem;
                d.HasTrail |= c is TrailRenderer;
                d.HasAudio |= c is AudioSource;
                d.HasCloth |= c is Cloth;
            }

            var lim = Thresholds.Current;
            d.TriRank = Thresholds.Classify(d.TotalTris, lim.Tris);
            d.MatRank = Thresholds.Classify(d.TotalMats, lim.Slots);
            d.VramRank = Thresholds.Classify(d.SelfVramBytes / MB, lim.TexMB);
            return d;
        }

        public static AvatarStats Avatar(GameObject root)
        {
            var s = new AvatarStats { Name = root.name };
            long meshBytes = 0;
            var renderers = new List<Renderer>();
            var bones = new HashSet<Transform>();

            ForEachUploaded(root, go =>
            {
                var smr = go.GetComponent<SkinnedMeshRenderer>();
                if (smr)
                {
                    if (smr.sharedMesh) { s.Triangles += Tris(smr.sharedMesh); meshBytes += MeshBytes(smr.sharedMesh); }
                    s.SkinnedMeshes++;
                    s.MaterialSlots += smr.sharedMaterials.Length;
                    foreach (var b in smr.bones) if (b) bones.Add(b);
                }
                var mr = go.GetComponent<MeshRenderer>();
                if (mr)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf && mf.sharedMesh) { s.Triangles += Tris(mf.sharedMesh); meshBytes += MeshBytes(mf.sharedMesh); }
                    s.BasicMeshes++;
                    s.MaterialSlots += mr.sharedMaterials.Length;
                }
                var rend = go.GetComponent<Renderer>();
                if (rend) renderers.Add(rend);
                if (go.GetComponent<Animator>()) s.Animators++;

                foreach (var c in go.GetComponents<Component>())
                {
                    if (!c) continue;
                    string tn = c.GetType().Name;
                    if (tn == "VRCPhysBone") { s.PhysBoneComponents++; s.PhysBoneTransforms += CountUploadedTransforms(c.transform); }
                    else if (tn == "VRCPhysBoneCollider") s.PhysBoneColliders++;
                    else if (tn == "VRCContactReceiver" || tn == "VRCContactSender") s.Contacts++;
                    if (tn.Contains("Constraint")) s.ConstraintCount++;

                    switch (c)
                    {
                        case Light _: s.Lights++; break;
                        case ParticleSystem ps:
                            s.ParticleSystems++;
                            s.TotalParticlesActive += ps.main.maxParticles;
                            s.ParticleTrailsEnabled |= ps.trails.enabled;
                            s.ParticleCollisionEnabled |= ps.collision.enabled;
                            break;
                        case TrailRenderer _: s.TrailRenderers++; break;
                        case LineRenderer _: s.LineRenderers++; break;
                        case AudioSource _: s.AudioSources++; break;
                        case Cloth cl: s.Cloths++; s.ClothVertices += cl.vertices.Length; break;
                        case Collider _: s.PhysicsColliders++; break;
                        case Rigidbody _: s.PhysicsRigidbodies++; break;
                    }
                }
            });

            s.MeshVRAMBytes = meshBytes;
            s.Bones = bones.Count;
            if (renderers.Count > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                s.BoundsSize = b.size;
            }

            CollectTextures(renderers, s);
            s.TextureCount = s.Textures.Count;
            s.TextureMemoryMB = s.Textures.Sum(t => t.VRAMBytes) / MB;
            float meshMB = meshBytes / MB;
            s.TotalVRAMMB = s.TextureMemoryMB + meshMB;

            // 非圧縮はVRAM相当で近似。圧縮は「テクスチャのファイルサイズ + メッシュの半分」に
            // 20%上乗せしたVAP由来の経験則 (実際のバンドル圧縮後サイズは取得できない)
            s.UncompressedSizeMB = s.TotalVRAMMB;
            float diskMB = s.Textures.Where(t => !string.IsNullOrEmpty(t.Path) && File.Exists(t.Path))
                .Sum(t => new FileInfo(t.Path).Length / MB);
            if (diskMB < .1f) diskMB = s.TextureMemoryMB * .25f;
            s.CompressedSizeMB = (diskMB + meshMB * .5f) * 1.2f;

            s.PCResults = BuildResults(s, Thresholds.Pc);
            s.MobileResults = BuildResults(s, Thresholds.Mobile);
            s.OverallPC = (PerfRank)s.PCResults.Max(r => (int)r.Rank);
            s.OverallMobile = (PerfRank)s.MobileResults.Max(r => (int)r.Rank);
            return s;
        }

        static List<MetricResult> BuildResults(AvatarStats s, Limits l)
        {
            var list = new List<MetricResult>();
            void Count(string label, int v, int[] lim)
            {
                if (lim == null) return;
                list.Add(new MetricResult
                {
                    Label = label, Value = Fmt.Short(v), Rank = Thresholds.Classify(v, lim),
                    Limits = string.Join(" / ", lim.Select(Fmt.Short)),
                });
            }
            void Flag(string label, bool on, bool[] lim) => list.Add(new MetricResult
            {
                Label = label, Value = on ? "True" : "False", Rank = Thresholds.ClassifyFlag(on, lim),
                Limits = string.Join(" / ", lim),
            });

            Count(L.Triangles, s.Triangles, l.Tris);

            var sz = s.BoundsSize;
            var boundsRank = (PerfRank)Mathf.Max((int)Thresholds.Classify(sz.x, l.BoundsX),
                Mathf.Max((int)Thresholds.Classify(sz.y, l.BoundsY), (int)Thresholds.Classify(sz.z, l.BoundsZ)));
            list.Add(new MetricResult
            {
                Label = L.BoundsSize, Value = $"{sz.x:F1} x {sz.y:F1} x {sz.z:F1} m", Rank = boundsRank,
                Limits = string.Join(" / ", Enumerable.Range(0, 4).Select(i => $"{l.BoundsX[i]}x{l.BoundsY[i]}x{l.BoundsZ[i]}")),
            });

            list.Add(new MetricResult
            {
                Label = L.TextureMemory, Value = $"{s.TextureMemoryMB:F1} MB",
                Rank = Thresholds.Classify(s.TextureMemoryMB, l.TexMB), Limits = string.Join(" / ", l.TexMB),
            });

            Count(L.SkinnedMeshes, s.SkinnedMeshes, l.Skinned);
            Count(L.BasicMeshes, s.BasicMeshes, l.Basic);
            Count(L.MaterialSlots, s.MaterialSlots, l.Slots);
            Count(L.PhysBoneComps, s.PhysBoneComponents, l.PbComps);
            Count(L.PhysBoneTransforms, s.PhysBoneTransforms, l.PbTransforms);
            Count(L.PhysBoneColliders, s.PhysBoneColliders, l.PbColliders);
            Count(L.Contacts, s.Contacts, l.Contacts);
            Count(L.Constraints, s.ConstraintCount, l.Constraints);
            Count(L.Animators, s.Animators, l.Animators);
            Count(L.Bones, s.Bones, l.Bones);
            Count(L.Lights, s.Lights, l.Lights);
            Count(L.ParticleSystems, s.ParticleSystems, l.Particles);
            Count(L.TotalParticles, s.TotalParticlesActive, l.ParticleTotal);
            Count(L.MeshParticlePolys, s.MeshParticlePolys, l.MeshParticlePolys);
            Flag(L.ParticleTrails, s.ParticleTrailsEnabled, l.ParticleTrails);
            Flag(L.ParticleCollision, s.ParticleCollisionEnabled, l.ParticleCollision);
            Count(L.TrailRenderers, s.TrailRenderers, l.TrailRend);
            Count(L.LineRenderers, s.LineRenderers, l.LineRend);
            Count(L.AudioSources, s.AudioSources, l.Audio);
            Count(L.Cloths, s.Cloths, l.Cloths);
            Count(L.ClothVertices, s.ClothVertices, l.ClothVerts);
            Count(L.PhysicsColliders, s.PhysicsColliders, l.Colliders);
            Count(L.PhysicsRigidbodies, s.PhysicsRigidbodies, l.Bodies);
            return list;
        }

        public static int Tris(Mesh m)
        {
            int n = 0;
            for (int i = 0; i < m.subMeshCount; i++) n += (int)m.GetSubMesh(i).indexCount;
            return n / 3;
        }

        static long ObjectVram(GameObject go)
        {
            var seen = new HashSet<Texture>();
            foreach (var r in go.GetComponents<Renderer>())
                foreach (var m in r.sharedMaterials) AddMaterialTextures(m, seen);
            long bytes = seen.Sum(TexBytes);
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr && smr.sharedMesh) bytes += MeshBytes(smr.sharedMesh);
            var mf = go.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh) bytes += MeshBytes(mf.sharedMesh);
            return bytes;
        }

        static void CollectTextures(List<Renderer> renderers, AvatarStats stats)
        {
            var seen = new HashSet<Texture>();
            foreach (var r in renderers)
                foreach (var m in r.sharedMaterials) AddMaterialTextures(m, seen);
            foreach (var tex in seen)
            {
                string format = tex switch
                {
                    Texture2D t2 => t2.format.ToString(),
                    RenderTexture rt => rt.format.ToString(),
                    Cubemap _ => "Cubemap",
                    _ => "Unknown",
                };
                stats.Textures.Add(new TextureEntry
                {
                    Name = tex.name, Path = AssetDatabase.GetAssetPath(tex), Width = tex.width, Height = tex.height,
                    Format = format, VRAMBytes = TexBytes(tex), Ref = tex,
                });
            }
            stats.Textures.Sort((a, b) => b.VRAMBytes.CompareTo(a.VRAMBytes));
        }

        // マテリアルのプロパティ定義から辿る。未設定スロットは除外される
        static void AddMaterialTextures(Material m, HashSet<Texture> set)
        {
            if (!m || !m.shader) return;
            int n = ShaderUtil.GetPropertyCount(m.shader);
            for (int i = 0; i < n; i++)
            {
                if (ShaderUtil.GetPropertyType(m.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var t = m.GetTexture(ShaderUtil.GetPropertyName(m.shader, i));
                if (t) set.Add(t);
            }
        }

        // 実際のVRAM使用量は取れないので、フォーマットのbpp×ピクセル数で概算する (ミップ込みで×1.33)
        static long TexBytes(Texture t)
        {
            if (!t) return 0;
            int px = t.width * t.height;
            switch (t)
            {
                case Texture2D t2: return (long)(px * Bpp(t2.format) / 8f * (t2.mipmapCount > 1 ? 1.33f : 1f));
                case RenderTexture rt: return (long)(px * RtBpp(rt.format) / 8f);
                case Cubemap _: return (long)(px * 32f / 8f * 6 * 1.33f);
                default: return (long)(px * 32f / 8f);
            }
        }

        // 頂点バッファの実サイズは取れないため、属性ごとの標準的なバイト数から概算する
        static long MeshBytes(Mesh mesh)
        {
            if (!mesh) return 0;
            int vc = mesh.vertexCount, stride = 48;
            if (mesh.uv2.Length > 0) stride += 8;
            if (mesh.uv3.Length > 0) stride += 8;
            if (mesh.uv4.Length > 0) stride += 8;
            if (mesh.colors.Length > 0) stride += 16;
            if (mesh.boneWeights.Length > 0) stride += 32;
            long bytes = (long)vc * stride;
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetSubMesh(i).indexCount;
            bytes += indices * (mesh.indexFormat == IndexFormat.UInt32 ? 4 : 2);
            if (mesh.blendShapeCount > 0) bytes += (long)vc * 12 * mesh.blendShapeCount;
            return bytes;
        }

        static float Bpp(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.DXT1: case TextureFormat.DXT1Crunched: case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB: case TextureFormat.BC4: return 4f;
                case TextureFormat.DXT5: case TextureFormat.DXT5Crunched: case TextureFormat.ETC2_RGBA8:
                case TextureFormat.BC5: case TextureFormat.BC6H: case TextureFormat.BC7: case TextureFormat.ASTC_4x4:
                case TextureFormat.Alpha8: case TextureFormat.R8: return 8f;
                case TextureFormat.ASTC_5x5: return 5.12f;
                case TextureFormat.ASTC_6x6: return 3.56f;
                case TextureFormat.ASTC_8x8: return 2f;
                case TextureFormat.ASTC_10x10: return 1.28f;
                case TextureFormat.ASTC_12x12: return 0.89f;
                case TextureFormat.R16: case TextureFormat.RG16: case TextureFormat.RGB565: case TextureFormat.RGBA4444: return 16f;
                case TextureFormat.RGB24: return 24f;
                case TextureFormat.RGBAHalf: return 64f;
                case TextureFormat.RGBAFloat: return 128f;
                default: return 32f;
            }
        }

        static float RtBpp(RenderTextureFormat f)
        {
            switch (f)
            {
                case RenderTextureFormat.R8: return 8f;
                case RenderTextureFormat.R16: case RenderTextureFormat.RHalf: return 16f;
                case RenderTextureFormat.ARGBHalf: return 64f;
                case RenderTextureFormat.ARGBFloat: return 128f;
                default: return 32f;
            }
        }
    }

    // ---------------------------------------------------------------- ヒエラルキー表示

    [InitializeOnLoad]
    static class HierarchyOverlay
    {
        static GUIStyle style;

        static HierarchyOverlay()
        {
            EditorApplication.hierarchyWindowItemOnGUI -= Draw;
            EditorApplication.hierarchyWindowItemOnGUI += Draw;
            EditorApplication.hierarchyChanged -= StatsCache.MarkDirty;
            EditorApplication.hierarchyChanged += StatsCache.MarkDirty;
        }

        static void Draw(int id, Rect rect)
        {
            if (!Prefs.EnableOverlay || !(EditorUtility.InstanceIDToObject(id) is GameObject go)) return;
            var d = StatsCache.Object(go);
            var sb = new StringBuilder();
            void Add(string s) { if (sb.Length > 0) sb.Append(' '); sb.Append(s); }

            if (Prefs.ShowTriangles && d.TotalTris > 0)
                Add($"<color=#{Palette.Hex(d.TriRank)}>Δ{Fmt.Short(d.TotalTris)}</color>");
            if (Prefs.ShowMaterials && d.TotalMats > 0)
                Add($"<color=#{Palette.Hex(d.MatRank)}>M{d.TotalMats}</color>");
            if (Prefs.ShowVRAM && d.SelfVramBytes > 0)
            {
                float mb = d.SelfVramBytes / (1024f * 1024f);
                string text = mb >= 1f ? $"{mb:F1}M" : $"{d.SelfVramBytes / 1024f:F0}K";
                Add($"<color=#{Palette.Hex(d.VramRank)}>V{text}</color>");
            }
            if (Prefs.ShowCompIcons)
            {
                string icons = (d.HasPhysBone ? "♦" : "") + (d.HasConstraint ? "⚓" : "") + (d.HasLight ? "☀" : "")
                    + (d.HasParticles ? "✧" : "") + (d.HasTrail ? "~" : "") + (d.HasAudio ? "♫" : "") + (d.HasCloth ? "✂" : "");
                if (icons.Length > 0) Add($"<color=#{Palette.Hex(PerfRank.Medium)}>{icons}</color>");
            }
            if (sb.Length == 0) return;

            style ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight, fontSize = 10, richText = true, padding = new RectOffset(0, 4, 0, 0),
            };
            // 名前と重ならないよう、名前の幅を除いた右側にだけ描く
            float nameW = EditorStyles.label.CalcSize(new GUIContent(go.name)).x + 24;
            float w = Mathf.Max(200, rect.width - nameW);
            GUI.Label(new Rect(rect.xMax - w, rect.y, w, rect.height), sb.ToString(), style);
        }
    }

    // ---------------------------------------------------------------- Sceneビューのオーバーレイ

    [Overlay(typeof(SceneView), OverlayID, "AvatorPerformanceChecker", true)]
    public class APCSceneOverlay : Overlay
    {
        public const string OverlayID = "apc-performance-overlay";

        GUIStyle label, cell, head, foldButton;
        GameObject root;
        double lastFind;
        IMGUIContainer gui;
        AnimBool expanded;

        // Overlaysメニューから消した後に戻すための入口
        [MenuItem("nyaohtech/アバターInfo/オーバーレイを再表示")]
        public static void ShowOverlayMenu()
        {
            if (SceneView.sceneViews.Count == 0) EditorWindow.GetWindow<SceneView>();
            foreach (SceneView sv in SceneView.sceneViews)
                if (sv.TryGetOverlay(OverlayID, out Overlay ov)) { ov.displayed = true; ov.collapsed = false; }
            SceneView.RepaintAll();
        }

        public override void OnCreated() => EditorApplication.hierarchyChanged += OnHierarchyChanged;
        public override void OnWillBeDestroyed() => EditorApplication.hierarchyChanged -= OnHierarchyChanged;

        void OnHierarchyChanged()
        {
            root = null;
            lastFind = 0;
            StatsCache.MarkDirty();
            gui?.MarkDirtyRepaint();
        }

        public override VisualElement CreatePanelContent()
        {
            var ve = new VisualElement { name = "apc-overlay-root" };
            ve.style.minWidth = 260;
            gui = new IMGUIContainer(DrawGUI);
            ve.Add(gui);
            // 値を新鮮に保つ定期再描画。折りたたみアニメ中は AnimBool 側が再描画を促す
            ve.schedule.Execute(() => gui?.MarkDirtyRepaint()).Every(250);
            return ve;
        }

        void InitStyles()
        {
            if (label != null) return;
            label = new GUIStyle(EditorStyles.label)
            {
                richText = true, fontSize = 12, padding = new RectOffset(4, 4, 1, 1), normal = { textColor = Color.white },
            };
            cell = new GUIStyle(label) { fontSize = 11 };
            head = new GUIStyle(label) { fontStyle = FontStyle.Bold };
            foldButton = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 9, fixedWidth = 22, fixedHeight = 16, padding = new RectOffset(0, 0, 1, 1),
            };
        }

        void DrawGUI()
        {
            InitStyles();
            if (expanded == null)
            {
                expanded = new AnimBool(!Prefs.OverlayCompact) { speed = 3.5f };
                expanded.valueChanged.AddListener(() => gui?.MarkDirtyRepaint());
            }

            // 毎フレーム探索すると重いので、キャッシュ有効期間ごとにしかルートを探し直さない
            double now = EditorApplication.timeSinceStartup;
            if (root == null || !root.activeInHierarchy || root.CompareTag("EditorOnly")
                || now - lastFind > Mathf.Max(1f, Prefs.CacheLifetime))
            {
                root = Analyzer.FindAvatarRoot();
                lastFind = now;
            }
            if (root == null) { GUILayout.Label($"<color=#888888>{L.NoAvatarOverlay}</color>", label); return; }

            var s = StatsCache.Avatar(root);
            var overall = Prefs.Platform == TargetPlatform.PC ? s.OverallPC : s.OverallMobile;

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{s.Name}</b>", head);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color=#{Palette.Hex(overall)}><b>{L.RankName(overall)}</b></color>", head);
            GUILayout.Space(4);
            bool open = expanded.target;
            if (GUILayout.Button(open ? "▲" : "▼", foldButton))
            {
                expanded.target = !open;
                Prefs.OverlayCompact = open;
            }
            GUILayout.EndHorizontal();

            // オーバーレイの色分けは常にPC基準
            var pc = Thresholds.Pc;
            var triR = Thresholds.Classify(s.Triangles, pc.Tris);
            var pbR = Thresholds.Classify(s.PhysBoneComponents, pc.PbComps);
            var texR = Thresholds.Classify(s.TextureMemoryMB, pc.TexMB);
            var compR = Thresholds.ClassifyRatio(s.CompressedSizeMB, Thresholds.CompressedLimitMB);
            var uncompR = Thresholds.ClassifyRatio(s.UncompressedSizeMB, Thresholds.UncompressedLimitMB);

            if (EditorGUILayout.BeginFadeGroup(1f - expanded.faded))
            {
                string sep = "<color=#666666>|</color>";
                GUILayout.Label(
                    $"<color=#{Palette.Hex(triR)}>Δ {Fmt.Full(s.Triangles)}</color> {sep} " +
                    $"<color=#{Palette.Hex(pbR)}>PB {s.PhysBoneComponents}</color> {sep} " +
                    $"<color=#{Palette.Hex(texR)}>V {s.TextureMemoryMB:F1}M</color> {sep} " +
                    $"<color=#{Palette.Hex(compR)}>{s.CompressedSizeMB:F1}MB</color>", label);
            }
            EditorGUILayout.EndFadeGroup();

            if (EditorGUILayout.BeginFadeGroup(expanded.faded))
            {
                Sep();
                Row(Cell(L.Triangles, Fmt.Full(s.Triangles), triR),
                    Cell(L.T("マテリアル数", "Materials"), s.MaterialSlots.ToString(), Thresholds.Classify(s.MaterialSlots, pc.Slots)));
                Row(Cell(L.T("スキンメッシュ", "SkinnedMesh"), s.SkinnedMeshes.ToString(), Thresholds.Classify(s.SkinnedMeshes, pc.Skinned)),
                    Cell(L.T("ボーン総数", "Bones"), s.Bones.ToString(), Thresholds.Classify(s.Bones, pc.Bones)));
                Sep();
                Row(Cell("PhysBone", s.PhysBoneComponents.ToString(), pbR),
                    Cell("PB Transform", s.PhysBoneTransforms.ToString(), Thresholds.Classify(s.PhysBoneTransforms, pc.PbTransforms)));
                Row(Cell("Constraint", s.ConstraintCount.ToString(), Thresholds.Classify(s.ConstraintCount, pc.Constraints)),
                    Cell("Animator", s.Animators.ToString(), Thresholds.Classify(s.Animators, pc.Animators)));
                Sep();
                Row(Cell(L.T("テクスチャVRAM", "Texture VRAM"), $"{s.TextureMemoryMB:F1} MB", texR),
                    Cell(L.T("枚数", "Count"), $"{s.TextureCount}{L.TexUnit}"));
                GUILayout.Label(
                    $"<color=#BBBBBB>{L.T("アバター推定容量", "Est. avatar size")}:</color> " +
                    $"<color=#{Palette.Hex(compR)}>{L.T("圧縮", "Comp")} {s.CompressedSizeMB:F1} MB</color> <color=#888888>/</color> " +
                    $"<color=#{Palette.Hex(uncompR)}>{L.T("非圧縮", "Uncomp")} {s.UncompressedSizeMB:F1} MB</color>", cell);

                // ライトとパーティクルは存在する時だけ出す
                if (s.Lights > 0 || s.ParticleSystems > 0)
                {
                    Sep();
                    Row(s.Lights > 0 ? WarnCell("Light", s.Lights, Thresholds.Classify(s.Lights, pc.Lights)) : "",
                        s.ParticleSystems > 0 ? WarnCell("Particle", s.ParticleSystems, Thresholds.Classify(s.ParticleSystems, pc.Particles)) : "");
                }
            }
            EditorGUILayout.EndFadeGroup();
        }

        static string Cell(string name, string value, PerfRank? rank = null) =>
            $"<color=#BBBBBB>{name}:</color> <color=#{(rank.HasValue ? Palette.Hex(rank.Value) : "FFFFFF")}>{value}</color>";

        static string WarnCell(string name, int count, PerfRank rank) =>
            Cell(name, count.ToString(), rank) + (rank >= PerfRank.Poor ? " <color=#FF9926>⚠</color>" : "");

        void Row(string a, string b)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(a, cell, GUILayout.MinWidth(135));
            GUILayout.Label(b, cell, GUILayout.MinWidth(115));
            GUILayout.EndHorizontal();
        }

        static void Sep()
        {
            GUILayout.Space(2);
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)), new Color(1f, 1f, 1f, 0.12f));
            GUILayout.Space(2);
        }
    }

    // ---------------------------------------------------------------- ダッシュボード

    public class APCDashboard : EditorWindow
    {
        Vector2 scroll;
        GameObject avatar;
        AvatarStats stats;
        int tab;
        bool problemsOnly;

        [MenuItem("nyaohtech/アバターInfo/チェッカー表示")]
        public static void ShowWindow() => GetWindow<APCDashboard>("Nt_AvatorPerformanceChecker").minSize = new Vector2(600, 500);

        void OnEnable() => Selection.selectionChanged += Repaint;
        void OnDisable() => Selection.selectionChanged -= Repaint;

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L.DashboardTitle, new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            avatar = EditorGUILayout.ObjectField(L.AvatarRoot, avatar, typeof(GameObject), true) as GameObject;
            if (GUILayout.Button(L.AutoDetect, GUILayout.Width(90))) Detect(true);
            if (GUILayout.Button(L.Refresh, GUILayout.Width(70))) { StatsCache.Clear(); stats = null; }
            EditorGUILayout.EndHorizontal();

            if (!avatar) Detect(false);
            if (!avatar) { EditorGUILayout.HelpBox(L.SelectAvatarHint, MessageType.Info); return; }
            if (stats == null || stats.Name != avatar.name) stats = StatsCache.Avatar(avatar);

            EditorGUILayout.Space(4);
            DrawSizeInfo();
            EditorGUILayout.Space(4);
            DrawVramSummary();
            EditorGUILayout.Space(6);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(tab == 0, "PC", EditorStyles.toolbarButton)) tab = 0;
            if (GUILayout.Toggle(tab == 1, "Mobile", EditorStyles.toolbarButton)) tab = 1;
            GUILayout.FlexibleSpace();
            problemsOnly = GUILayout.Toggle(problemsOnly, L.ProblemsOnly, GUILayout.Width(140));
            EditorGUILayout.EndHorizontal();

            var results = tab == 0 ? stats.PCResults : stats.MobileResults;
            var overall = tab == 0 ? stats.OverallPC : stats.OverallMobile;
            EditorGUILayout.Space(4);
            var bannerStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, fixedHeight = 38,
            };
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = Palette.Tint(overall);
            GUILayout.Label(L.PerfRankBanner(tab == 0 ? "PC" : "Mobile", overall), bannerStyle);
            GUI.backgroundColor = prevBg;
            if (tab == 1 && overall >= PerfRank.Poor) EditorGUILayout.HelpBox(L.MobileBlockingWarn, MessageType.Warning);
            EditorGUILayout.Space(4);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(L.ColMetric, EditorStyles.toolbarButton, GUILayout.Width(200));
            GUILayout.Label(L.ColValue, EditorStyles.toolbarButton, GUILayout.Width(130));
            GUILayout.Label(L.ColRank, EditorStyles.toolbarButton, GUILayout.Width(160));
            GUILayout.Label(L.ColLimits, EditorStyles.toolbarButton);
            EditorGUILayout.EndHorizontal();

            foreach (var m in results)
            {
                if (problemsOnly && m.Rank <= PerfRank.Good) continue;
                var rowBg = GUI.backgroundColor;
                if (m.Rank >= PerfRank.Poor) GUI.backgroundColor = new Color(1f, .4f, .3f, .15f);
                else if (m.Rank == PerfRank.Medium) GUI.backgroundColor = new Color(1f, .9f, .3f, .10f);

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUILayout.Label(m.Label, GUILayout.Width(200));
                var prevColor = GUI.color;
                GUI.color = Palette.Tint(m.Rank);
                var bold = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = GUI.color } };
                GUILayout.Label(m.Value, bold, GUILayout.Width(130));
                GUILayout.Label(L.RankLabel(m.Rank), bold, GUILayout.Width(160));
                GUI.color = prevColor;
                GUILayout.Label(m.Limits, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
                GUI.backgroundColor = rowBg;
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawSizeInfo()
        {
            const float MB = 1024f * 1024f;
            bool warn = stats.CompressedSizeMB > Thresholds.CompressedLimitMB * 0.9f
                || stats.UncompressedSizeMB > Thresholds.UncompressedLimitMB * 0.9f;
            float meshMB = stats.MeshVRAMBytes / MB;
            string head = $"{L.AvatarTotalSize} (MB): {L.Compressed} {stats.CompressedSizeMB:F1} / {L.Uncompressed} {stats.UncompressedSizeMB:F1}  |  ";
            string detail = L.IsJP
                ? $"テクスチャ: {stats.TextureMemoryMB:F1} MB（{stats.TextureCount}枚）  |  メッシュ: {meshMB:F1} MB"
                : $"Textures: {stats.TextureMemoryMB:F1} MB ({stats.TextureCount} tex)  |  Meshes: {meshMB:F1} MB";
            EditorGUILayout.HelpBox(head + detail + "\n" + (warn ? L.FileSizeWarn : L.FileSizeOK),
                warn ? MessageType.Warning : MessageType.Info);
        }

        void DrawVramSummary()
        {
            float meshMB = stats.MeshVRAMBytes / (1024f * 1024f);
            string text = $"{L.TotalVRAM}: {stats.TotalVRAMMB:F1} MB  [ {L.TextureMemory}: {stats.TextureMemoryMB:F1} MB + {L.MeshVRAM}: {meshMB:F1} MB ]";
            var style = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, fixedHeight = 24,
            };
            var rank = Thresholds.Classify(stats.TextureMemoryMB, Thresholds.Current.TexMB);
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = Palette.Tint(rank);
            GUILayout.Label(text, style);
            GUI.backgroundColor = prevBg;
        }

        // 自動検出は選択中のオブジェクトより親を遡ってアバタールートを探す。ボタン時は選択をそのまま採用
        void Detect(bool fromButton)
        {
            var found = Analyzer.FindAvatarRoot();
            if (!found && Selection.activeGameObject)
            {
                if (fromButton) found = Selection.activeGameObject;
                else
                    for (var go = Selection.activeGameObject; go && !found; go = go.transform.parent ? go.transform.parent.gameObject : null)
                        if (Analyzer.IsAvatar(go)) found = go;
            }
            if (!found) return;
            avatar = found;
            stats = null;
            if (fromButton) Repaint();
        }
    }

    // ---------------------------------------------------------------- VRAMレポート

    public class APCVRAMReport : EditorWindow
    {
        Vector2 scroll;
        AvatarStats stats;
        GameObject avatar;

        [MenuItem("nyaohtech/アバターInfo/詳細表示")]
        public static void ShowWindow() => GetWindow<APCVRAMReport>("APC VRAM Report").minSize = new Vector2(600, 300);

        void OnGUI()
        {
            const float MB = 1024f * 1024f;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L.VRAMTitle, EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            avatar = EditorGUILayout.ObjectField(L.AvatarRoot, avatar, typeof(GameObject), true) as GameObject;
            if (GUILayout.Button(L.Refresh, GUILayout.Width(70))) { StatsCache.Clear(); stats = null; }
            EditorGUILayout.EndHorizontal();

            if (!avatar)
            {
                avatar = Analyzer.FindAvatarRoot();
                if (!avatar) avatar = Selection.activeGameObject;
                if (!avatar) { EditorGUILayout.HelpBox(L.SelectAvatar, MessageType.Info); return; }
            }
            if (stats == null) stats = StatsCache.Avatar(avatar);

            EditorGUILayout.Space(6);
            float texMB = stats.TextureMemoryMB, meshMB = stats.MeshVRAMBytes / MB;
            var pcRank = Thresholds.Classify(texMB, Thresholds.Pc.TexMB);
            var mbRank = Thresholds.Classify(texMB, Thresholds.Mobile.TexMB);
            string ranks = $"PC: {L.RankLabel(pcRank)}   |   Mobile: {L.RankLabel(mbRank)}";
            string summary = L.IsJP
                ? $"推定VRAM合計: {stats.TotalVRAMMB:F1} MB  (テクスチャ: {texMB:F1} MB / {stats.TextureCount}枚 + メッシュ: {meshMB:F1} MB)\n{ranks}"
                : $"Total VRAM: {stats.TotalVRAMMB:F1} MB  (Textures: {texMB:F1} MB / {stats.TextureCount} tex + Meshes: {meshMB:F1} MB)\n{ranks}";
            var color = Palette.Tint((PerfRank)Mathf.Max((int)pcRank, (int)mbRank));
            EditorGUILayout.LabelField(summary, new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = color }, wordWrap = true });
            EditorGUILayout.LabelField(
                $"PC: {string.Join("/", Thresholds.Pc.TexMB.Select(v => v + "MB"))}  |  Mobile: {string.Join("/", Thresholds.Mobile.TexMB.Select(v => v + "MB"))}",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(L.ColTexture, EditorStyles.toolbarButton, GUILayout.MinWidth(140));
            GUILayout.Label(L.ColResolution, EditorStyles.toolbarButton, GUILayout.Width(90));
            GUILayout.Label(L.ColFormat, EditorStyles.toolbarButton, GUILayout.Width(110));
            GUILayout.Label("VRAM", EditorStyles.toolbarButton, GUILayout.Width(70));
            GUILayout.Label("%", EditorStyles.toolbarButton, GUILayout.Width(40));
            GUILayout.Label(L.ColPath, EditorStyles.toolbarButton, GUILayout.MinWidth(120));
            EditorGUILayout.EndHorizontal();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            long total = stats.Textures.Sum(t => t.VRAMBytes);
            foreach (var t in stats.Textures)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(t.Name, EditorStyles.label, GUILayout.MinWidth(140)) && t.Ref) EditorGUIUtility.PingObject(t.Ref);
                GUILayout.Label($"{t.Width}x{t.Height}", GUILayout.Width(90));
                GUILayout.Label(t.Format, EditorStyles.miniLabel, GUILayout.Width(110));
                float mb = t.VRAMBytes / MB;
                GUILayout.Label(mb >= 1f ? $"{mb:F1} MB" : $"{t.VRAMBytes / 1024f:F0} KB", GUILayout.Width(70));
                GUILayout.Label($"{(total > 0 ? t.VRAMBytes * 100f / total : 0):F0}%", GUILayout.Width(40));
                GUILayout.Label(t.Path, EditorStyles.miniLabel, GUILayout.MinWidth(120));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }

    // ---------------------------------------------------------------- 設定ウィンドウ

    public class APCSettingsWindow : EditorWindow
    {
        [MenuItem("nyaohtech/アバターInfo/設定")]
        public static void ShowWindow() => GetWindow<APCSettingsWindow>("APC Settings").minSize = new Vector2(340, 480);

        static void Heading(string text)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L.SettingsTitle, EditorStyles.boldLabel);
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField(L.Language, EditorStyles.boldLabel);
            int prevLang = Prefs.Language;
            Prefs.Language = GUILayout.Toolbar(prevLang, new[] { "日本語", "English" });
            if (Prefs.Language != prevLang)
            {
                StatsCache.Clear();
                foreach (var w in Resources.FindObjectsOfTypeAll<APCDashboard>()) w.Repaint();
                foreach (var w in Resources.FindObjectsOfTypeAll<APCVRAMReport>()) w.Repaint();
                EditorApplication.RepaintHierarchyWindow();
                SceneView.RepaintAll();
            }

            Heading(L.Platform);
            Prefs.PlatformIndex = GUILayout.Toolbar(Prefs.PlatformIndex, new[] { "PC", "Mobile" });
            EditorGUILayout.HelpBox(Prefs.PlatformIndex == 0 ? L.PCLimitsInfo : L.MobileLimitsInfo, MessageType.Info);

            Heading(L.SceneOverlay);
            EditorGUILayout.HelpBox(L.SceneOverlayHint, MessageType.Info);

            Heading(L.HierarchyOverlay);
            Prefs.EnableOverlay = EditorGUILayout.Toggle(L.EnableOverlay, Prefs.EnableOverlay);
            Prefs.ShowTriangles = EditorGUILayout.Toggle(L.ShowTriangles, Prefs.ShowTriangles);
            Prefs.ShowMaterials = EditorGUILayout.Toggle(L.ShowMaterials, Prefs.ShowMaterials);
            Prefs.ShowVRAM = EditorGUILayout.Toggle(L.ShowVRAM, Prefs.ShowVRAM);
            Prefs.ShowCompIcons = EditorGUILayout.Toggle(L.ShowCompIcons, Prefs.ShowCompIcons);
            Prefs.IncludeChildren = EditorGUILayout.Toggle(L.IncludeChildren, Prefs.IncludeChildren);

            Heading(L.CacheSection);
            Prefs.CacheLifetime = EditorGUILayout.Slider(L.CacheLifetime, Prefs.CacheLifetime, 0.5f, 30f);
            if (GUILayout.Button(L.ForceRefresh))
            {
                StatsCache.Clear();
                EditorApplication.RepaintHierarchyWindow();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.HelpBox(L.About, MessageType.Info);
        }
    }
}
#endif
