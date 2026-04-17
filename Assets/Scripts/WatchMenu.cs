using System.Collections;
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
    public bool showColorButton = true;
    public string menuTitle = "PALM MENU";

    [Header("Menu Appearance")]
    public float panelWidth = 140f;
    [Tooltip("Leave at 0 to auto-size based on which buttons are shown")]
    public float panelHeightOverride = 0f;
    [Tooltip("Overall scale of the menu in the world.")]
    public float globalScale = 0.0008f;
    [Tooltip("Offset relative to the wrist bone (moves menu to the palm center)")]
    public Vector3 menuOffset = new Vector3(0.02f, -0.02f, 0.06f);
    [Tooltip("Rotation offset relative to the wrist bone")]
    public Vector3 menuRotationOffset = new Vector3(180f, 0f, 0f);
    public float transitionSpeed = 10f;

    [Header("Detection Thresholds")]
    [Tooltip("How directly the palm needs to face the camera to appear (0 to 1)")]
    public float facingThreshold = 0.6f;

    // ── Internal State ────────────────────────────────────────────────
    private OVRCameraRig _rig;
    private OVRSkeleton  _skeleton;
    private OVRHand      _hand;

    private CanvasGroup  _canvasGroup;
    private RectTransform _panelRT;

    private bool  _isLookingAtWatch = false;
    private float _currentAlpha = 0f;

    private UmapPointCloud  _pointCloud;
    private TextMeshProUGUI _toggleMoveText;
    private Image           _toggleMoveBtnImg;
    private TextMeshProUGUI _colorModeText;
    private Image           _colorModeBtnImg;

    // Button colors
    static readonly Color ColorSelectMode = new Color(0.08f, 0.10f, 0.25f, 0.97f);
    static readonly Color ColorMoveMode   = new Color(0.10f, 0.55f, 0.18f, 0.97f);
    static readonly Color[] ColorModeColors = {
        new Color(0.08f, 0.10f, 0.25f, 0.97f),  // 0: viridis / off
        new Color(0.55f, 0.22f, 0.00f, 0.97f),  // 1: RBPdetect2 amber
        new Color(0.13f, 0.40f, 0.67f, 0.97f),  // 2: fiber/spike blue
        new Color(0.35f, 0.12f, 0.55f, 0.97f),  // 3: spike detail purple
    };
    static readonly string[] ColorModeLabels = {
        "COLORS\nOFF",
        "RBPDETECT2\nON",
        "FIBER/SPIKE\nON",
        "SPIKE\nDETAIL\nON",
    };

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

        _canvasGroup.alpha    = 0f;
        _currentAlpha         = 0f;
        _panelRT.localScale   = Vector3.zero;
    }

    void Update()
    {
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

        if (_pointCloud != null && _colorModeText != null)
        {
            int mode = _pointCloud.ColorModeIndex;
            string t = ColorModeLabels[Mathf.Clamp(mode, 0, ColorModeLabels.Length - 1)];
            if (_colorModeText.text != t) _colorModeText.text = t;
            if (_colorModeBtnImg != null)
            {
                Color c = ColorModeColors[Mathf.Clamp(mode, 0, ColorModeColors.Length - 1)];
                if (_colorModeBtnImg.color != c) _colorModeBtnImg.color = c;
            }
        }

        if (_rig == null || _skeleton == null || !_skeleton.IsInitialized || _hand == null || !_hand.IsTracked)
        {
            UpdateMenuVisibility(false, null, Vector3.zero);
            return;
        }

        var wristBone = GetBone(_skeleton, OVRSkeleton.BoneId.Hand_WristRoot);
        if (wristBone == null) return;

        Vector3 anchorPosition = wristBone.Transform.position;
        Vector3 palmNormal     = -wristBone.Transform.up;
        Vector3 cameraPos      = _rig.centerEyeAnchor.position;
        Vector3 palmToCamera   = (cameraPos - anchorPosition).normalized;

        float dot = Vector3.Dot(palmNormal, palmToCamera);
        _isLookingAtWatch = dot > facingThreshold;

        UpdateMenuVisibility(_isLookingAtWatch, wristBone.Transform, anchorPosition);
    }

    void UpdateMenuVisibility(bool visible, Transform wristTransform, Vector3 anchorPosition)
    {
        float targetAlpha = visible ? 1f : 0f;
        _currentAlpha = Mathf.MoveTowards(_currentAlpha, targetAlpha, Time.deltaTime * transitionSpeed);
        _canvasGroup.alpha = _currentAlpha;
        _panelRT.localScale = Vector3.one * globalScale;

        bool show = _currentAlpha > 0.01f;
        if (_canvasGroup.gameObject.activeSelf != show)
            _canvasGroup.gameObject.SetActive(show);

        if (show && wristTransform != null)
        {
            Vector3 pos = anchorPosition
                        + wristTransform.right   * menuOffset.x
                        + wristTransform.up      * menuOffset.y
                        + wristTransform.forward * menuOffset.z;

            _panelRT.position = pos;
            _panelRT.rotation = wristTransform.rotation * Quaternion.Euler(menuRotationOffset);
        }
    }

    private OVRBone GetBone(OVRSkeleton skel, OVRSkeleton.BoneId boneId)
    {
        foreach (var b in skel.Bones)
            if (b.Id == boneId) return b;
        return null;
    }

    // ── Canvas Construction ──────────────────────────────────────────────

    void BuildMenu()
    {
        int buttonCount = (showMoveButton ? 1 : 0) + (showColorButton ? 1 : 0);
        float panelHeight = panelHeightOverride > 0f ? panelHeightOverride
                          : buttonCount == 1 ? 130f : 220f;

        // 1. Root Canvas
        var canvasGo = new GameObject("WatchMenuCanvas");
        canvasGo.transform.SetParent(transform, false);
        canvasGo.layer = LayerMask.NameToLayer("UI");

        _canvasGroup = canvasGo.AddComponent<CanvasGroup>();
        _panelRT     = canvasGo.AddComponent<RectTransform>();
        _panelRT.sizeDelta  = new Vector2(panelWidth, panelHeight);
        _panelRT.localScale = Vector3.one * globalScale;

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<GraphicRaycaster>();

        EnsureEventSystemHasPointableCanvasModule();
        var pc = canvasGo.AddComponent<PointableCanvas>();
        pc.InjectAllPointableCanvas(canvas);
        AddPokeInteractable(pc, canvasGo, panelWidth, panelHeight);

        var font = TMP_Settings.defaultFontAsset;

        // 2. Background
        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(_panelRT, false);
        var bgRT = bgGo.AddComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.05f, 0.07f, 0.15f, 0.97f);
        bgImg.raycastTarget = false;

        // 3. Border
        var borderGo = new GameObject("Border");
        borderGo.transform.SetParent(_panelRT, false);
        var borderRT = borderGo.AddComponent<RectTransform>();
        borderRT.anchorMin = Vector2.zero; borderRT.anchorMax = Vector2.one;
        borderRT.offsetMin = borderRT.offsetMax = Vector2.zero;
        var borderImg = borderGo.AddComponent<Image>();
        borderImg.color = new Color(0.2f, 0.8f, 1f, 0.2f);
        borderImg.raycastTarget = false;

        // 4. Header
        var headerRT = Pin("Header", bgRT, 0f, 30f);
        var headerImg = headerRT.gameObject.AddComponent<Image>();
        headerImg.color = new Color(0.12f, 0.14f, 0.36f, 1f);
        headerImg.raycastTarget = false;

        var titleRT = new GameObject("Title").AddComponent<RectTransform>();
        titleRT.SetParent(headerRT, false);
        titleRT.anchorMin = Vector2.zero; titleRT.anchorMax = Vector2.one;
        titleRT.offsetMin = new Vector2(12f, 0f);
        titleRT.offsetMax = new Vector2(-12f, 0f);
        Label(titleRT, font, menuTitle, 12f, new Color(0.85f, 0.92f, 1f), FontStyles.Bold, TextAlignmentOptions.Center);

        // 5. Buttons
        if (buttonCount == 2)
        {
            // Both buttons: move on top half, color on bottom half
            BuildMoveButton(bgRT, font, new Vector2(0f, 0.5f), Vector2.one,
                            new Vector2(6f, 3f), new Vector2(-6f, -36f));
            BuildColorButton(bgRT, font, Vector2.zero, new Vector2(1f, 0.5f),
                             new Vector2(6f, 6f), new Vector2(-6f, -3f));
        }
        else if (showMoveButton)
        {
            // Full height (below header)
            BuildMoveButton(bgRT, font, Vector2.zero, Vector2.one,
                            new Vector2(6f, 6f), new Vector2(-6f, -36f));
        }
        else if (showColorButton)
        {
            BuildColorButton(bgRT, font, Vector2.zero, Vector2.one,
                             new Vector2(6f, 6f), new Vector2(-6f, -36f));
        }
    }

    void BuildMoveButton(RectTransform parent, TMP_FontAsset font,
                         Vector2 anchorMin, Vector2 anchorMax,
                         Vector2 offsetMin, Vector2 offsetMax)
    {
        var btnRT = new GameObject("MoveModeBtn").AddComponent<RectTransform>();
        btnRT.SetParent(parent, false);
        btnRT.anchorMin = anchorMin; btnRT.anchorMax = anchorMax;
        btnRT.offsetMin = offsetMin; btnRT.offsetMax = offsetMax;

        bool moving = _pointCloud != null && _pointCloud.isMovementEnabled;
        var btnImg = btnRT.gameObject.AddComponent<Image>();
        btnImg.color = moving ? ColorMoveMode : ColorSelectMode;
        btnImg.raycastTarget = true;

        var btn = btnRT.gameObject.AddComponent<Button>();
        btn.targetGraphic = btnImg;
        var c = btn.colors;
        c.normalColor = Color.white; c.highlightedColor = new Color(1.18f,1.18f,1.18f,1f);
        c.pressedColor = new Color(0.75f,0.75f,0.75f,1f); c.colorMultiplier = 1f;
        btn.colors = c;

        _toggleMoveText    = Label(btnRT, font, moving ? "MOVE MODE\nON" : "SELECT MODE\nOFF",
                                   18f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
        _toggleMoveBtnImg  = btnImg;
        btn.onClick.AddListener(OnToggleMoveModeClicked);
    }

    void BuildColorButton(RectTransform parent, TMP_FontAsset font,
                          Vector2 anchorMin, Vector2 anchorMax,
                          Vector2 offsetMin, Vector2 offsetMax)
    {
        var btnRT = new GameObject("ColorModeBtn").AddComponent<RectTransform>();
        btnRT.SetParent(parent, false);
        btnRT.anchorMin = anchorMin; btnRT.anchorMax = anchorMax;
        btnRT.offsetMin = offsetMin; btnRT.offsetMax = offsetMax;

        int mode = _pointCloud != null ? _pointCloud.ColorModeIndex : 0;
        var btnImg = btnRT.gameObject.AddComponent<Image>();
        btnImg.color = ColorModeColors[Mathf.Clamp(mode, 0, ColorModeColors.Length - 1)];
        btnImg.raycastTarget = true;

        var btn = btnRT.gameObject.AddComponent<Button>();
        btn.targetGraphic = btnImg;
        var c = btn.colors;
        c.normalColor = Color.white; c.highlightedColor = new Color(1.18f,1.18f,1.18f,1f);
        c.pressedColor = new Color(0.75f,0.75f,0.75f,1f); c.colorMultiplier = 1f;
        btn.colors = c;

        _colorModeText   = Label(btnRT, font, ColorModeLabels[Mathf.Clamp(mode, 0, ColorModeLabels.Length - 1)],
                                 18f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
        _colorModeBtnImg = btnImg;
        btn.onClick.AddListener(OnToggleColorModeClicked);
    }

    void EnsureEventSystemHasPointableCanvasModule()
    {
        if (FindFirstObjectByType<PointableCanvasModule>() == null)
        {
            var es = FindFirstObjectByType<EventSystem>();
            if (es != null) es.gameObject.AddComponent<PointableCanvasModule>();
            else
            {
                var g = new GameObject("EventSystem");
                g.AddComponent<EventSystem>();
                g.AddComponent<PointableCanvasModule>();
            }
        }
    }

    void AddPokeInteractable(PointableCanvas pc, GameObject canvasGo, float w, float h)
    {
        var pokeGO = new GameObject("PokeInteractable");
        pokeGO.transform.SetParent(canvasGo.transform, false);

        var surfGO = new GameObject("PokeSurface");
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

    // ── Callbacks ────────────────────────────────────────────────────────

    private void OnToggleMoveModeClicked()
    {
        if (_pointCloud == null) return;
        _pointCloud.isMovementEnabled = !_pointCloud.isMovementEnabled;
    }

    private void OnToggleColorModeClicked()
    {
        if (_pointCloud == null) return;
        _pointCloud.CycleColorMode();
    }

    // ── UI Layout Helpers ────────────────────────────────────────────────

    RectTransform Pin(string name, RectTransform parent, float y, float height)
    {
        var rt = new GameObject(name).AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(0f, height);
        rt.anchoredPosition = new Vector2(0f, y);
        return rt;
    }

    TextMeshProUGUI Label(RectTransform parent, TMP_FontAsset font, string text, float size,
        Color color, FontStyles style, TextAlignmentOptions align)
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
