using UnityEditor;
using UnityEngine;

namespace Nyaohtech.MLVCFileView
{
    // 右クリックは Unity 本体の "Assets" メニューをそのまま出す（Project ウィンドウと同じ項目・同じ動き）
    internal static class FvAssetMenu
    {
        // 標準メニューは選択中のアセットに対して動くので、先に選んでから出す
        public static void Show(string path, Vector2 pos)
        {
            var o = AssetDatabase.LoadMainAssetAtPath(path);
            if (o != null) Selection.activeObject = o;
            EditorApplication.delayCall += () => EditorUtility.DisplayPopupMenu(new Rect(pos.x, pos.y, 0, 0), "Assets", null);
        }
    }
}
