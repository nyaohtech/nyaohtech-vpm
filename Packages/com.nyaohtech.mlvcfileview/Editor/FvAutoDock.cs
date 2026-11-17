// 導入後の初回だけ、Project ウィンドウと同じタブグループへファイラーを置く（以後は自由に動かせる）。
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nyaohtech.MLVCFileView
{
    internal static class FvAutoDock
    {
        static string Marker => Path.Combine(FvPaths.ProjectRoot, "UserSettings", "MLVCFileView.docked");

        [InitializeOnLoadMethod]
        static void Init()
        {
            FvPaths.Init();
            if (File.Exists(Marker)) return;
            EditorApplication.delayCall += () => EditorApplication.delayCall += Dock;
        }

        static void Dock()
        {
            var pb = typeof(Editor).Assembly.GetType("UnityEditor.ProjectBrowser");
            if (pb == null || Resources.FindObjectsOfTypeAll(pb).Length == 0) return;  // Project が無ければ次回に回す
            try
            {
                EditorWindow.GetWindow<MLVCFileViewWindow>(pb).Show();  // Project のタブグループへ
                Directory.CreateDirectory(Path.GetDirectoryName(Marker));
                File.WriteAllText(Marker, "1");
            }
            catch (Exception e) { Debug.LogWarning("[MLVCFileView] 初期配置に失敗: " + e.Message); }
        }
    }
}
