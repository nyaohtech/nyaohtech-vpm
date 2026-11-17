// 「触れたフォルダ」の記録
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nyaohtech.MLVCFileView
{
    [Serializable]
    internal sealed class FvTouchedJson
    {
        public bool baselined;
        public List<string> paths = new List<string>();
    }

    [InitializeOnLoad]
    internal static class FvTouched
    {
        static readonly HashSet<string> Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool _baselined;
        static bool _saveQueued;

        public static event Action Changed;

        static string FilePath => Path.Combine(FvPaths.ProjectRoot, "UserSettings", "MLVCFileView.json");

        public static bool Baselined => _baselined;

        static FvTouched()
        {
            Load();
            Selection.selectionChanged += OnSelection;
        }

        static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var j = JsonUtility.FromJson<FvTouchedJson>(File.ReadAllText(FilePath));
                if (j == null) return;
                _baselined = j.baselined;
                foreach (var p in j.paths) Set.Add(p);
            }
            catch { }
        }

        static void QueueSave()
        {
            if (_saveQueued) return;
            _saveQueued = true;
            EditorApplication.delayCall += () =>
            {
                _saveQueued = false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var j = new FvTouchedJson { baselined = _baselined, paths = new List<string>(Set) };
                    File.WriteAllText(FilePath, JsonUtility.ToJson(j));
                }
                catch (Exception ex) { Debug.LogWarning("[MLVCFileView] 保存に失敗: " + ex.Message); }
            };
        }

        static void OnSelection()
        {
            var guids = Selection.assetGUIDs;
            if (guids == null || guids.Length == 0) return;
            bool any = false;
            int n = Math.Min(guids.Length, 20);
            for (int i = 0; i < n; i++)
                any |= Add(AssetDatabase.GUIDToAssetPath(guids[i]));
            if (any) { QueueSave(); Changed?.Invoke(); }
        }

        static bool Add(string assetPath)
        {
            string root = FvPaths.RootOrSelf(assetPath ?? "");
            return root.Length > 0 && Set.Add(root);
        }

        public static bool IsTouched(string root) => Set.Contains(root);

        public static void Mark(string assetPath)
        {
            if (Add(assetPath)) { QueueSave(); Changed?.Invoke(); }
        }

        public static void Unmark(string assetPath)
        {
            string root = FvPaths.RootOrSelf(assetPath ?? "");
            if (root.Length > 0 && Set.Remove(root)) { QueueSave(); Changed?.Invoke(); }
        }

        // 今ある Assets 直下フォルダを全部「触れた」扱いにし、以後は新しく増えたものだけを目立たせる。
        public static void MarkAllExisting()
        {
            try
            {
                foreach (var d in new DirectoryInfo(FvPaths.Abs("Assets")).EnumerateDirectories())
                    if (!d.Name.StartsWith(".") && !d.Name.EndsWith("~")) Set.Add("Assets/" + d.Name);
            }
            catch { }
            _baselined = true;
            QueueSave();
            Changed?.Invoke();
        }

        public static void SetBaselined()
        {
            if (_baselined) return;
            _baselined = true;
            QueueSave();
        }
    }
}
