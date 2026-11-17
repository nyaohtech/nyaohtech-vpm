using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nyaohtech.MLVCFileView
{
    public sealed class MLVCFileViewWindow : EditorWindow
    {
        enum Tab { Files = 0, Avatar = 1, Costume = 2, Prop = 3, Gimmick = 4 }
        static readonly string[] TabNames = { "ファイル", "アバター", "衣装", "小物", "ギミック" };
        static readonly string[] SortNames = { "取り込み順", "名前", "更新日時" };

        const string PrefSize = "MLVCFileView.size";
        const int ListRowH = 22;
        const int MaxSearch = 400;
        const int PreviewCache = 96;

        [SerializeField] string _cwd = "Assets";
        [SerializeField] int _tab;
        [SerializeField] int _sort;
        [SerializeField] int _leftMode = -1;  // 0=フォルダ 1=取り込み履歴
        [SerializeField] string _query = "";
        [SerializeField] string _selected = "";

        // UI
        VisualElement _tabsEl, _tabIndicator, _crumbs, _toolbar, _leftHeader;
        readonly List<VisualElement> _tabEls = new List<VisualElement>();
        readonly List<Label> _tabCounts = new List<Label>();
        readonly List<Label> _segEls = new List<Label>();
        ListView _list, _hist, _tree;
        Label _empty, _statusText;
        VisualElement _statusActions, _sortEl, _sizeEl;
        TextField _search;
        Label _searchHint;
        SliderInt _sizeSlider;

        // データ
        List<FvEntry> _entries = new List<FvEntry>();
        List<List<FvEntry>> _rows = new List<List<FvEntry>>();
        List<string> _allPrefabs = new List<string>();
        readonly Dictionary<string, FvEntry> _entryCache = new Dictionary<string, FvEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> _rootKeys = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        int _cols = 1;
        int _sizeVal = -1;  // 0=リスト、1..80=タイル（一辺 48+値 px）
        int _linkVer = -1;
        bool _projectDirty, _needRebuild, _needRefreshItems, _treeDirty = true;
        double _lastRoleRefresh, _lastPreviewPoll;

        // フォルダツリー
        sealed class Node { public string Path; public int Depth; public bool HasKids; }
        readonly List<Node> _nodes = new List<Node>();
        readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assets" };
        readonly Dictionary<string, bool> _kids = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Unity プレビューの待ち（生成は非同期なので、出来上がったら見えている行だけ再描画）
        readonly Dictionary<string, int> _previewWait = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 検索
        CancellationTokenSource _cts;
        volatile object _searchDone;
        List<FvEntry> _searchResult;
        bool _searching;

        bool ListMode => _sizeVal <= 0;
        int TileSize => 48 + Mathf.Clamp(_sizeVal, 1, 80);
        int CardW => TileSize + 16;
        int RowH => TileSize + 38;

        [MenuItem("nyaohtech/MLVCFileView", false, 200)]
        public static void Open()
        {
            var w = GetWindow<MLVCFileViewWindow>();
            w.titleContent = new GUIContent("MLVC FileView");
            w.minSize = new Vector2(320, 120);
            w.Show();
        }

        // ───────────────────────── ライフサイクル ─────────────────────────
        void OnEnable()
        {
            FvPaths.Init();
            EditorApplication.update += OnUpdate;
            EditorApplication.projectChanged += OnProjectChanged;
            FvTouched.Changed += OnTouchedChanged;
            AssetPreview.SetPreviewTextureCacheSize(PreviewCache);
        }

        void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            EditorApplication.projectChanged -= OnProjectChanged;
            FvTouched.Changed -= OnTouchedChanged;
            _cts?.Cancel();
            FvLink.ClearThumbs();
        }

        void OnProjectChanged() { _projectDirty = true; }
        void OnTouchedChanged() { _needRefreshItems = true; }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.AddToClassList("fv-root");
            var ss = LoadStyleSheet();
            if (ss != null) root.styleSheets.Add(ss);

            if (_sizeVal < 0)
                _sizeVal = EditorPrefs.HasKey(PrefSize) ? EditorPrefs.GetInt(PrefSize) : (position.height < 280 ? 0 : 24);
            if (_leftMode < 0) _leftMode = FvLink.Available ? 1 : 0;

            BuildToolbar(root);

            var split = new TwoPaneSplitView(0, 200, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("fv-split");
            split.Add(BuildLeft());
            split.Add(BuildMain());
            root.Add(split);

            BuildStatus(root);
            BuildTip(root);

            root.RegisterCallback<GeometryChangedEvent>(_ => ApplyResponsive());

            if (!FvTouched.Baselined) AskBaseline();
            RefreshPrefabList();
            ExpandTo(_cwd);
            Rebuild();
        }

        static StyleSheet LoadStyleSheet()
        {
            var ss = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.nyaohtech.mlvcfileview/Editor/Styles/MLVCFileView.uss");
            if (ss != null) return ss;
            foreach (var g in AssetDatabase.FindAssets("MLVCFileView t:StyleSheet"))
            {
                ss = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(g));
                if (ss != null) return ss;
            }
            return null;
        }

        void AskBaseline()
        {
            EditorApplication.delayCall += () =>
            {
                if (FvTouched.Baselined) return;
                bool yes = EditorUtility.DisplayDialog("MLVC FileView",
                    "今ある Assets 直下のフォルダをすべて「触れた」扱いにしますか？\n\n" +
                    "「はい」: これから増えたフォルダだけが未開封としてハイライトされます。\n" +
                    "「いいえ」: 今あるフォルダも、開くまで未開封として表示されます。",
                    "はい（既存は既読）", "いいえ");
                if (yes) FvTouched.MarkAllExisting(); else FvTouched.SetBaselined();
            };
        }

        // ───────────────────────── ツールバー（1 行） ─────────────────────────
        static Label Btn(string text, Action click, string tip = null)
        {
            var b = new Label(text); b.AddToClassList("fv-btn");
            if (tip != null) b.tooltip = tip;
            b.RegisterCallback<ClickEvent>(_ => click());
            return b;
        }

        void BuildToolbar(VisualElement root)
        {
            _toolbar = new VisualElement(); _toolbar.AddToClassList("fv-toolbar");

            _tabsEl = new VisualElement(); _tabsEl.AddToClassList("fv-tabs");
            for (int i = 0; i < TabNames.Length; i++)
            {
                int idx = i;
                var t = new VisualElement(); t.AddToClassList("fv-tab");
                t.Add(new Label(TabNames[i]));
                var c = new Label(""); c.AddToClassList("fv-tab-count"); c.style.display = DisplayStyle.None;
                t.Add(c);
                t.RegisterCallback<ClickEvent>(_ => SetTab(idx));
                t.RegisterCallback<GeometryChangedEvent>(_ => { if (idx == _tab) MoveIndicator(); });
                _tabsEl.Add(t); _tabEls.Add(t); _tabCounts.Add(c);
            }
            _tabIndicator = new VisualElement(); _tabIndicator.AddToClassList("fv-tab-indicator");
            _tabIndicator.pickingMode = PickingMode.Ignore;
            _tabsEl.Add(_tabIndicator);
            _toolbar.Add(_tabsEl);
            ApplyTabClasses();

            var sp = new VisualElement(); sp.AddToClassList("fv-spacer"); _toolbar.Add(sp);

            // 検索（ヒント文字は Label を重ねる。2022.3 には placeholder が無いため）
            var searchWrap = new VisualElement(); searchWrap.AddToClassList("fv-search-wrap");
            _search = new TextField { value = _query };
            _search.AddToClassList("fv-search");
            _searchHint = new Label("検索"); _searchHint.AddToClassList("fv-search-hint");
            _searchHint.pickingMode = PickingMode.Ignore;
            _searchHint.style.display = _query.Length > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _search.RegisterValueChangedCallback(e =>
            {
                _query = e.newValue ?? "";
                _searchHint.style.display = _query.Length > 0 ? DisplayStyle.None : DisplayStyle.Flex;
                _search.schedule.Execute(OnQueryChanged).StartingIn(220);
            });
            searchWrap.Add(_search); searchWrap.Add(_searchHint);
            _toolbar.Add(searchWrap);

            var sort = new DropdownField(SortNames.ToList(), Mathf.Clamp(_sort, 0, 2));
            sort.AddToClassList("fv-sort");
            sort.RegisterValueChangedCallback(_ => { _sort = sort.index; _treeDirty = true; Rebuild(); });
            _sortEl = sort;
            _toolbar.Add(sort);

            // Unity の Project ウィンドウと同じ: 左端でリスト、右へ行くほどタイルが大きい
            _sizeSlider = new SliderInt(0, 80) { value = Mathf.Clamp(_sizeVal, 0, 80) };
            _sizeSlider.AddToClassList("fv-size");
            _sizeSlider.tooltip = "表示サイズ（左端でリスト表示）";
            _sizeSlider.RegisterValueChangedCallback(e =>
            {
                _sizeVal = e.newValue;
                EditorPrefs.SetInt(PrefSize, _sizeVal);
                RebuildListMode();
            });
            _sizeEl = _sizeSlider;
            _toolbar.Add(_sizeSlider);

            _toolbar.Add(Btn("更新", () => { RefreshPrefabList(); FvLink.Refresh(true); _treeDirty = true; Rebuild(); }, "一覧を読み直す"));
            _toolbar.Add(Btn("…", ShowMenu, "メニュー"));
            root.Add(_toolbar);
        }

        void ApplyResponsive()
        {
            float w = rootVisualElement.resolvedStyle.width;
            if (float.IsNaN(w)) return;
            // 細い状態では優先度の低いものから隠す
            _sizeEl.style.display = w < 560 ? DisplayStyle.None : DisplayStyle.Flex;
            _sortEl.style.display = w < 470 ? DisplayStyle.None : DisplayStyle.Flex;
            MoveIndicator();
            _list?.schedule.Execute(RelayoutIfNeeded).StartingIn(40);
        }

        void ShowMenu()
        {
            var m = new GenericMenu();
            m.AddItem(new GUIContent("今ある Assets 直下を既読にする"), false, FvTouched.MarkAllExisting);
            m.AddItem(new GUIContent("役割の解析をやり直す"), false, () => { FvClassifier.ResetGuids(); RefreshPrefabList(); Rebuild(); });
            m.AddItem(new GUIContent("MLVC 連携データの場所を開く"), false, () =>
            {
                if (Directory.Exists(FvLink.Dir)) EditorUtility.RevealInFinder(FvLink.JsonPath);
                else EditorUtility.DisplayDialog("MLVC FileView", "連携データ(.mlvc)はまだありません。\nMLVC または VLA でアイテムを取り込むと作られます。", "OK");
            });
            m.ShowAsContext();
        }

        void ApplyTabClasses()
        {
            for (int i = 0; i < _tabEls.Count; i++) _tabEls[i].EnableInClassList("fv-tab--on", i == _tab);
            MoveIndicator();
        }

        void MoveIndicator()
        {
            if (_tabIndicator == null || _tab >= _tabEls.Count) return;
            var r = _tabEls[_tab].layout;
            if (float.IsNaN(r.width) || r.width <= 0) return;
            _tabIndicator.style.left = r.x + 8;
            _tabIndicator.style.width = Mathf.Max(8, r.width - 16);
        }

        void SetTab(int i)
        {
            if (_tab == i) return;
            _tab = i;
            ApplyTabClasses();
            Rebuild();
        }

        // ───────────────────────── 左ペイン（フォルダ / 取り込み履歴） ─────────────────────────
        VisualElement BuildLeft()
        {
            var left = new VisualElement(); left.AddToClassList("fv-left");
            _leftHeader = new VisualElement(); _leftHeader.AddToClassList("fv-left-header");
            string[] names = { "フォルダ", "取り込み" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var s = new Label(names[i]); s.AddToClassList("fv-seg");
                s.RegisterCallback<ClickEvent>(_ => SetLeftMode(idx));
                _leftHeader.Add(s); _segEls.Add(s);
            }
            left.Add(_leftHeader);

            _tree = new ListView
            {
                fixedItemHeight = ListRowH,
                selectionType = SelectionType.None,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                makeItem = MakeTreeRow,
                bindItem = BindTreeRow,
                showBorder = false
            };
            _tree.AddToClassList("fv-list"); _tree.style.flexGrow = 1;
            left.Add(_tree);

            _hist = new ListView
            {
                fixedItemHeight = 44,
                selectionType = SelectionType.None,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                makeItem = MakeHistRow,
                bindItem = BindHistRow,
                showBorder = false
            };
            _hist.AddToClassList("fv-list"); _hist.style.flexGrow = 1;
            left.Add(_hist);
            ApplyLeftMode();
            return left;
        }

        void SetLeftMode(int m) { _leftMode = m; ApplyLeftMode(); if (m == 0) _treeDirty = true; Rebuild(); }

        void ApplyLeftMode()
        {
            for (int i = 0; i < _segEls.Count; i++) _segEls[i].EnableInClassList("fv-seg--on", i == _leftMode);
            _tree.style.display = _leftMode == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _hist.style.display = _leftMode == 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // フォルダツリー（開いている枝だけ列挙）
        VisualElement MakeTreeRow()
        {
            var row = new VisualElement(); row.AddToClassList("fv-trow");
            var arrow = new Label(); arrow.AddToClassList("fv-tarrow"); arrow.name = "arrow";
            var icon = new VisualElement(); icon.AddToClassList("fv-ticon"); icon.name = "icon";
            var name = new Label(); name.AddToClassList("fv-tname"); name.name = "name";
            var dot = new VisualElement(); dot.AddToClassList("fv-tdot"); dot.name = "dot";
            row.Add(arrow); row.Add(icon); row.Add(name); row.Add(dot);
            arrow.RegisterCallback<ClickEvent>(e =>
            {
                if (row.userData is Node n && n.HasKids) { ToggleNode(n.Path); e.StopPropagation(); }
            });
            row.RegisterCallback<ClickEvent>(e =>
            {
                if (!(row.userData is Node n)) return;
                bool selected = _tab == (int)Tab.Files && string.Equals(n.Path, _cwd, StringComparison.OrdinalIgnoreCase);
                if (n.HasKids)
                {
                    if (selected && _expanded.Contains(n.Path)) _expanded.Remove(n.Path); else _expanded.Add(n.Path);
                    _treeDirty = true;
                }
                NavigateTo(n.Path, expandSelf: false);
            });
            HookTip(row, v =>
            {
                if (!(v.userData is Node n)) return (null, null);
                var lines = new List<string> { n.Path };
                AddSourceLines(lines, n.Depth == 1 ? n.Path : "");
                return (FvScanner.Clean(Path.GetFileName(n.Path)), FvScanner.Clean(string.Join("\n", lines)));
            });
            return row;
        }

        void BindTreeRow(VisualElement row, int i)
        {
            if (i < 0 || i >= _nodes.Count) return;
            var n = _nodes[i];
            row.userData = n;
            row.style.paddingLeft = 4 + n.Depth * 12;
            var arrow = row.Q<Label>("arrow");
            arrow.text = n.HasKids ? (_expanded.Contains(n.Path) ? "▾" : "▸") : "";
            row.Q<Label>("name").text = FvScanner.Clean(Path.GetFileName(n.Path));
            SetBackground(row.Q("icon"), IconFor(n.Path));
            row.EnableInClassList("fv-trow--sel", _tab == (int)Tab.Files && string.Equals(n.Path, _cwd, StringComparison.OrdinalIgnoreCase));
            string root = FvPaths.RootOrSelf(n.Path);
            row.Q("dot").style.display = n.Depth == 1 && root.Length > 0 && !FvTouched.IsTouched(root) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ToggleNode(string path)
        {
            if (!_expanded.Remove(path)) _expanded.Add(path);
            _treeDirty = true; BuildTree();
        }

        void ExpandTo(string path, bool includeSelf = true)
        {
            var seg = FvPaths.Norm(path).Split('/');
            string acc = "";
            int n = includeSelf ? seg.Length : seg.Length - 1;
            for (int i = 0; i < n; i++) { acc = i == 0 ? seg[0] : acc + "/" + seg[i]; _expanded.Add(acc); }
            _treeDirty = true;
        }

        void BuildTree()
        {
            if (_tree == null) return;
            _treeDirty = false;
            _nodes.Clear();
            AddNode("Assets", 0);
            _tree.itemsSource = _nodes;
            _tree.Rebuild();
        }

        void AddNode(string path, int depth)
        {
            if (!_kids.TryGetValue(path, out bool kids)) { kids = FvScanner.HasDirs(path); _kids[path] = kids; }
            _nodes.Add(new Node { Path = path, Depth = depth, HasKids = kids });
            if (!kids || !_expanded.Contains(path)) return;
            var dirs = FvScanner.ListDirs(path);
            if (depth == 0) dirs = SortRoots(dirs);  // 取り込み順などはこのリスト（Assets 直下）に効かせる
            foreach (var d in dirs) AddNode(d, depth + 1);
        }

        List<string> SortRoots(List<string> dirs)
        {
            switch ((FvSort)_sort)
            {
                case FvSort.Name:
                    return dirs;  // ListDirs が名前順
                case FvSort.Modified:
                    return dirs.OrderByDescending(d => { try { return Directory.GetLastWriteTime(FvPaths.Abs(d)); } catch { return DateTime.MinValue; } }).ToList();
                default:
                    return dirs.OrderByDescending(RootKey).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // 取り込み履歴（サムネイル付き）
        VisualElement MakeHistRow()
        {
            var row = new VisualElement(); row.AddToClassList("fv-hist-row");
            var th = new VisualElement(); th.AddToClassList("fv-hist-thumb"); th.name = "th";
            var tx = new VisualElement(); tx.AddToClassList("fv-hist-text");
            var n = new Label(); n.AddToClassList("fv-hist-name"); n.name = "n";
            var s = new Label(); s.AddToClassList("fv-hist-sub"); s.name = "s";
            tx.Add(n); tx.Add(s);
            row.Add(th); row.Add(tx);
            row.RegisterCallback<ClickEvent>(_ => { if (row.userData is FvLinkItem it) GoToItem(it); });
            HookTip(row, v =>
            {
                if (!(v.userData is FvLinkItem it)) return (null, null);
                var lines = new List<string>();
                if (it.Author.Length > 0) lines.Add(it.Author);
                if (it.ImportedAt != DateTime.MinValue) lines.Add("取り込み日時 " + it.ImportedAt.ToString("yyyy/MM/dd HH:mm"));
                lines.Add(it.Roots.Length > 0 ? "展開先 " + string.Join(", ", it.Roots) : "展開先は不明");
                lines.Add("クリックでフォルダを開く");
                return (FvScanner.Clean(it.Name.Length > 0 ? it.Name : it.Id), FvScanner.Clean(string.Join("\n", lines)));
            });
            return row;
        }

        void BindHistRow(VisualElement row, int i)
        {
            var items = FvLink.Items;
            if (i < 0 || i >= items.Count) return;
            var it = items[i];
            row.userData = it;
            row.EnableInClassList("fv-hist-row--dim", it.Roots.Length == 0);
            string nm = FvScanner.Clean(it.Name.Length > 0 ? it.Name : it.Id);
            row.Q<Label>("n").text = nm;
            string when = it.ImportedAt == DateTime.MinValue ? "" : Ago(it.ImportedAt);
            row.Q<Label>("s").text = (it.Author.Length > 0 ? FvScanner.Clean(it.Author) + " · " : "") + when;
            SetBackground(row.Q("th"), FvLink.GetThumb(it));
        }

        // ───────────────────────── 右ペイン（内容） ─────────────────────────
        VisualElement BuildMain()
        {
            var main = new VisualElement(); main.AddToClassList("fv-main");
            _crumbs = new VisualElement(); _crumbs.AddToClassList("fv-crumbs");
            main.Add(_crumbs);

            _list = CreateList();
            main.Add(_list);

            _empty = new Label(""); _empty.AddToClassList("fv-empty"); _empty.pickingMode = PickingMode.Ignore;
            _empty.style.display = DisplayStyle.None;
            main.Add(_empty);
            return main;
        }

        void BuildStatus(VisualElement root)
        {
            var bar = new VisualElement(); bar.AddToClassList("fv-status");
            _statusText = new Label(); _statusText.AddToClassList("fv-status-text");
            _statusActions = new VisualElement(); _statusActions.AddToClassList("fv-status-actions");
            bar.Add(_statusText); bar.Add(_statusActions);
            root.Add(bar);
        }

        // ───────── 即時ツールチップ（Unity 標準のは出るまで遅いので自前） ─────────
        VisualElement _tip;
        Label _tipTitle, _tipBody;

        void BuildTip(VisualElement root)
        {
            _tip = new VisualElement(); _tip.AddToClassList("fv-tip");
            _tip.pickingMode = PickingMode.Ignore;
            _tipTitle = new Label(); _tipTitle.AddToClassList("fv-tip-title"); _tipTitle.pickingMode = PickingMode.Ignore;
            _tipBody = new Label(); _tipBody.AddToClassList("fv-tip-body"); _tipBody.pickingMode = PickingMode.Ignore;
            _tip.Add(_tipTitle); _tip.Add(_tipBody);
            _tip.style.display = DisplayStyle.None;
            root.Add(_tip);
        }

        void HideTip() { if (_tip != null) _tip.style.display = DisplayStyle.None; }

        void MoveTip(Vector2 mouse)
        {
            var r = rootVisualElement.layout;
            float w = _tip.resolvedStyle.width, h = _tip.resolvedStyle.height;
            if (float.IsNaN(w)) w = 240;
            if (float.IsNaN(h)) h = 60;
            float x = mouse.x + 14, y = mouse.y + 18;
            if (x + w > r.width - 4) x = Mathf.Max(4, mouse.x - w - 8);
            if (y + h > r.height - 4) y = Mathf.Max(4, mouse.y - h - 8);
            _tip.style.left = x; _tip.style.top = y;
        }

        // 要素にマウスが乗った瞬間にツールチップを出す。getter は (見出し, 本文) を返す。
        void HookTip(VisualElement ve, Func<VisualElement, (string, string)> getter)
        {
            ve.RegisterCallback<PointerEnterEvent>(e =>
            {
                var (t, b) = getter(ve);
                if (string.IsNullOrEmpty(t)) { HideTip(); return; }
                _tipTitle.text = t; _tipBody.text = b;
                _tipBody.style.display = string.IsNullOrEmpty(b) ? DisplayStyle.None : DisplayStyle.Flex;
                _tip.style.display = DisplayStyle.Flex;
                MoveTip(e.position);
            });
            ve.RegisterCallback<PointerMoveEvent>(e => { if (_tip.style.display == DisplayStyle.Flex) MoveTip(e.position); });
            ve.RegisterCallback<PointerLeaveEvent>(_ => HideTip());
            ve.RegisterCallback<PointerDownEvent>(_ => HideTip());
        }

        (string, string) EntryTip(VisualElement ve)
        {
            if (!(ve.userData is FvEntry e)) return (null, null);
            var lines = new List<string>();
            lines.Add(e.Path);
            string kind = FvScanner.KindLabel(e.Kind);
            if (e.Role != FvRole.None) kind += " / " + FvScanner.RoleLabel(e.Role);
            if (!e.IsFolder) kind += "   " + FormatSize(e.Size);
            lines.Add(kind);
            lines.Add("更新 " + e.ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm"));
            AddSourceLines(lines, FvPaths.RootOrSelf(e.Path));
            return (FvScanner.Clean(e.Name), FvScanner.Clean(string.Join("\n", lines)));
        }

        static void AddSourceLines(List<string> lines, string root)
        {
            var link = FvLink.ItemForRoot(root);
            if (link == null) return;
            lines.Add("取り込み元 " + link.Name + (link.Author.Length > 0 ? "（" + link.Author + "）" : ""));
            if (link.ImportedAt != DateTime.MinValue) lines.Add("取り込み日時 " + link.ImportedAt.ToString("yyyy/MM/dd HH:mm"));
        }

        // ───────── タイル / リストの行 ─────────
        int _builtKey = -2;

        // 行の作り方・高さを最初から決めた ListView
        ListView CreateList()
        {
            var lv = new ListView
            {
                selectionType = SelectionType.None,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                showBorder = false,
                fixedItemHeight = ListMode ? ListRowH : RowH,
                makeItem = ListMode ? (Func<VisualElement>)MakeListRow : MakeRow,
                bindItem = ListMode ? (Action<VisualElement, int>)BindListRow : BindRow
            };
            lv.AddToClassList("fv-list");
            lv.style.flexGrow = 1;
            lv.RegisterCallback<GeometryChangedEvent>(_ => lv.schedule.Execute(RelayoutIfNeeded).StartingIn(40));
            return lv;
        }

        void ConfigureList()
        {
            int key = ListMode ? 0 : TileSize;
            if (key == _builtKey) return;
            _builtKey = key;
            var parent = _list.parent;
            int idx = parent.IndexOf(_list);
            parent.Remove(_list);
            _list = CreateList();
            parent.Insert(idx, _list);
        }

        void RebuildListMode() { _cols = -1; Relayout(); }

        VisualElement MakeRow()
        {
            var row = new VisualElement(); row.AddToClassList("fv-row");
            return row;
        }

        VisualElement MakeCard()
        {
            int s = TileSize;
            var card = new VisualElement(); card.AddToClassList("fv-card");
            card.style.width = s + 10; card.style.height = s + 32;
            var wrap = new VisualElement(); wrap.AddToClassList("fv-thumb-wrap");
            wrap.style.height = s; wrap.pickingMode = PickingMode.Ignore;
            var th = new VisualElement(); th.AddToClassList("fv-thumb"); th.name = "thumb";
            th.pickingMode = PickingMode.Ignore; th.style.flexGrow = 1;
            wrap.Add(th);
            var badges = new VisualElement(); badges.AddToClassList("fv-badges"); badges.name = "badges";
            badges.pickingMode = PickingMode.Ignore;
            wrap.Add(badges);
            card.Add(wrap);
            var n = new Label(); n.AddToClassList("fv-card-name"); n.name = "name"; n.pickingMode = PickingMode.Ignore;
            var sub = new Label(); sub.AddToClassList("fv-card-sub"); sub.name = "sub"; sub.pickingMode = PickingMode.Ignore;
            sub.style.display = s >= 72 ? DisplayStyle.Flex : DisplayStyle.None;
            card.style.height = s + (s >= 72 ? 44 : 28);
            card.Add(n); card.Add(sub);
            WireInteractions(card);
            return card;
        }

        VisualElement MakeListRow()
        {
            var row = new VisualElement(); row.AddToClassList("fv-lrow");
            var icon = new VisualElement(); icon.AddToClassList("fv-licon"); icon.name = "icon"; icon.pickingMode = PickingMode.Ignore;
            var name = new Label(); name.AddToClassList("fv-lname"); name.name = "name"; name.pickingMode = PickingMode.Ignore;
            var chip = new Label(); chip.AddToClassList("fv-badge"); chip.name = "chip"; chip.pickingMode = PickingMode.Ignore;
            var kind = new Label(); kind.AddToClassList("fv-lcol"); kind.AddToClassList("fv-lcol--kind"); kind.name = "kind"; kind.pickingMode = PickingMode.Ignore;
            var size = new Label(); size.AddToClassList("fv-lcol"); size.AddToClassList("fv-lcol--size"); size.name = "size"; size.pickingMode = PickingMode.Ignore;
            var date = new Label(); date.AddToClassList("fv-lcol"); date.AddToClassList("fv-lcol--date"); date.name = "date"; date.pickingMode = PickingMode.Ignore;
            row.Add(icon); row.Add(name); row.Add(chip); row.Add(kind); row.Add(size); row.Add(date);
            WireInteractions(row);
            return row;
        }

        void WireInteractions(VisualElement ve)
        {
            HookTip(ve, EntryTip);
            Vector2 down = Vector2.zero; bool pressed = false;
            ve.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) { pressed = true; down = e.position; } });
            ve.RegisterCallback<PointerUpEvent>(_ => pressed = false);
            ve.RegisterCallback<PointerLeaveEvent>(_ => pressed = false);
            ve.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!pressed || (e.pressedButtons & 1) == 0) return;
                if (((Vector2)e.position - down).sqrMagnitude < 64f) return;
                pressed = false;
                if (ve.userData is FvEntry en) StartDrag(en);
            });
            ve.RegisterCallback<ClickEvent>(e =>
            {
                if (!(ve.userData is FvEntry en)) return;
                if (en.IsFolder)
                {
                    // 開いた直後に同じ位置へ来た 2 回目のクリック（ダブルクリックの後半）で、中のフォルダまで開かないようにする
                    if (e.clickCount >= 2 && EditorApplication.timeSinceStartup - _lastNavTime < 0.6) return;
                    Activate(en);
                    return;
                }
                if (e.clickCount >= 2) Activate(en); else Select(en);
            });
            ve.AddManipulator(new ContextualMenuManipulator(e =>
            {
                if (!(ve.userData is FvEntry en)) return;
                Select(en);
                e.menu.AppendAction("開く", _ => Activate(en));
                e.menu.AppendAction("Project ウィンドウで表示", _ => Ping(en));
                e.menu.AppendAction("エクスプローラーで表示", _ => EditorUtility.RevealInFinder(FvPaths.Abs(en.Path)));
                e.menu.AppendAction("パスをコピー", _ => EditorGUIUtility.systemCopyBuffer = en.Path);
                string root = FvPaths.RootOrSelf(en.Path);
                if (root.Length > 0)
                {
                    e.menu.AppendSeparator();
                    if (FvTouched.IsTouched(root)) e.menu.AppendAction("未開封に戻す（" + root + "）", _ => FvTouched.Unmark(root));
                    else e.menu.AppendAction("既読にする（" + root + "）", _ => FvTouched.Mark(root));
                }
            }));
        }

        void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            var data = _rows[index];
            while (row.childCount < data.Count) row.Add(MakeCard());
            for (int i = 0; i < row.childCount; i++)
            {
                var card = row[i];
                if (i >= data.Count) { card.style.display = DisplayStyle.None; card.userData = null; continue; }
                card.style.display = DisplayStyle.Flex;
                BindCard(card, data[i]);
            }
        }

        bool IsTopLevel => _tab == (int)Tab.Files && _cwd == "Assets" && _query.Trim().Length == 0;

        bool IsRootEntry(FvEntry e) => e.IsFolder && FvPaths.Norm(e.Path).Count(c => c == '/') == 1;

        bool IsNewRoot(FvEntry e)
        {
            if (!(IsRootEntry(e) || _tab != (int)Tab.Files || _query.Trim().Length > 0)) return false;
            string root = FvPaths.RootOrSelf(e.Path);
            return root.Length > 0 && !FvTouched.IsTouched(root);
        }

        void BindCard(VisualElement card, FvEntry e)
        {
            card.userData = e;
            card.EnableInClassList("fv-card--sel", string.Equals(e.Path, _selected, StringComparison.OrdinalIgnoreCase));
            bool isNew = IsNewRoot(e);
            card.EnableInClassList("fv-card--new", isNew);

            card.Q<Label>("name").text = FvScanner.Clean(e.Name);
            var item = ThumbItem(e);
            string sub = FvScanner.KindLabel(e.Kind);
            if (e.Kind == FvKind.Prefab && e.Role != FvRole.None) sub = FvScanner.RoleLabel(e.Role);
            if (item != null && item.Author.Length > 0) sub = FvScanner.Clean(item.Author);
            card.Q<Label>("sub").text = sub;

            ApplyVisual(card.Q("thumb"), e, item);

            var badges = card.Q("badges");
            badges.Clear();
            if (isNew) badges.Add(Badge("未開封", "fv-badge--new"));
            if (e.Kind == FvKind.Prefab && e.Role != FvRole.None)
                badges.Add(Badge(FvScanner.RoleLabel(e.Role), "fv-badge--" + e.Role.ToString().ToLowerInvariant()));
        }

        void BindListRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _entries.Count) return;
            var e = _entries[index];
            row.userData = e;
            row.EnableInClassList("fv-lrow--sel", string.Equals(e.Path, _selected, StringComparison.OrdinalIgnoreCase));
            row.EnableInClassList("fv-lrow--new", IsNewRoot(e));
            row.Q<Label>("name").text = FvScanner.Clean(e.Name);
            SetBackground(row.Q("icon"), IconFor(e.Path));
            var chip = row.Q<Label>("chip");
            string c = e.Kind == FvKind.Prefab && e.Role != FvRole.None ? FvScanner.RoleLabel(e.Role) : (IsNewRoot(e) ? "未開封" : "");
            chip.text = c;
            chip.style.display = c.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var cls in new[] { "fv-badge--new", "fv-badge--avatar", "fv-badge--costume", "fv-badge--prop", "fv-badge--gimmick" }) chip.RemoveFromClassList(cls);
            if (c == "未開封") chip.AddToClassList("fv-badge--new");
            else if (c.Length > 0) chip.AddToClassList("fv-badge--" + e.Role.ToString().ToLowerInvariant());
            row.Q<Label>("kind").text = FvScanner.KindLabel(e.Kind);
            row.Q<Label>("size").text = e.IsFolder ? "" : FormatSize(e.Size);
            row.Q<Label>("date").text = e.ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        }

        static Label Badge(string text, string cls)
        {
            var b = new Label(text); b.AddToClassList("fv-badge"); b.AddToClassList(cls);
            return b;
        }

        static void SetBackground(VisualElement ve, Texture tex)
        {
            var t2 = tex as Texture2D;
            ve.style.backgroundImage = t2 != null ? new StyleBackground(Background.FromTexture2D(t2)) : new StyleBackground(StyleKeyword.None);
        }

        static Texture IconFor(string path)
        {
            var t = AssetDatabase.GetCachedIcon(path);
            return t != null ? t : EditorGUIUtility.IconContent("DefaultAsset Icon").image;
        }

        // ───────── サムネイルの出し分け ─────────
        FvLinkItem ThumbItem(FvEntry e)
        {
            if (!FvLink.Available || !IsTopLevel) return null;
            if (!IsRootEntry(e)) return null;
            return FvLink.ItemForRoot(FvPaths.RootOrSelf(e.Path));
        }

        static bool WantsPreview(FvKind k) =>
            k == FvKind.Prefab || k == FvKind.Model || k == FvKind.Texture || k == FvKind.Material;

        void ApplyVisual(VisualElement th, FvEntry e, FvLinkItem item)
        {
            // ① 最上位: MLVC/VLA の画像
            var link = item != null ? FvLink.GetThumb(item) : null;
            if (link != null)
            {
                th.RemoveFromClassList("fv-thumb--icon");
                SetBackground(th, link);
                return;
            }
            // ② 中身: Unity 標準のプレビュー（生成は非同期。出来るまでは型アイコン）
            th.AddToClassList("fv-thumb--icon");
            if (!e.IsFolder && WantsPreview(e.Kind))
            {
                var pv = GetPreview(e.Path);
                if (pv != null) { SetBackground(th, pv); return; }
            }
            SetBackground(th, IconFor(e.Path));
        }

        // Unity 標準のプレビュー。巨大ファイル（64MB 超）は読み込みが重いので対象外にして型アイコンのままにする。
        static Texture2D TryPreview(string path)
        {
            try { if (new FileInfo(FvPaths.Abs(path)).Length > 64L * 1024 * 1024) return null; } catch { return null; }
            var o = AssetDatabase.LoadMainAssetAtPath(path);
            return o != null ? AssetPreview.GetAssetPreview(o) : null;
        }

        Texture2D GetPreview(string path)
        {
            var pv = TryPreview(path);
            if (pv != null) { _previewWait.Remove(path); return pv; }
            if (!_previewWait.TryGetValue(path, out int n)) _previewWait[path] = 0;  // 出来たら OnUpdate が再描画する
            return null;
        }

        // ───────────────────────── 操作 ─────────────────────────
        void Select(FvEntry e)
        {
            _selected = e.Path;
            FvTouched.Mark(e.Path);
            _list.RefreshItems();
            UpdateStatus();
        }

        void Activate(FvEntry e)
        {
            FvTouched.Mark(e.Path);
            if (e.IsFolder) { NavigateTo(e.Path); return; }
            var o = AssetDatabase.LoadMainAssetAtPath(e.Path);
            if (o != null) AssetDatabase.OpenAsset(o);
        }

        void Ping(FvEntry e)
        {
            var o = AssetDatabase.LoadMainAssetAtPath(e.Path);
            if (o != null) { Selection.activeObject = o; EditorGUIUtility.PingObject(o); }
        }

        void StartDrag(FvEntry e)
        {
            var o = AssetDatabase.LoadMainAssetAtPath(e.Path);
            if (o == null) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.paths = new[] { e.Path };
            DragAndDrop.objectReferences = new[] { o };
            DragAndDrop.StartDrag(e.Name);
        }

        double _lastNavTime;

        void NavigateTo(string path, bool expandSelf = true)
        {
            _lastNavTime = EditorApplication.timeSinceStartup;
            _tab = (int)Tab.Files;
            ApplyTabClasses();
            _cwd = FvPaths.Norm(path);
            FvTouched.Mark(_cwd);
            _query = ""; _search?.SetValueWithoutNotify(""); if (_searchHint != null) _searchHint.style.display = DisplayStyle.Flex;
            _selected = "";
            _previewWait.Clear();
            ExpandTo(_cwd, expandSelf);
            Rebuild();
        }

        void GoToItem(FvLinkItem it)
        {
            if (it.Roots.Length == 0)
            {
                ShowNotification(new GUIContent("展開先フォルダを特定できませんでした（VLA ライブラリに元の unitypackage が無い場合など）"));
                return;
            }
            string target = it.Roots.FirstOrDefault(r => Directory.Exists(FvPaths.Abs(r)));
            if (target == null) { ShowNotification(new GUIContent("フォルダが見つかりません（削除済み？）: " + it.Roots[0])); return; }
            _leftMode = 0; ApplyLeftMode();
            _centerTreeOn = FvPaths.Norm(target);
            NavigateTo(target);
        }

        string _centerTreeOn;

        // ツリー内の指定フォルダが縦方向の中央に来るようにスクロールする（レイアウト確定後に実行）。
        void CenterTree(string path)
        {
            int idx = _nodes.FindIndex(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;
            _tree.schedule.Execute(() =>
            {
                var sv = _tree.Q<ScrollView>();
                if (sv == null) return;
                float view = sv.contentViewport.resolvedStyle.height;
                if (float.IsNaN(view) || view <= 0) view = _tree.resolvedStyle.height;
                float y = idx * ListRowH - (view - ListRowH) / 2f;
                sv.scrollOffset = new Vector2(0, Mathf.Max(0, y));
            }).StartingIn(60);
        }

        void OnQueryChanged()
        {
            _cts?.Cancel();
            if (_query.Trim().Length == 0) { _searching = false; _searchResult = null; Rebuild(); return; }
            if (_tab != (int)Tab.Files) { Rebuild(); return; }
            _searching = true;
            _cts = new CancellationTokenSource();
            var tok = _cts.Token; string q = _query.Trim();
            FvPaths.Init();
            Task.Run(() =>
            {
                var r = FvScanner.Search(q, MaxSearch, tok);
                if (!tok.IsCancellationRequested) _searchDone = new KeyValuePair<string, List<FvEntry>>(q, r);
            });
            UpdateStatus();
        }

        // ───────────────────────── データ構築 ─────────────────────────
        void RefreshPrefabList()
        {
            _entryCache.Clear(); _rootKeys.Clear(); _kids.Clear();
            _allPrefabs = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.Length > 0).ToList();
            FvClassifier.Begin(_allPrefabs);
            _treeDirty = true;
        }

        void Rebuild()
        {
            if (_list == null) return;
            _needRebuild = false;
            _rootKeys.Clear();
            HideTip();
            List<FvEntry> list;

            if (_tab == (int)Tab.Files)
            {
                if (_query.Trim().Length > 0) list = _searchResult ?? new List<FvEntry>();
                else
                {
                    if (!Directory.Exists(FvPaths.Abs(_cwd))) _cwd = "Assets";
                    list = FvScanner.ListFolder(_cwd);
                    foreach (var e in list) if (e.Kind == FvKind.Prefab) e.Role = FvClassifier.RoleOf(e.Path);
                }
            }
            else
            {
                var want = (FvRole)_tab;
                list = new List<FvEntry>();
                foreach (var p in _allPrefabs)
                {
                    if (FvClassifier.RoleOf(p) != want) continue;
                    list.Add(EntryFor(p, want));
                }
                string q = _query.Trim();
                if (q.Length > 0) list = list.Where(e => e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            bool byRoot = _tab != (int)Tab.Files || _cwd == "Assets" || _query.Trim().Length > 0;
            foreach (var e in list) e.OrderKey = byRoot ? KeyOf(e) : e.CreatedUtc.ToLocalTime();

            switch ((FvSort)_sort)
            {
                case FvSort.Name:
                    list = list.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
                case FvSort.Modified:
                    list = list.OrderByDescending(e => e.ModifiedUtc).ToList(); break;
                default:
                    list = byRoot
                        ? list.OrderByDescending(e => e.OrderKey).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
                        : list.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    break;
            }
            _entries = list;
            _cols = -1;
            Relayout();
            BuildCrumbs();
            UpdateTabCounts();
            if (_treeDirty) BuildTree(); else _tree.RefreshItems();
            if (_centerTreeOn != null) { CenterTree(_centerTreeOn); _centerTreeOn = null; }
            _hist.itemsSource = FvLink.Items as System.Collections.IList ?? FvLink.Items.ToList();
            _hist.Rebuild();
            UpdateEmpty();
            UpdateStatus();
        }

        FvEntry EntryFor(string path, FvRole role)
        {
            if (!_entryCache.TryGetValue(path, out var e))
            {
                e = FvScanner.Make(path, new FileInfo(FvPaths.Abs(path)), false);
                _entryCache[path] = e;
            }
            e.Role = role;
            return e;
        }

        // 取り込み順のキー。MLVC/VLA の記録があればその取り込み日時、無ければフォルダの作成日時。
        DateTime KeyOf(FvEntry e)
        {
            string root = FvPaths.RootOrSelf(e.Path);
            return root.Length == 0 ? e.CreatedUtc.ToLocalTime() : RootKey(root);
        }

        DateTime RootKey(string root)
        {
            if (_rootKeys.TryGetValue(root, out var k)) return k;
            var item = FvLink.ItemForRoot(root);
            if (item != null && item.ImportedAt != DateTime.MinValue) k = item.ImportedAt;
            else
            {
                try { k = Directory.GetCreationTime(FvPaths.Abs(root)); } catch { k = DateTime.MinValue; }
            }
            _rootKeys[root] = k;
            return k;
        }

        void Relayout()
        {
            var sv0 = _list.Q<ScrollView>();
            var off = sv0 != null ? sv0.scrollOffset : Vector2.zero;
            ConfigureList();

            if (ListMode)
            {
                _cols = 1;
                _list.itemsSource = _entries;
            }
            else
            {
                float w = _list.resolvedStyle.width;
                if (float.IsNaN(w) || w <= 0) w = 600;
                _cols = Mathf.Max(1, Mathf.FloorToInt((w - 12) / CardW));
                _rows = new List<List<FvEntry>>();
                for (int i = 0; i < _entries.Count; i += _cols)
                    _rows.Add(_entries.GetRange(i, Math.Min(_cols, _entries.Count - i)));
                _list.itemsSource = _rows;
            }
            _list.Rebuild();
            var sv = _list.Q<ScrollView>();
            if (sv != null) _list.schedule.Execute(() => sv.scrollOffset = off);
        }

        void RelayoutIfNeeded()
        {
            if (ListMode) return;
            float w = _list.resolvedStyle.width;
            if (float.IsNaN(w) || w <= 0) return;
            int cols = Mathf.Max(1, Mathf.FloorToInt((w - 12) / CardW));
            if (cols != _cols) Relayout();
        }

        // ───────────────────────── 表示の更新 ─────────────────────────
        void BuildCrumbs()
        {
            _crumbs.Clear();
            if (_tab != (int)Tab.Files || _query.Trim().Length > 0)
            {
                var l = new Label(_query.Trim().Length > 0 ? "「" + _query.Trim() + "」の検索結果" : TabNames[_tab] + "（Assets 全体）");
                l.AddToClassList("fv-crumb"); l.AddToClassList("fv-crumb--last");
                _crumbs.Add(l);
                return;
            }
            var segs = _cwd.Split('/');
            string acc = "";
            for (int i = 0; i < segs.Length; i++)
            {
                acc = i == 0 ? segs[0] : acc + "/" + segs[i];
                string target = acc;
                var c = new Label(FvScanner.Clean(segs[i])); c.AddToClassList("fv-crumb");
                if (i == segs.Length - 1) c.AddToClassList("fv-crumb--last");
                c.RegisterCallback<ClickEvent>(_ => NavigateTo(target));
                _crumbs.Add(c);
                if (i < segs.Length - 1) { var s = new Label("›"); s.AddToClassList("fv-crumb-sep"); _crumbs.Add(s); }
            }
        }

        void UpdateTabCounts()
        {
            var counts = new int[5];
            foreach (var p in _allPrefabs)
            {
                var r = FvClassifier.RoleOf(p);
                if (r != FvRole.None) counts[(int)r]++;
            }
            for (int i = 1; i < 5; i++)
            {
                _tabCounts[i].text = counts[i].ToString();
                _tabCounts[i].style.display = counts[i] > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        void UpdateEmpty()
        {
            string msg = "";
            if (_entries.Count == 0)
            {
                if (_searching) msg = "検索中…";
                else if (_query.Trim().Length > 0) msg = "見つかりませんでした";
                else if (_tab != (int)Tab.Files)
                    msg = FvClassifier.Running ? "プレハブを解析中…" : TabNames[_tab] + "として判定されたプレハブはありません";
                else msg = "このフォルダは空です";
            }
            _empty.text = msg;
            _empty.style.display = msg.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // 下のバー: Unity の Project ウィンドウと同じく、選択中のパスと情報をここに出す。
        void UpdateStatus()
        {
            if (_statusText == null) return;
            _statusActions.Clear();
            var e = _entries.FirstOrDefault(x => string.Equals(x.Path, _selected, StringComparison.OrdinalIgnoreCase));
            if (e == null)
            {
                string s = _entries.Count + " 件";
                if (FvClassifier.Running) s += "  ·  プレハブ解析中 " + FvClassifier.Done + "/" + FvClassifier.Total;
                if (_searching) s += "  ·  検索中…";
                _statusText.text = s;
                _statusText.tooltip = "";
                return;
            }
            string info = e.Path + "   " + FvScanner.KindLabel(e.Kind);
            if (e.Role != FvRole.None) info += "/" + FvScanner.RoleLabel(e.Role);
            if (!e.IsFolder) info += "   " + FormatSize(e.Size);
            _statusText.text = FvScanner.Clean(info);
            _statusText.tooltip = e.Path;

            var link = FvLink.ItemForRoot(FvPaths.RootOrSelf(e.Path));
            if (link != null)
            {
                _statusActions.Add(new Label(FvScanner.Clean(link.Name) + (link.ImportedAt != DateTime.MinValue ? "  " + link.ImportedAt.ToString("yyyy/MM/dd") : ""))
                    { name = "src" });
                _statusActions[0].AddToClassList("fv-status-src");
                if (link.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    _statusActions.Add(Btn("商品ページ", () => Application.OpenURL(link.Url)));
            }
            _statusActions.Add(Btn("Projectで表示", () => Ping(e)));
        }

        // ───────────────────────── 毎フレーム処理 ─────────────────────────
        void OnUpdate()
        {
            if (_list == null) return;
            bool dirty = false;

            if (_projectDirty) { _projectDirty = false; RefreshPrefabList(); dirty = true; }

            var sd = _searchDone;
            if (sd != null)
            {
                _searchDone = null;
                var kv = (KeyValuePair<string, List<FvEntry>>)sd;
                if (kv.Key == _query.Trim()) { _searching = false; _searchResult = kv.Value; dirty = true; }
            }

            if (FvClassifier.Tick(5))
            {
                double now = EditorApplication.timeSinceStartup;
                if (!FvClassifier.Running || now - _lastRoleRefresh > 0.4) { _lastRoleRefresh = now; dirty = true; }
            }

            if (FvLink.Version != _linkVer) { _linkVer = FvLink.Version; _treeDirty = true; dirty = true; }
            else FvLink.Refresh(false);

            // Unity プレビューの生成待ち: 0.25 秒ごとに確認し、出来ていれば見えている行だけ再描画
            if (_previewWait.Count > 0 && EditorApplication.timeSinceStartup - _lastPreviewPoll > 0.25)
            {
                _lastPreviewPoll = EditorApplication.timeSinceStartup;
                bool ready = false;
                foreach (var key in _previewWait.Keys.ToList())
                {
                    var pv = TryPreview(key);
                    int tries = _previewWait[key] + 1;
                    if (pv != null) { ready = true; _previewWait.Remove(key); }
                    else if (tries > 40) _previewWait.Remove(key);  // 約 10 秒で諦めて型アイコンのまま
                    else _previewWait[key] = tries;
                }
                if (ready) _needRefreshItems = true;
            }

            if (_needRebuild) dirty = true;
            if (dirty) Rebuild();
            else if (_needRefreshItems) { _list.RefreshItems(); _tree.RefreshItems(); }
            _needRefreshItems = false;
        }

        // ───────────────────────── 小物 ─────────────────────────
        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "たった今";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + "分前";
            if (d.TotalDays < 1) return (int)d.TotalHours + "時間前";
            if (d.TotalDays < 30) return (int)d.TotalDays + "日前";
            return t.ToString("yyyy/MM/dd");
        }

        static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }
    }
}
