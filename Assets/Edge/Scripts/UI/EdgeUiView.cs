using System.Collections.Generic;
using UnityEngine;

namespace Edge.UI
{
    // Runtime uGUI canvas for the EDGE screens. Layouts (EdgeFeedingHud) describe the screen as a list
    // of UiNodes each frame; Apply() reuses one GameObject per node id and only writes properties that
    // changed, so a static screen costs no canvas rebuilds. Nodes missing from a frame are hidden.
    // Sprites come from EdgeUiArt (drawn in code); fonts from Resources/EdgeUI (Cinzel for titles,
    // Atkinson Hyperlegible for text, both SIL OFL), falling back to Unity's built-in font.
    // Legacy uGUI Text is used on purpose: it needs no TextMesh Pro essentials or shader setup.
    public class EdgeUiView : MonoBehaviour
    {
        public static EdgeUiView Instance { get; private set; }

        sealed class Entry
        {
            public GameObject go;
            public RectTransform rt;
            public UnityEngine.UI.Image img;
            public UnityEngine.UI.Text txt;
            public UnityEngine.UI.Shadow shadow;
            public int frame;
            public UiNode last;
            public bool fresh = true;
        }

        Canvas _canvas;
        RectTransform _root;
        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        readonly List<Entry> _all = new List<Entry>();
        readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        Font _title, _body, _bold;
        int _frame;

        public static EdgeUiView Create(int sortingOrder = 50)
        {
            var go = new GameObject("EdgeUI (runtime)", typeof(RectTransform));
            var view = go.AddComponent<EdgeUiView>();
            view.Build(sortingOrder);
            return view;
        }

        void Build(int sortingOrder)
        {
            Instance = this;
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = sortingOrder;
            var scaler = gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            scaler.referencePixelsPerUnit = 100f;
            gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();   // nothing here is clickable; every graphic has raycastTarget off

            var rootGo = new GameObject("Root", typeof(RectTransform));
            _root = (RectTransform)rootGo.transform;
            _root.SetParent(transform, false);
            _root.anchorMin = Vector2.zero; _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero; _root.offsetMax = Vector2.zero;

            Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _title = Resources.Load<Font>("EdgeUI/Cinzel-Bold") ?? builtin;
            _body = Resources.Load<Font>("EdgeUI/AtkinsonHyperlegible-Regular") ?? builtin;
            _bold = Resources.Load<Font>("EdgeUI/AtkinsonHyperlegible-Bold") ?? _body;
            if (_title == builtin) Debug.LogWarning("[EDGE] UI fonts not found in Resources/EdgeUI; using the built-in font");

            foreach (var id in EdgeUiArt.All) _sprites[id] = MakeSprite(EdgeUiArt.Make(id));
        }

        static Sprite MakeSprite(UiSpriteData d)
        {
            var tex = new Texture2D(d.w, d.h, TextureFormat.RGBA32, true)
            {
                name = "EdgeUI " + d.id, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear,
            };
            tex.SetPixels32(d.px);
            tex.Apply(true, true);
            return Sprite.Create(tex, new Rect(0, 0, d.w, d.h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, d.border);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var s in _sprites.Values) if (s) { Destroy(s.texture); Destroy(s); }
        }

        string _sub;
        float _subStart = -100f, _subUntil = -100f;

        /// Show a narrator line in the themed style (fades in 0.25 s, out over the last 0.6 s).
        public void ShowSubtitle(string text, float seconds)
        {
            _sub = text; _subStart = Time.unscaledTime; _subUntil = _subStart + Mathf.Max(0.5f, seconds);
        }

        public string Subtitle => _sub;
        public float SubtitleAlpha
        {
            get
            {
                float t = Time.unscaledTime;
                return Mathf.Clamp01(Mathf.Min((t - _subStart) / 0.25f, (_subUntil - t) / 0.6f));
            }
        }

        /// Canvas size in canvas units (layouts work in these).
        public Vector2 Size => _root ? _root.rect.size : new Vector2(1920f, 1080f);

        /// Screen pixels (origin bottom-left) -> canvas units (origin top-left).
        public Vector2 ScreenToCanvas(Vector2 screen)
        {
            float k = _canvas && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            return new Vector2(screen.x / k, Size.y - screen.y / k);
        }

        public void Apply(List<UiNode> nodes)
        {
            if (!_root) return;
            _frame++;
            int lastIndex = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (!_entries.TryGetValue(n.id, out var e) || (n.kind == UiKind.Text) != (e.txt != null))
                {
                    if (e != null) { Destroy(e.go); _all.Remove(e); }
                    e = Create(n);
                    _entries[n.id] = e;
                    _all.Add(e);
                }
                e.frame = _frame;
                if (!e.go.activeSelf) e.go.SetActive(true);
                // keep sibling order = draw order (only moves out-of-order entries)
                int idx = e.rt.GetSiblingIndex();
                if (idx < lastIndex) { e.rt.SetSiblingIndex(lastIndex); idx = lastIndex; }
                lastIndex = idx;
                Write(e, n);
            }
            foreach (var e in _all)
                if (e.frame != _frame && e.go.activeSelf) e.go.SetActive(false);
        }

        Entry Create(in UiNode n)
        {
            var go = new GameObject(n.id, typeof(RectTransform));
            var e = new Entry { go = go, rt = (RectTransform)go.transform };
            e.rt.SetParent(_root, false);
            e.rt.anchorMin = e.rt.anchorMax = new Vector2(0f, 1f);
            e.rt.pivot = new Vector2(0f, 1f);
            if (n.kind == UiKind.Text)
            {
                e.txt = go.AddComponent<UnityEngine.UI.Text>();
                e.txt.raycastTarget = false;
                e.txt.supportRichText = false;
                e.txt.horizontalOverflow = HorizontalWrapMode.Wrap;
                e.txt.verticalOverflow = VerticalWrapMode.Overflow;
                e.txt.lineSpacing = 1.05f;
                e.shadow = go.AddComponent<UnityEngine.UI.Shadow>();
                e.shadow.effectColor = new Color(0f, 0f, 0f, 0.65f);
                e.shadow.effectDistance = new Vector2(2f, -2f);
            }
            else
            {
                e.img = go.AddComponent<UnityEngine.UI.Image>();
                e.img.raycastTarget = false;
                e.img.preserveAspect = false;
            }
            return e;
        }

        void Write(Entry e, in UiNode n)
        {
            var l = e.last;
            bool f = e.fresh;
            if (f || l.rect != n.rect)
            {
                e.rt.anchoredPosition = new Vector2(n.rect.x, -n.rect.y);
                e.rt.sizeDelta = new Vector2(n.rect.width, n.rect.height);
            }
            if (e.img)
            {
                if (f || l.sprite != n.sprite) e.img.sprite = n.sprite != null && _sprites.TryGetValue(n.sprite, out var sp) ? sp : null;
                if (f || l.slice != n.slice || l.fill != n.fill)
                {
                    if (n.slice > 0f) { e.img.type = UnityEngine.UI.Image.Type.Sliced; e.img.pixelsPerUnitMultiplier = n.slice; e.img.fillCenter = true; }
                    else if (n.fill < 0.999f)
                    {
                        e.img.type = UnityEngine.UI.Image.Type.Filled;
                        e.img.fillMethod = UnityEngine.UI.Image.FillMethod.Radial360;
                        e.img.fillOrigin = (int)UnityEngine.UI.Image.Origin360.Top;
                        e.img.fillClockwise = true;
                        e.img.fillAmount = Mathf.Clamp01(n.fill);
                    }
                    else e.img.type = UnityEngine.UI.Image.Type.Simple;
                }
                if (f || l.color != n.color) e.img.color = n.color;
            }
            else
            {
                if (f || l.font != n.font) e.txt.font = n.font == UiFont.Title ? _title : n.font == UiFont.BodyBold ? _bold : _body;
                if (f || l.size != n.size) e.txt.fontSize = n.size;
                if (f || l.align != n.align) e.txt.alignment = n.align;
                if (f || l.color != n.color)
                {
                    e.txt.color = n.color;
                    e.shadow.effectColor = new Color(0f, 0f, 0f, 0.65f * n.color.a);
                }
                if (f || l.shadow != n.shadow) e.shadow.enabled = n.shadow;
                if (f || l.text != n.text) e.txt.text = n.text;
            }
            e.last = n;
            e.fresh = false;
        }
    }
}
