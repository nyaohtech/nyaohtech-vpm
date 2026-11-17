using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nyaohtech.MLVCFileView
{
    // Project ウィンドウの右クリックと同じ操作を、FileView の右クリックメニューに足す。
    // 作成・そのほかは Unity 本体の "Assets" メニューをそのまま出す（本体と同じ項目になる）
    internal static class FvAssetMenu
    {
        public static void Add(DropdownMenu menu, string path, Vector2 pos, Action refresh)
        {
            bool real = path != "Assets";
            if (real) menu.AppendSeparator();

            menu.AppendAction("作成", _ => Native(path, pos, "Assets/Create"));
            if (real)
            {
                menu.AppendAction("名前を変更…", _ => FvRenameWindow.Open(path, refresh));
                menu.AppendAction("複製", _ => Duplicate(path, refresh));
                menu.AppendAction("削除", _ => Delete(path, refresh));
                menu.AppendSeparator();
                menu.AppendAction("再インポート", _ => AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive));
                menu.AppendAction("C# プロジェクトを開く", _ => EditorApplication.ExecuteMenuItem("Assets/Open C# Project"));
            }
            menu.AppendAction("Unity の標準メニュー…", _ => Native(path, pos, "Assets"));
        }

        // 標準メニューは選択中のアセットに対して動くので、先に選んでから出す
        static void Native(string path, Vector2 pos, string menuPath)
        {
            var o = AssetDatabase.LoadMainAssetAtPath(path);
            if (o != null) Selection.activeObject = o;
            EditorApplication.delayCall += () => EditorUtility.DisplayPopupMenu(new Rect(pos.x, pos.y, 0, 0), menuPath, null);
        }

        static void Duplicate(string path, Action refresh)
        {
            string to = AssetDatabase.GenerateUniqueAssetPath(path);
            if (AssetDatabase.CopyAsset(path, to)) { AssetDatabase.Refresh(); refresh?.Invoke(); }
        }

        static void Delete(string path, Action refresh)
        {
            if (!EditorUtility.DisplayDialog("削除", "「" + path + "」をごみ箱へ移します。\nUndo はできません。", "削除", "やめる")) return;
            if (AssetDatabase.MoveAssetToTrash(path)) refresh?.Invoke();
        }
    }

    internal sealed class FvRenameWindow : EditorWindow
    {
        string _path, _name; Action _done; bool _focus = true;

        public static void Open(string path, Action done)
        {
            var w = CreateInstance<FvRenameWindow>();
            w._path = path; w._done = done;
            w._name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (AssetDatabase.IsValidFolder(path)) w._name = System.IO.Path.GetFileName(path);
            w.titleContent = new GUIContent("名前を変更");
            var p = GUIUtility.GUIToScreenPoint(Event.current != null ? Event.current.mousePosition : Vector2.zero);
            w.position = new Rect(p.x, p.y, 320, 62);
            w.ShowUtility();
        }

        void OnGUI()
        {
            GUI.SetNextControlName("n");
            _name = EditorGUILayout.TextField(_name);
            if (_focus) { EditorGUI.FocusTextInControl("n"); _focus = false; }
            var e = Event.current;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { Close(); return; }
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("やめる", GUILayout.Width(70))) { Close(); return; }
            if (GUILayout.Button("変更", GUILayout.Width(70)) || enter) Apply();
            EditorGUILayout.EndHorizontal();
        }

        void Apply()
        {
            string err = _name.Trim().Length == 0 ? "名前が空です" : AssetDatabase.RenameAsset(_path, _name.Trim());
            if (!string.IsNullOrEmpty(err)) { EditorUtility.DisplayDialog("名前を変更できません", err, "OK"); return; }
            _done?.Invoke();
            Close();
        }
    }
}
