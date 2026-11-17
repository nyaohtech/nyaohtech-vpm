// エディタ終了時に Scene ビューを撮って、MLVC の一覧用サムネイルにする
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MLVCTool
{
    [InitializeOnLoad]
    public static class MVLCScreenshotBridge
    {
        const string MutexName = @"Local\MLVC_MoreLighterVRChatCreator_SingleInstance";
        const string RequestEvent = @"Local\MLVC_CaptureRequest";
        const double WaitSec = 1.5;
        const int MaxWidth = 640;

        static bool canQuit;
        static EventWaitHandle doneEvent;
        static Stopwatch sw;

        static string LocalMlvc(string sub) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MLVC", sub);

        static MVLCScreenshotBridge()
        {
            EditorApplication.wantsToQuit += OnQuit;
        }

        static bool OnQuit()
        {
            if (canQuit) return true;

            try
            {
                if (!MlvcRunning()) return true;

                string proj = Directory.GetParent(Application.dataPath).FullName;
                int pid = Process.GetCurrentProcess().Id;

                bool wrote = false;
                try { wrote = CaptureScene(proj); } catch { }

                string dir = LocalMlvc("CaptureRequests");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, pid + ".request"),
                    proj + "\n" + pid + "\n" + (wrote ? "written" : "capture"));

                using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, RequestEvent))
                    ev.Set();

                if (wrote) return true;

                // Scene ビューが無いときは MLVC にウィンドウ撮影を頼む。 ここで待つとUnityが固まって真っ黒に撮れるので、終了を止めてフレームごとに確認する
                doneEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MLVC_CaptureDone_" + pid);
                sw = Stopwatch.StartNew();
                EditorApplication.update += Poll;
                return false;
            }
            catch
            {
                return true;
            }
        }

        static void Poll()
        {
            bool done;
            try { done = doneEvent != null && doneEvent.WaitOne(0); }
            catch { done = true; }

            if (!done && sw.Elapsed.TotalSeconds <= WaitSec) return;

            EditorApplication.update -= Poll;
            try { doneEvent?.Dispose(); } catch { }
            doneEvent = null;

            canQuit = true;
            EditorApplication.Exit(0);
        }

        // Scene ビューのカメラを RenderTexture に描いて PNG にする。ビューが無ければ false
        static bool CaptureScene(string proj)
        {
            var sv = SceneView.lastActiveSceneView;
            var cam = sv != null ? sv.camera : null;
            if (cam == null) return false;

            int srcW = Mathf.Max(cam.pixelWidth, 1);
            int srcH = Mathf.Max(cam.pixelHeight, 1);
            int w = Mathf.Min(MaxWidth, srcW);
            int h = Mathf.Max(1, Mathf.RoundToInt(w * (srcH / (float)srcW)));

            RenderTexture rt = null;
            var prevActive = RenderTexture.active;
            var prevTarget = cam.targetTexture;
            Texture2D tex = null;
            try
            {
                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                byte[] png = tex.EncodeToPNG();
                if (png == null || png.Length == 0) return false;

                string dest = ThumbPath(proj);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                string tmp = dest + ".tmp";
                File.WriteAllBytes(tmp, png);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }
        }

        // MLVC の ThumbnailService.PathFor と同じ規則（パスの SHA1）。 Mono に SHA1.HashData が無いので Create() を使う
        static string ThumbPath(string proj)
        {
            string norm = proj.TrimEnd('\\', '/').ToLowerInvariant();
            byte[] hash;
            using (var sha = SHA1.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));

            var sb = new StringBuilder();
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return Path.Combine(LocalMlvc("Thumbnails"), sb + ".png");
        }

        static bool MlvcRunning()
        {
            try
            {
                using (Mutex.OpenExisting(MutexName)) return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
#endif
