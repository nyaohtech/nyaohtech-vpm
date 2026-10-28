# nyaohtech VPM Repository

[nyaohtech](https://nyaohtech.raindrop.jp/) の VRChat アバター向け Unity ツールを、VRChat Creator Companion (VCC) からそのまま導入できる VPM リポジトリです。

紹介ページ: https://nyaohtech.github.io/nyaohtech-vpm/

## VCC への追加

次のどちらかで、リポジトリを追加できます。

- 紹介ページの「VCC に追加する」ボタンを押す
- VCC の「Settings → Packages → Add Repository」に、次の URL を貼り付ける

```
https://nyaohtech.github.io/nyaohtech-vpm/index.json
```

追加したあとは、VCC のプロジェクトの「Manage Project」から、使いたいパッケージの「＋」を押すだけで導入できます。Unity を開くと、メニューバーの **nyaohtech** から使えます。

## パッケージ

| パッケージ | 内容 | Unity メニュー |
|---|---|---|
| **Nt_AvatorSupports** | 下の 2 つを、まとめて一度に導入するための詰め合わせ（これ自体に機能はありません） | — |
| **Nt_AvatorPerformanceChecker** | アバターのパフォーマンスランク（PC / Mobile）と、テクスチャの VRAM・アップロード容量の目安を、Scene ビューとダッシュボードで確認できるチェッカー | nyaohtech → アバターInfo |
| **Nt_SimpleMatResize** | アバターを複製して、テクスチャを一括で縮小し、新しいプレファブとして書き出す軽量化ツール（元のアバターは変更されません） | nyaohtech → アバター軽量化 |

- 対応する Unity: 2022.3 以降（VRChat 推奨のバージョン）
- 縮小したファイルは `Assets/SimpleMatResize/Generated` に出力されます。
- パフォーマンスランクの基準は VRChat 公式の値です。VRAM やアップロード容量は目安の概算なので、最終確認は VRChat SDK のビルド画面で行ってください。

## このリポジトリについて

パッケージの zip は、GitHub の Releases から配信しています。ダウンロード数（毎日更新）は、紹介ページに表示しています。

不具合や気になる点があれば、Issues、または下の連絡先からお知らせください。

## 連絡先・応援

- 公式サイト: https://nyaohtech.raindrop.jp/
- BOOTH: https://nyaohtech.booth.pm/
- X: https://x.com/nyaoh_tech
- FANBOX（月額 300 円から支援できます）: https://nyaohtech.fanbox.cc/

個人で開発しています。高品質なツールを、たくさんの方に使い続けてもらうために、FANBOX での応援をいただけると、とても励みになります。
