using UnityEditor;
using UnityEngine;

namespace Nyaohtech.Setup
{
    // VCC にリポジトリを追加するための案内。ツール本体は VCC から入れる
    public class NtSetupWindow : EditorWindow
    {
        const string Repo = "https://nyaohtech.github.io/nyaohtech-vpm/index.json";
        const string Pkg = "com.nyaohtech.avatorsupports";
        const string Dir = "Assets/nyaohtech/Nt_AvatorSupports_Setup";
        const string Shown = "nt_setup_shown";

        [MenuItem("nyaohtech/導入(VCCに追加)", false, 900)]
        static void Open() => GetWindow<NtSetupWindow>(true, "Nt_AvatorSupports 導入").Show();

        // 取り込んだ直後に、一度だけ開く。導入済みなら開かない
        [InitializeOnLoadMethod]
        static void AutoOpen()
        {
            if (SessionState.GetBool(Shown, false)) return;
            SessionState.SetBool(Shown, true);
            EditorApplication.delayCall += () => { if (!Installed()) Open(); };
        }

        static bool Installed() => UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + Pkg) != null;

        void OnGUI()
        {
            EditorGUILayout.LabelField("Nt_AvatorSupports", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("ツールは VCC(VRChat Creator Companion)から入れます。更新も VCC に通知されます。", MessageType.Info);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("1. VCC にリポジトリを追加", EditorStyles.boldLabel);
            if (GUILayout.Button("VCC で追加する", GUILayout.Height(28)))
                Application.OpenURL("vcc://vpm/addRepo?url=" + UnityEngine.Networking.UnityWebRequest.EscapeURL(Repo));
            if (GUILayout.Button("URL をコピー(VCC に手で貼る場合)"))
                EditorGUIUtility.systemCopyBuffer = Repo;

            GUILayout.Space(6);
            EditorGUILayout.LabelField("2. プロジェクトに追加", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("VCC の「Manage Project」で、Nt_AvatorSupports の「＋」を押します。", EditorStyles.wordWrappedLabel);

            GUILayout.Space(6);
            EditorGUILayout.LabelField(Installed() ? "導入済みです。メニューバーの nyaohtech から使えます。" : "まだ導入されていません。", EditorStyles.wordWrappedLabel);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("このセットアップを削除"))
            {
                if (!EditorUtility.DisplayDialog("削除", "この案内を、プロジェクトから削除します。導入したツールには影響しません。", "削除", "やめる")) return;
                AssetDatabase.DeleteAsset(Dir);
                if (AssetDatabase.FindAssets("", new[] { "Assets/nyaohtech" }).Length == 0) AssetDatabase.DeleteAsset("Assets/nyaohtech");
            }
        }
    }
}
