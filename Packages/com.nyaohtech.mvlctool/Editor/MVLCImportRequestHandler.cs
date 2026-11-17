// 起動中の MLVC から「この unitypackage / テクスチャを取り込んで」と頼まれたとき、Unity 側で取り込む。 一覧やサムネイルは MLVC 側で見るので、ここはパスのリストを受けて取り込むだけ
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MLVCTool
{
    [Serializable]
    internal class MVLCTextureJob
    {
        public string sourceFolder;
        public string destFolderName;
    }

    [Serializable]
    internal class MVLCImportRequestDto
    {
        public List<string> unityPackagePaths;
        public List<MVLCTextureJob> textureJobs;
        public int packageBatchSize;  // 0 なら自分のメモリ量から決める
    }

    [InitializeOnLoad]
    public static class MVLCImportRequestHandler
    {
        const string EventName = @"Local\MLVC_ImportRequest";
        const string ReqDir = "MLVCImportRequests";

        static readonly string[] TexExt = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".bmp" };

        static volatile bool pending;
        static EventWaitHandle ev;

        static MVLCImportRequestHandler()
        {
            try
            {
                ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                new Thread(WaitLoop) { IsBackground = true, Name = "MVLC-ImportRequestListener" }.Start();
            }
            catch (Exception e)
            {
                Debug.LogError("[MVLC] 取り込みリクエストの待ち受けに失敗: " + e);
            }

            EditorApplication.update += OnUpdate;
            pending = true;  // Unity が閉じている間に届いた分を起動時に1回見る
        }

        // Unity の API はメインスレッド専用なので、ここはフラグを立てるだけ
        static void WaitLoop()
        {
            while (true)
            {
                try { ev.WaitOne(); }
                catch { return; }
                pending = true;
            }
        }

        static void OnUpdate()
        {
            if (!pending) return;
            pending = false;
            Process();
        }

        static string Dir => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ProjectSettings", ReqDir);

        static void Process()
        {
            if (!Directory.Exists(Dir)) return;

            string[] files;
            try { files = Directory.GetFiles(Dir, "*.json"); }
            catch { return; }
            if (files.Length == 0) return;

            var pkgs = new List<string>();
            var jobs = new List<MVLCTextureJob>();
            int batch = 0;

            foreach (string f in files)
            {
                try
                {
                    var dto = JsonUtility.FromJson<MVLCImportRequestDto>(File.ReadAllText(f));
                    if (dto == null) continue;
                    if (dto.unityPackagePaths != null) pkgs.AddRange(dto.unityPackagePaths);
                    if (dto.textureJobs != null) jobs.AddRange(dto.textureJobs);
                    // 複数あるときは小さいほうに合わせる
                    if (dto.packageBatchSize > 0)
                        batch = batch <= 0 ? dto.packageBatchSize : Math.Min(batch, dto.packageBatchSize);
                }
                catch (Exception e)
                {
                    Debug.LogError("[MVLC] リクエストの読み込みに失敗: " + f + " : " + e);
                }
                finally
                {
                    try { File.Delete(f); } catch { }
                }
            }

            if (pkgs.Count == 0 && jobs.Count == 0) return;

            var existing = pkgs.Where(File.Exists).ToList();
            Debug.Log("[MVLC] MLVC から取り込み指示（unitypackage " + existing.Count + " 件 / テクスチャ " + jobs.Count + " 件）");

            // まとめて取り込むとメモリが跳ねるので、数個ずつに分けて確定させる
            if (batch <= 0) batch = BatchFromMemory();
            for (int i = 0; i < existing.Count; i += batch)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (string p in existing.Skip(i).Take(batch))
                        AssetDatabase.ImportPackage(p, false);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
                EditorUtility.UnloadUnusedAssetsImmediate();
                GC.Collect();
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var j in jobs) CopyTextures(j);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();
            Debug.Log("[MVLC] 取り込み完了");
        }

        // MLVC の LowSpecTuning と同じ目安
        static int BatchFromMemory()
        {
            int mb = SystemInfo.systemMemorySize;
            if (mb <= 0) return 3;
            if (mb <= 4608) return 1;
            if (mb <= 8704) return 3;
            if (mb <= 16896) return 6;
            return 10;
        }

        // 包みフォルダは作らず、Assets 直下にアイテム名のフォルダで置く（MLVC 側と同じ）
        static void CopyTextures(MVLCTextureJob job)
        {
            if (job == null || string.IsNullOrEmpty(job.sourceFolder) || !Directory.Exists(job.sourceFolder)) return;

            string name = string.IsNullOrEmpty(job.destFolderName) ? "asset" : job.destFolderName;
            string root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets", name);

            try
            {
                Directory.CreateDirectory(root);
                foreach (string f in Directory.EnumerateFiles(job.sourceFolder, "*", SearchOption.AllDirectories))
                {
                    if (!TexExt.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                    string rel = f.Substring(job.sourceFolder.Length).TrimStart('\\', '/');
                    string dest = Path.Combine(root, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f, dest, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MVLC] テクスチャの取り込みに失敗: " + job.sourceFolder + " : " + e);
            }
        }
    }
}
#endif
