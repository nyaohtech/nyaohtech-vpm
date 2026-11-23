using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Nyaohtech.FvSetup
{
    // 最新版を VPM リポジトリ（GitHub）から取ってきて Packages に入れる。本体はこの .unitypackage には入っていない
    public class FvSetupWindow : EditorWindow
    {
        const string Repo = "https://nyaohtech.github.io/nyaohtech-vpm/index.json";
        const string Pkg = "com.nyaohtech.mlvcfileview";
        const string Dir = "Assets/nyaohtech/Nt_MLVCFileView_Setup";
        const string Tmp = "Temp/NtFvSetup";
        const string Shown = "nt_fvsetup_shown";

        enum St { Idle, Index, Zip, Busy }

        St st;
        UnityWebRequest req;
        List<(string ver, string url, string sha)> cands = new List<(string, string, string)>();
        int ci;
        string msg = "", err = "";

        [MenuItem("nyaohtech/MLVCFileView 導入", false, 901)]
        static void Open() => GetWindow<FvSetupWindow>(true, "Nt_MLVCFileView 導入").Show();

        // 取り込んだ直後に一度だけ開き、未導入なら自動で最新版を取りに行く
        [InitializeOnLoadMethod]
        static void AutoOpen()
        {
            if (SessionState.GetBool(Shown, false)) return;
            SessionState.SetBool(Shown, true);
            EditorApplication.delayCall += () =>
            {
                Open();
                if (Info() == null) GetWindow<FvSetupWindow>().Start();
            };
        }

        static PackageInfo Info() => PackageInfo.FindForAssetPath("Packages/" + Pkg);

        // VCC が入れたパッケージは vpm-manifest.json に載る。そのときは VCC に任せる
        static bool VccManaged()
        {
            try { return File.Exists("Packages/vpm-manifest.json") && File.ReadAllText("Packages/vpm-manifest.json").Contains("\"" + Pkg + "\""); }
            catch { return false; }
        }

        void OnEnable() => EditorApplication.update += Tick;
        void OnDisable() => EditorApplication.update -= Tick;

        void Start()
        {
            if (st != St.Idle) return;
            err = ""; msg = "最新版を確認しています…";
            st = St.Index;
            req = UnityWebRequest.Get(Repo);
            req.timeout = 20;
            req.SendWebRequest();
        }

        void Fail(string m) { err = m; msg = ""; st = St.Idle; req?.Dispose(); req = null; Repaint(); }

        void Tick()
        {
            if (req == null || !req.isDone) return;
            bool ok = req.result == UnityWebRequest.Result.Success;
            if (st == St.Index)
            {
                string text = ok ? req.downloadHandler.text : null;
                req.Dispose(); req = null;
                if (text == null) { Fail("リポジトリに接続できませんでした。ネットワークを確認してください。"); return; }
                try { cands = ParseIndex(text); }
                catch (Exception e) { Fail("リポジトリの情報を読めませんでした: " + e.Message); return; }
                if (cands.Count == 0) { Fail("公開されている版が見つかりませんでした。"); return; }
                ci = 0; GetZip();
            }
            else if (st == St.Zip)
            {
                byte[] data = ok ? req.downloadHandler.data : null;
                req.Dispose(); req = null;
                var c = cands[ci];
                if (data == null)
                {
                    // 公開直後で zip がまだ無いときは、ひとつ前の版を試す
                    if (++ci < Mathf.Min(cands.Count, 3)) { GetZip(); return; }
                    Fail("ダウンロードできませんでした。しばらくしてから、もう一度お試しください。"); return;
                }
                st = St.Busy;
                try { Install(c.ver, c.sha, data); }
                catch (Exception e) { Fail("導入に失敗しました: " + e.Message); return; }
                st = St.Idle; Repaint();
            }
        }

        void GetZip()
        {
            var c = cands[ci];
            msg = c.ver + " をダウンロードしています…";
            st = St.Zip;
            req = UnityWebRequest.Get(c.url);
            req.timeout = 60;
            req.SendWebRequest();
            Repaint();
        }

        static List<(string, string, string)> ParseIndex(string json)
        {
            var root = Json.Parse(json) as Dictionary<string, object>;
            var vs = ((root["packages"] as Dictionary<string, object>)[Pkg] as Dictionary<string, object>)["versions"] as Dictionary<string, object>;
            var l = new List<(Version v, string url, string sha, string name)>();
            foreach (var kv in vs)
            {
                var o = kv.Value as Dictionary<string, object>;
                if (o == null || !Version.TryParse(kv.Key, out var v)) continue;
                string url = o.ContainsKey("url") ? o["url"] as string : null;
                if (url == null || !url.StartsWith("https://")) continue;
                l.Add((v, url, o.ContainsKey("zipSHA256") ? o["zipSHA256"] as string ?? "" : "", kv.Key));
            }
            return l.OrderByDescending(x => x.v).Select(x => (x.name, x.url, x.sha)).ToList();
        }

        void Install(string ver, string sha, byte[] data)
        {
            if (!string.IsNullOrEmpty(sha))
            {
                string got;
                using (var h = SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
                if (!got.Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new Exception("ファイルの検証に失敗しました（SHA256 が一致しません）");
            }
            if (VccManaged()) throw new Exception("このプロジェクトでは VCC が管理しています。VCC から更新してください。");

            if (Directory.Exists(Tmp)) Directory.Delete(Tmp, true);
            string ex = Tmp + "/x";
            Directory.CreateDirectory(ex);
            string full = Path.GetFullPath(ex);
            using (var ms = new MemoryStream(data))
            using (var z = new ZipArchive(ms, ZipArchiveMode.Read))
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    string dest = Path.GetFullPath(Path.Combine(ex, e.FullName));
                    if (!dest.StartsWith(full + Path.DirectorySeparatorChar)) throw new Exception("不正なファイルが含まれています");  // zip の外へ書かせない
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }

            string pj = ex + "/package.json";
            if (!File.Exists(pj) || !File.ReadAllText(pj).Contains("\"" + Pkg + "\"")) throw new Exception("パッケージの中身が想定と違います");

            string target = "Packages/" + Pkg;
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(ex, target);
            Directory.Delete(Tmp, true);
            msg = ver + " を導入しました。";
            AssetDatabase.Refresh();
        }

        void OnGUI()
        {
            var info = Info();
            EditorGUILayout.LabelField("Nt_MLVCFileView", EditorStyles.boldLabel);
            if (info != null)
                EditorGUILayout.HelpBox("導入済みです（" + info.version + "）。メニューバーの nyaohtech > MLVCFileView から開けます。", MessageType.Info);
            else if (st == St.Idle)
                EditorGUILayout.HelpBox("まだ導入されていません。下のボタンで、最新版を GitHub から取ってきて入れます。", MessageType.None);

            if (msg.Length > 0) EditorGUILayout.LabelField(msg, EditorStyles.wordWrappedLabel);
            if (err.Length > 0) EditorGUILayout.HelpBox(err, MessageType.Error);

            GUILayout.Space(6);
            bool vcc = VccManaged();
            using (new EditorGUI.DisabledScope(st != St.Idle || vcc))
                if (GUILayout.Button(info == null ? "最新版をダウンロードして導入" : "最新版に更新", GUILayout.Height(28))) Start();
            if (vcc) EditorGUILayout.LabelField("このプロジェクトは VCC が管理しています。更新は VCC から行ってください。", EditorStyles.wordWrappedLabel);

            GUILayout.Space(8);
            EditorGUILayout.LabelField("VCC で管理したいとき", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("VCC に追加すると、更新の通知が VCC に届きます。", EditorStyles.wordWrappedLabel);
            if (GUILayout.Button("VCC にリポジトリを追加"))
                Application.OpenURL("vcc://vpm/addRepo?url=" + UnityWebRequest.EscapeURL(Repo));
            if (GUILayout.Button("URL をコピー（VCC に手で貼る場合）"))
                EditorGUIUtility.systemCopyBuffer = Repo;
            if (info != null && !vcc)
            {
                EditorGUILayout.LabelField("VCC の「Manage Project」で追加する前に、いま入っている本体を外します。", EditorStyles.wordWrappedLabel);
                if (GUILayout.Button("本体を外す（VCC 管理に切り替える）"))
                    if (EditorUtility.DisplayDialog("本体を外す", "Packages の " + Pkg + " を削除します。その後 VCC の Manage Project で Nt_MLVCFileView の「＋」を押してください。", "外す", "やめる"))
                    {
                        FileUtil.DeleteFileOrDirectory("Packages/" + Pkg);
                        AssetDatabase.Refresh();
                    }
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("この案内を削除"))
            {
                if (!EditorUtility.DisplayDialog("削除", "この案内を、プロジェクトから削除します。導入した本体には影響しません。", "削除", "やめる")) return;
                AssetDatabase.DeleteAsset(Dir);
                if (AssetDatabase.FindAssets("", new[] { "Assets/nyaohtech" }).Length == 0) AssetDatabase.DeleteAsset("Assets/nyaohtech");
            }
        }
    }

    // index.json 用の最小の JSON 読み取り（Newtonsoft が入っていないプロジェクトでも動かすため）
    static class Json
    {
        public static object Parse(string s) { int i = 0; return Val(s, ref i); }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Val(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++;
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); string k = Str(s, ref i); Ws(s, ref i);
                    if (s[i++] != ':') throw new FormatException();
                    d[k] = Val(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == '}') return d;
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Val(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == ']') return l;
                    throw new FormatException();
                }
            }
            if (c == '"') return Str(s, ref i);
            int st = i;
            while (i < s.Length && ",}] \r\n\t".IndexOf(s[i]) < 0) i++;
            return s.Substring(st, i - st);  // 数値・true・false・null は文字列のまま（使わない）
        }

        static string Str(string s, ref int i)
        {
            if (s[i++] != '"') throw new FormatException();
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }
    }
}
