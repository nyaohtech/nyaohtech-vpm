using UnityEditor;
using UnityEngine;

namespace Nyaohtech.MLVCFileView
{
    // 右クリックは Unity 本体の "Assets" メニューをそのまま出す（Project ウィンドウと同じ項目・同じ動き）
    internal static class FvAssetMenu
    {
        // 標準メニューは選択中のアセットに対して動くので、先に選んでから出す。
        // DisplayPopupMenu は「いま描画中のビュー」を基準に座標を変換するため、
        // 遅延実行だとビューが違って右にずれる。画面座標を自前で出し、その変換分を引いて渡す
        public static void Show(EditorWindow w, string path, Vector2 pos)
        {
            var o = AssetDatabase.LoadMainAssetAtPath(path);
            if (o != null) Selection.activeObject = o;
            Vector2 screen = w.position.position + pos;
            EditorApplication.delayCall += () =>
            {
                Vector2 at = screen - GUIUtility.GUIToScreenPoint(Vector2.zero);
                EditorUtility.DisplayPopupMenu(new Rect(at.x, at.y, 0, 0), "Assets", null);
            };
        }
    }
}
