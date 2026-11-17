// 起動直後、コンパイルと読み込みが落ち着いたら MLVC に1回だけ知らせる
#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MLVCTool
{
    [InitializeOnLoad]
    public static class MVLCReadyNotifier
    {
        const string SessionKey = "MVLC_ReadyNotified_v1";
        const string EventName = @"Local\MLVC_ReadyRequest";
        // フレーム数だと遅いPCで早く出てしまうので秒で数える
        const double StableSec = 3.0;
        static double stableSince = -1;

        static MVLCReadyNotifier()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            EditorApplication.update += Check;
        }

        static void Check()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                stableSince = -1;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (stableSince < 0) stableSince = now;
            if (now - stableSince < StableSec) return;

            EditorApplication.update -= Check;
            SessionState.SetBool(SessionKey, true);
            Send();
        }

        static void Send()
        {
            try
            {
                string proj = Directory.GetParent(Application.dataPath).FullName;
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MLVC", "ReadyRequests");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".ready"), proj);

                using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName))
                    ev.Set();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MVLC] 起動完了の通知に失敗: " + e);
            }
        }
    }
}
#endif
