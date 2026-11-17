# Nt_MLVCFileView

Unity の Assets フォルダ用ファイラー。メニュー **nyaohtech > MLVCFileView** で開きます。

- **ファイル**タブ: Assets を 1 フォルダずつ表示（検索・ドラッグ&ドロップ・右クリックは Unity 標準メニュー）
- **アバター / 衣装 / 小物 / ギミック**タブ: プレハブの YAML を読み、VRCAvatarDescriptor / Modular Avatar のコンポーネントから自動で振り分け（バリアントも継承）
- **取り込み順**で並べ替え。Assets 直下で一度も開いていないフォルダは「未開封」でハイライト
- **MLVC / VLA で取り込んだアイテム**はサムネイル付きで表示（左に取り込み履歴）

## サムネイルと軽さ
MLVC が `<プロジェクト>/.mlvc/` に縮小済み画像（長辺 256px）を書き出し、FileView はそれだけを読みます（`.mlvc` は Assets の外なので Unity のインポート対象になりません）。表示中のものだけを読み込み、最大 96 枚で LRU 破棄、ウィンドウを閉じる／スクリプト再読み込みで全解放します。MLVC/VLA 以外のフォルダやプレハブはサムネイルを作らず、型アイコンのみです。

## 「触れた」の定義
FileView で選択・開いた、または Unity の Project ウィンドウ等で選択した Assets 直下フォルダ。記録は `UserSettings/MLVCFileView.json`（個人設定）。初回起動時に「今あるフォルダを既読にするか」を聞きます。

## 要件
Unity 2022.3 以降。Modular Avatar / VRCSDK が無くても動作します（役割判定のみ該当なし）。
