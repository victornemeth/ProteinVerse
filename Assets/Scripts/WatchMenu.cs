using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;

public class WatchMenu : MonoBehaviour
{
    // ── Configuration ────────────────────────────────────────────────
    [Header("Hand Side")]
    public bool isRightHand = false;

    [Header("Menu Content")]
    public bool showMoveButton  = true;
    public bool showColorButton = false;
    public string menuTitle = "PALM MENU";

    [Header("Menu Appearance")]
    public float panelWidth = 140f;
    [Tooltip("Leave at 0 to auto-size")]
    public float panelHeightOverride = 0f;
    [Tooltip("World-space scale of the canvas")]
    public float globalScale = 0.0008f;
    [Tooltip("Offset relative to the wrist bone")]
    public Vector3 menuOffset = new Vector3(0.02f, -0.02f, 0.06f);
    [Tooltip("Rotation offset relative to the wrist bone")]
    public Vector3 menuRotationOffset = new Vector3(180f, 0f, 0f);
    public float transitionSpeed = 10f;

    [Header("Detection Thresholds")]
    [Tooltip("Palm-to-camera dot product threshold for showing the menu")]
    public float facingThreshold = 0.6f;

    // ── Internal State ────────────────────────────────────────────────
    private OVRCameraRig  _rig;
    private OVRSkeleton   _skeleton;
    private OVRHand       _hand;

    private CanvasGroup   _canvasGroup;
    private RectTransform _panelRT;
    private float         _currentAlpha = 0f;

    private UmapPointCloud  _pointCloud;

    // Move button
    private TextMeshProUGUI _toggleMoveText;
    private Image           _toggleMoveBtnImg;
    private RectTransform   _moveBtnRT;

    // Long-press reset
    private Image _progressRing;
    private float _holdStart       = -1f;
    private bool  _holdFired       = false;
    private bool  _suppressClick   = false;
    private const float HoldTime   = 3f;
    private const float PokeRadius = 0.045f;

    // Color mode buttons (one per mode: 0=viridis, 1=RBP, 2=fiber/spike, 3=spike detail)
    private readonly Image[]           _colorBtnImages = new Image[4];
    private readonly TextMeshProUGUI[] _colorBtnTexts  = new TextMeshProUGUI[4];

    // ── Style constants ───────────────────────────────────────────────
    static readonly Color ColorSelectMode = new Color(0.08f, 0.10f, 0.25f, 0.97f);
    static readonly Color ColorMoveMode   = new Color(0.10f, 0.55f, 0.18f, 0.97f);

    // Active color per mode; inactive buttons are shown at 30% brightness
    static readonly Color[] ModeColors = {
        new Color(0.22f, 0.25f, 0.45f, 0.97f),  // 0: viridis/off  — muted navy
        new Color(0.55f, 0.22f, 0.00f, 0.97f),  // 1: RBPdetect2   — amber
        new Color(0.13f, 0.40f, 0.67f, 0.97f),  // 2: fiber/spike  — blue
        new Color(0.35f, 0.12f, 0.55f, 0.97f),  // 3: spike detail — purple
    };
    static readonly string[] ModeLabels = {
        "VIRIDIS\n(OFF)",
        "RBPDETECT2",
        "FIBER / SPIKE",
        "SPIKE DETAIL",
    };

    // ── Lifecycle ─────────────────────────────────────────────────────
    void Start()
    {
        _rig = FindFirstObjectByType<OVRCameraRig>();
        if (_rig != null)
        {
            var anchor = isRightHand ? _rig.rightHandAnchor : _rig.leftHandAnchor;
            _skeleton  = anchor.GetComponentInChildren<OVRSkeleton>();
            _hand      = anchor.GetComponentInChildren<OVRHand>();
        }
        _pointCloud = FindFirstObjectByType<UmapPointCloud>();

        BuildMenu();
        _canvasGroup.alpha  = 0f;
        _currentAlpha       = 0f;
        _panelRT.localScale = Vector3.zero;
    }

    void Update()
    {
        // Sync move button
        if (_pointCloud != null && _toggleMoveText != null)
        {
            bool moving = _pointCloud.isMovementEnabled;
            string t = moving ? "MOVE MODE\nON" : "SELECT MODE\nOFF";
            if (_toggleMoveText.text != t) _toggleMoveText.text = t;
            if (_toggleMoveBtnImg != null)
            {
                Color c = moving ? ColorMoveMode : ColorSelectMode;
                if (_toggleMoveBtnImg.color != c) _toggleMoveBtnImg.color = c;
            }
        }

        // Sync color mode buttons: active = full brightness, inactive = 30%
        if (_pointCloud != null)
        {
            int active = _pointCloud.ColorModeIndex;
            for (int i = 0; i < 4; i++)
            {
                if (_colorBtnImages[i] == null) continue;
                Color full = ModeColors[i];
                Color dim  = new Color(full.r * 0.3f, full.g * 0.3f, full.b * 0.3f, full.a);
                Color want = (i == active) ? full : dim;
                if (_colorBtnImages[i].color != want)
                    _colorBtnImages[i].color = want;
            }
        }

        // Hand tracking visibility
        if (_rig == null || _skeleton == null || !_skeleton.IsInitialized || _hand == null || !_hand.IsTracked)
        {
            UpdateVisibility(false, null, Vector3.zero);
            return;
        }

        var wristBone = GetBone(_skeleton, OVRSkeleton.BoneId.Hand_WristRoot);
        if (wristBone == null) return;

        Vector3 anchor     = wristBone.Transform.position;
        Vector3 palmNormal = -wristBone.Transform.up;
        Vector3 toCamera   = (_rig.centerEyeAnchor.position - anchor).normalized;
        bool looking       = Vector3.Dot(palmNormal, toCamera) > facingThreshold;

        UpdateVisibility(looking, wristBone.Transform, anchor);
        UpdateLongPress();
    }

    void UpdateLongPress()
    {
        if (_moveBtnRT == null || _progressRing == null || _pointCloud == null) return;

        // Find index tip on the palm-menu hand
        Vector3? tip = GetIndexTip(_skeleton);
        Vector3 center = _moveBtnRT.TransformPoint(_moveBtnRT.rect.center);
        bool near = tip.HasValue && (tip.Value - center).sqrMagnitude < PokeRadius * PokeRadius;

        if (!near)
        {
            _holdStart = -1f;
            _holdFired = false;
            _progressRing.fillAmount = 0f;
            return;
        }

        if (_holdStart < 0f) _holdStart = Time.time;

        float t = (Time.time - _holdStart) / HoldTime;
        _progressRing.fillAmount = Mathf.Clamp01(t);

        if (t >= 1f && !_holdFired)
        {
            _holdFired     = true;
            _suppressClick = true;
            _progressRing.fillAmount = 0f;
            _holdStart = -1f;
            _pointCloud.ResetToDefaults();
        }
    }

    static Vector3? GetIndexTip(OVRSkeleton sk)
    {
        if (sk == null || !sk.IsInitialized) return null;
        foreach (var b in sk.Bones)
            if (b.Id == OVRSkeleton.BoneId.Hand_IndexTip)
                return b.Transform.position;
        return null;
    }

    void UpdateVisibility(bool visible, Transform wrist, Vector3 anchor)
    {
        _currentAlpha = Mathf.MoveTowards(_currentAlpha, visible ? 1f : 0f, Time.deltaTime * transitionSpeed);
        _canvasGroup.alpha  = _currentAlpha;
        _panelRT.localScale = Vector3.one * globalScale;

        bool show = _currentAlpha > 0.01f;
        if (_canvasGroup.gameObject.activeSelf != show)
            _canvasGroup.gameObject.SetActive(show);

        if (show && wrist != null)
        {
            _panelRT.position = anchor
                              + wrist.right   * menuOffset.x
                              + wrist.up      * menuOffset.y
                              + wrist.forward * menuOffset.z;
            _panelRT.rotation = wrist.rotation * Quaternion.Euler(menuRotationOffset);
        }
    }

    private OVRBone GetBone(OVRSkeleton skel, OVRSkeleton.BoneId id)
    {
        foreach (var b in skel.Bones)
            if (b.Id == id) return b;
        return null;
    }

    // ── Canvas Construction ──────────────────────────────────────────────

    void BuildMenu()
    {
        // Auto panel height:
        //   move-only  → 4× the base button height (520px) so it's large and easy to tap
        //   color-only → header + 4 stacked buttons (30 + 4*80 = 350px)
        //   both       → header + move + 4 color rows (not the primary use case)
        float panelHeight;
        if (panelHeightOverride > 0f)
            panelHeight = panelHeightOverride;
        else if (showMoveButton && !showColorButton)
            panelHeight = 520f;
        else if (showColorButton && !showMoveButton)
            panelHeight = 30f + 4 * 80f;   // header + 4 rows
        else
            panelHeight = 30f + 100f + 4 * 60f;  // header + move + 4 color rows

        // Root canvas
        var canvasGo = new GameObject("WatchMenuCanvas");
        canvasGo.transform.SetParent(transform, false);
        canvasGo.layer = LayerMask.NameToLayer("UI");

        _canvasGroup       = canvasGo.AddComponent<CanvasGroup>();
        _panelRT           = canvasGo.AddComponent<RectTransform>();
        _panelRT.sizeDelta = new Vector2(panelWidth, panelHeight);
        _panelRT.localScale = Vector3.one * globalScale;

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<GraphicRaycaster>();

        EnsureEventSystem();
        var pc = canvasGo.AddComponent<PointableCanvas>();
        pc.InjectAllPointableCanvas(canvas);
        AddPokeInteractable(pc, canvasGo, panelWidth, panelHeight);

        var font = TMP_Settings.defaultFontAsset;

        // Background
        MakeImage(_panelRT, Vector2.zero, Vector2.one, new Color(0.05f, 0.07f, 0.15f, 0.97f));
        // Border
        MakeImage(_panelRT, Vector2.zero, Vector2.one, new Color(0.2f, 0.8f, 1f, 0.2f));

        // Header
        var headerRT = PinTop(_panelRT, 30f);
        MakeImage(headerRT, Vector2.zero, Vector2.one, new Color(0.12f, 0.14f, 0.36f, 1f));
        MakeLabel(headerRT, font, menuTitle, 12f, new Color(0.85f, 0.92f, 1f), FontStyles.Bold, TextAlignmentOptions.Center);

        // Content area (below header)
        var contentRT = new GameObject("Content").AddComponent<RectTransform>();
        contentRT.SetParent(_panelRT, false);
        contentRT.anchorMin = Vector2.zero;
        contentRT.anchorMax = new Vector2(1f, 1f);
        contentRT.offsetMin = new Vector2(0f, 0f);
        contentRT.offsetMax = new Vector2(0f, -30f);  // leave header space

        if (showMoveButton && !showColorButton)
        {
            BuildMoveButton(contentRT, font);
        }
        else if (showColorButton && !showMoveButton)
        {
            BuildColorButtons(contentRT, font);
        }
        else if (showMoveButton && showColorButton)
        {
            // Move button takes fixed 100px at top, color buttons fill the rest
            var moveArea = new GameObject("MoveArea").AddComponent<RectTransform>();
            moveArea.SetParent(contentRT, false);
            moveArea.anchorMin = new Vector2(0f, 1f); moveArea.anchorMax = Vector2.one;
            moveArea.pivot     = new Vector2(0.5f, 1f);
            moveArea.sizeDelta = new Vector2(0f, 100f);
            moveArea.anchoredPosition = Vector2.zero;
            BuildMoveButton(moveArea, font);

            var colorArea = new GameObject("ColorArea").AddComponent<RectTransform>();
            colorArea.SetParent(contentRT, false);
            colorArea.anchorMin = Vector2.zero; colorArea.anchorMax = new Vector2(1f, 1f);
            colorArea.offsetMin = Vector2.zero; colorArea.offsetMax = new Vector2(0f, -100f);
            BuildColorButtons(colorArea, font);
        }
    }

    void BuildMoveButton(RectTransform parent, TMP_FontAsset font)
    {
        var btnRT = new GameObject("MoveModeBtn").AddComponent<RectTransform>();
        btnRT.SetParent(parent, false);
        btnRT.anchorMin = Vector2.zero; btnRT.anchorMax = Vector2.one;
        btnRT.offsetMin = new Vector2(6f, 6f);
        btnRT.offsetMax = new Vector2(-6f, -6f);

        bool moving = _pointCloud != null && _pointCloud.isMovementEnabled;
        var btnImg = btnRT.gameObject.AddComponent<Image>();
        btnImg.color         = moving ? ColorMoveMode : ColorSelectMode;
        btnImg.raycastTarget = true;

        var btn = btnRT.gameObject.AddComponent<Button>();
        SetButtonColors(btn, btnImg);
        btn.onClick.AddListener(() => {
            if (_suppressClick) { _suppressClick = false; return; }
            if (_pointCloud != null)
                _pointCloud.isMovementEnabled = !_pointCloud.isMovementEnabled;
        });

        _toggleMoveText   = MakeLabel(btnRT, font, moving ? "MOVE MODE\nON" : "SELECT MODE\nOFF",
                                      22f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
        _toggleMoveBtnImg = btnImg;
        _moveBtnRT        = btnRT;

        // Radial progress ring overlay (fills clockwise from top over 3-second hold)
        var ringGO = new GameObject("ProgressRing");
        ringGO.transform.SetParent(btnRT, false);
        var ringRT = ringGO.AddComponent<RectTransform>();
        ringRT.anchorMin = new Vector2(0.1f, 0.1f);
        ringRT.anchorMax = new Vector2(0.9f, 0.9f);
        ringRT.offsetMin = ringRT.offsetMax = Vector2.zero;
        _progressRing              = ringGO.AddComponent<Image>();
        _progressRing.color        = new Color(1f, 0.85f, 0.2f, 0.75f);  // amber
        _progressRing.type         = Image.Type.Filled;
        _progressRing.fillMethod   = Image.FillMethod.Radial360;
        _progressRing.fillOrigin   = (int)Image.Origin360.Top;
        _progressRing.fillClockwise = true;
        _progressRing.fillAmount   = 0f;
        _progressRing.raycastTarget = false;
    }

    void BuildColorButtons(RectTransform parent, TMP_FontAsset font)
    {
        int active = _pointCloud != null ? _pointCloud.ColorModeIndex : 0;

        for (int i = 0; i < 4; i++)
        {
            int modeIdx = i;  // capture for lambda

            // Each button is 1/4 of the parent height
            float yMax = 1f - i       * 0.25f;
            float yMin = 1f - (i + 1) * 0.25f;

            var btnRT = new GameObject($"ColorBtn_{i}").AddComponent<RectTransform>();
            btnRT.SetParent(parent, false);
            btnRT.anchorMin = new Vector2(0f, yMin);
            btnRT.anchorMax = new Vector2(1f, yMax);
            btnRT.offsetMin = new Vector2(6f, 4f);
            btnRT.offsetMax = new Vector2(-6f, -4f);

            Color full = ModeColors[i];
            Color dim  = new Color(full.r * 0.3f, full.g * 0.3f, full.b * 0.3f, full.a);

            var btnImg = btnRT.gameObject.AddComponent<Image>();
            btnImg.color        = (i == active) ? full : dim;
            btnImg.raycastTarget = true;

            var btn = btnRT.gameObject.AddComponent<Button>();
            SetButtonColors(btn, btnImg);
            btn.onClick.AddListener(() => OnColorBtnClicked(modeIdx));

            _colorBtnImages[i] = btnImg;
            _colorBtnTexts[i]  = MakeLabel(btnRT, font, ModeLabels[i],
                                            16f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
        }
    }

    void OnColorBtnClicked(int mode)
    {
        if (_pointCloud == null) return;
        // Pressing the active mode toggles back to viridis (0), otherwise activates that mode
        int newMode = (_pointCloud.ColorModeIndex == mode && mode != 0) ? 0 : mode;
        _pointCloud.SetColorMode(newMode);
    }

    // ── Infrastructure ────────────────────────────────────────────────

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<PointableCanvasModule>() != null) return;
        var es = FindFirstObjectByType<EventSystem>();
        if (es != null) es.gameObject.AddComponent<PointableCanvasModule>();
        else
        {
            var g = new GameObject("EventSystem");
            g.AddComponent<EventSystem>();
            g.AddComponent<PointableCanvasModule>();
        }
    }

    void AddPokeInteractable(PointableCanvas pc, GameObject canvasGo, float w, float h)
    {
        var pokeGO  = new GameObject("PokeInteractable");
        pokeGO.transform.SetParent(canvasGo.transform, false);

        var surfGO  = new GameObject("PokeSurface");
        surfGO.transform.SetParent(pokeGO.transform, false);
        surfGO.transform.localScale = new Vector3(1f, 1f, 0.001f);

        var plane = surfGO.AddComponent<PlaneSurface>();
        plane.InjectAllPlaneSurface(PlaneSurface.NormalFacing.Backward, true);

        var clip = surfGO.AddComponent<BoundsClipper>();
        clip.Size = new Vector3(w, h, 500f);

        var cps = surfGO.AddComponent<ClippedPlaneSurface>();
        cps.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { clip });

        var poke = pokeGO.AddComponent<PokeInteractable>();
        poke.InjectAllPokeInteractable(cps);
        poke.InjectOptionalPointableElement(pc);
    }

    static void SetButtonColors(Button btn, Image img)
    {
        btn.targetGraphic = img;
        var c = btn.colors;
        c.normalColor      = Color.white;
        c.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
        c.pressedColor     = new Color(0.75f, 0.75f, 0.75f, 1f);
        c.colorMultiplier  = 1f;
        btn.colors = c;
    }

    // ── UI Helpers ────────────────────────────────────────────────────

    RectTransform PinTop(RectTransform parent, float height)
    {
        var rt = new GameObject("HeaderRT").AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin        = new Vector2(0f, 1f);
        rt.anchorMax        = Vector2.one;
        rt.pivot            = new Vector2(0.5f, 1f);
        rt.sizeDelta        = new Vector2(0f, height);
        rt.anchoredPosition = Vector2.zero;
        return rt;
    }

    static void MakeImage(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Color col)
    {
        var go = new GameObject("Img");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.color        = col;
        img.raycastTarget = false;
    }

    static TextMeshProUGUI MakeLabel(RectTransform parent, TMP_FontAsset font, string text,
        float size, Color color, FontStyles style, TextAlignmentOptions align)
    {
        var go = new GameObject("Lbl");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.font = font; tmp.text = text; tmp.fontSize = size;
        tmp.color = color; tmp.fontStyle = style; tmp.alignment = align;
        tmp.raycastTarget = false;
        return tmp;
    }
}
