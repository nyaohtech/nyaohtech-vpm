// MLVCFileView - データモデルと Assets フォルダの列挙 Assets の中身は「開いたフォルダ 1 つ分」だけをその都度列挙する（全件を最初に読み込まない）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nyaohtech.MLVCFileView
{
    public enum FvKind { Folder, Prefab, Scene, Model, Texture, Material, Animation, Script, Audio, Shader, Other }

    // プレハブの役割。VRCAvatarDescriptor / Modular Avatar のコンポーネントから判定する。
    public enum FvRole { None, Avatar, Costume, Prop, Gimmick }

    public sealed class FvEntry
    {
        public string Path = "";  // "Assets/Foo/Bar.prefab"
        public string Name = "";
        public bool IsFolder;
        public FvKind Kind;
        public DateTime CreatedUtc;
        public DateTime ModifiedUtc;
        public long Size;
        public string Root = "";
        public FvRole Role;
        public DateTime OrderKey;  // 取り込み順の並べ替えキー（新しいほど大きい）
    }

    public enum FvSort { ImportOrder, Name, Modified }

    internal static class FvPaths
    {
        static string _root;

        // メインスレッドで一度呼んでおくと、以降はバックグラウンドスレッドからも ProjectRoot を使える。
        public static void Init() { if (_root == null) _root = Directory.GetParent(Application.dataPath).FullName; }

        public static string ProjectRoot { get { Init(); return _root; } }

        public static string Abs(string assetPath) =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectRoot, assetPath));

        public static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

        // "Assets/Foo/Bar" → "Assets/Foo"。Assets 直下のファイルや Assets 自身は空文字。
        public static string RootOf(string assetPath)
        {
            var seg = Norm(assetPath).Split('/');
            return seg.Length >= 3 && seg[0] == "Assets" ? seg[0] + "/" + seg[1] : "";
        }

        // "Assets/Foo" 自身も含めて Assets 直下フォルダを返す（"Assets/Foo/x.png" → "Assets/Foo"、"Assets/Foo" → "Assets/Foo"）。
        public static string RootOrSelf(string assetPath)
        {
            var seg = Norm(assetPath).Split('/');
            return seg.Length >= 2 && seg[0] == "Assets" ? seg[0] + "/" + seg[1] : "";
        }
    }

    internal static class FvScanner
    {
        static readonly Dictionary<string, FvKind> ExtKinds = new Dictionary<string, FvKind>(StringComparer.OrdinalIgnoreCase)
        {
            {".prefab", FvKind.Prefab}, {".unity", FvKind.Scene},
            {".fbx", FvKind.Model}, {".obj", FvKind.Model}, {".blend", FvKind.Model}, {".dae", FvKind.Model},
            {".png", FvKind.Texture}, {".jpg", FvKind.Texture}, {".jpeg", FvKind.Texture}, {".tga", FvKind.Texture},
            {".psd", FvKind.Texture}, {".tif", FvKind.Texture}, {".tiff", FvKind.Texture}, {".exr", FvKind.Texture},
            {".webp", FvKind.Texture}, {".bmp", FvKind.Texture}, {".gif", FvKind.Texture},
            {".mat", FvKind.Material},
            {".anim", FvKind.Animation}, {".controller", FvKind.Animation}, {".overrideController", FvKind.Animation},
            {".cs", FvKind.Script}, {".asmdef", FvKind.Script},
            {".wav", FvKind.Audio}, {".mp3", FvKind.Audio}, {".ogg", FvKind.Audio},
            {".shader", FvKind.Shader}, {".cginc", FvKind.Shader}, {".hlsl", FvKind.Shader},
        };

        public static FvKind KindOf(string path, bool isFolder)
        {
            if (isFolder) return FvKind.Folder;
            return ExtKinds.TryGetValue(System.IO.Path.GetExtension(path), out var k) ? k : FvKind.Other;
        }

        public static string KindLabel(FvKind k)
        {
            switch (k)
            {
                case FvKind.Folder: return "フォルダ";
                case FvKind.Prefab: return "プレハブ";
                case FvKind.Scene: return "シーン";
                case FvKind.Model: return "モデル";
                case FvKind.Texture: return "テクスチャ";
                case FvKind.Material: return "マテリアル";
                case FvKind.Animation: return "アニメーション";
                case FvKind.Script: return "スクリプト";
                case FvKind.Audio: return "オーディオ";
                case FvKind.Shader: return "シェーダー";
                default: return "ファイル";
            }
        }

        public static string RoleLabel(FvRole r)
        {
            switch (r)
            {
                case FvRole.Avatar: return "アバター";
                case FvRole.Costume: return "衣装";
                case FvRole.Prop: return "小物";
                case FvRole.Gimmick: return "ギミック";
                default: return "";
            }
        }

        // Unity が無視する名前（.meta、"."始まり、"~"終わり、cvs 等）。
        internal static bool Ignored(string name) =>
            name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".", StringComparison.Ordinal)
            || name.EndsWith("~", StringComparison.Ordinal)
            || name.Equals("cvs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        public static List<FvEntry> ListFolder(string assetPath)
        {
            var list = new List<FvEntry>();
            try
            {
                var dir = new DirectoryInfo(FvPaths.Abs(assetPath));
                if (!dir.Exists) return list;
                foreach (var fi in dir.EnumerateFileSystemInfos())
                {
                    if (Ignored(fi.Name)) continue;
                    bool isFolder = (fi.Attributes & FileAttributes.Directory) != 0;
                    string p = FvPaths.Norm(assetPath) + "/" + fi.Name;
                    list.Add(Make(p, fi, isFolder));
                }
            }
            catch (Exception ex) { Debug.LogWarning("[MLVCFileView] 列挙に失敗: " + assetPath + " " + ex.Message); }
            return list;
        }

        // 直下のフォルダ名だけを名前順で返す（フォルダツリー用。ファイルは見ない）。
        public static List<string> ListDirs(string assetPath)
        {
            var list = new List<string>();
            try
            {
                foreach (var d in new DirectoryInfo(FvPaths.Abs(assetPath)).EnumerateDirectories())
                    if (!Ignored(d.Name)) list.Add(FvPaths.Norm(assetPath) + "/" + d.Name);
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return list;
        }

        public static bool HasDirs(string assetPath)
        {
            try
            {
                foreach (var d in new DirectoryInfo(FvPaths.Abs(assetPath)).EnumerateDirectories())
                    if (!Ignored(d.Name)) return true;
            }
            catch { }
            return false;
        }

        // UI Toolkit の標準フォントで確実に出せる文字だけ通す。
        // 絵文字・制御文字・私用領域などは取り除き、フォントに無い記号類は ? に置き換える（警告ログや □ 表示を避けるため）
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n' || c == '\t') { sb.Append(c); continue; }
                if (Drop(c)) continue;
                sb.Append(Safe(c) ? c : '?');
            }
            return sb.ToString().Trim();
        }

        static bool Drop(char c) =>
            c < ' ' || (c >= '\u007f' && c <= '\u009f') || char.IsSurrogate(c)
            || (c >= '​' && c <= '‏') || (c >= '‪' && c <= '‮') || (c >= '⁠' && c <= '⁯')
            || (c >= '︀' && c <= '️') || (c >= '' && c <= '') || c == '﻿' || c >= '￰'
            || (c >= '☀' && c <= '➿');

        static bool Safe(char c) =>
            c < 'ɐ'                                    // ASCII・ラテン
            || (c >= 'Ͱ' && c <= 'ԯ')             // ギリシャ・キリル
            || (c >= '‐' && c <= '‧') || (c >= '‰' && c <= '⁞')   // 一般句読点
            || (c >= '←' && c <= '↓') || c == '■' || c == '□' || c == '▲' || c == '△' || c == '▼' || c == '▽' || c == '○' || c == '●'
            || (c >= '　' && c <= 'ヿ')             // 和文記号・かな
            || (c >= 'ㇰ' && c <= 'ㇿ')
            || (c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '鿿')   // 漢字
            || (c >= '가' && c <= '힯')             // ハングル
            || (c >= '！' && c <= 'ﾟ') || (c >= '￠' && c <= '￦');  // 全角・半角

        public static FvEntry Make(string assetPath, FileSystemInfo fi, bool isFolder)
        {
            return new FvEntry
            {
                Path = assetPath,
                Name = fi.Name,
                IsFolder = isFolder,
                Kind = KindOf(assetPath, isFolder),
                CreatedUtc = fi.CreationTimeUtc,
                ModifiedUtc = fi.LastWriteTimeUtc,
                Size = isFolder ? 0 : ((FileInfo)fi).Length,
                Root = FvPaths.RootOf(assetPath),
            };
        }

        // 名前に query を含むものを最大 max 件。深い階層まで探すので、呼び出し側でキャンセルできるようにしてある。
        public static List<FvEntry> Search(string query, int max, System.Threading.CancellationToken ct)
        {
            var result = new List<FvEntry>();
            try
            {
                string rootAbs = FvPaths.Abs("Assets");
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var fi in new DirectoryInfo(rootAbs).EnumerateFileSystemInfos("*", opt))
                {
                    if (ct.IsCancellationRequested) break;
                    if (Ignored(fi.Name)) continue;
                    if (fi.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool isFolder = (fi.Attributes & FileAttributes.Directory) != 0;
                    string rel = FvPaths.Norm(fi.FullName).Substring(FvPaths.Norm(FvPaths.ProjectRoot).Length + 1);
                    if (rel.Split('/').Any(s => s.StartsWith(".", StringComparison.Ordinal) || s.EndsWith("~", StringComparison.Ordinal))) continue;
                    result.Add(Make(rel, fi, isFolder));
                    if (result.Count >= max) break;
                }
            }
            catch { }
            return result;
        }
    }
}
