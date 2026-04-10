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
    [Header("Menu Appearance")]
    public float panelWidth = 140f;
    public float panelHeight = 160f;
    [Tooltip("Overall scale of the menu in the world. Reduce this to make everything (including fonts) smaller.")]
    public float globalScale = 0.0004f; // slightly larger text for palm
    [Tooltip("Offset relative to the wrist bone (moves menu to the palm center)")]
    public Vector3 menuOffset = new Vector3(0.02f, -0.02f, 0.06f); // Shift into palm: right(thumb-ish), down(out of palm), forward(towards fingers)
    [Tooltip("Rotation offset relative to the wrist bone")]
    public Vector3 menuRotationOffset = new Vector3(180f, 0f, 0f); // Flipped 180 to face out of the palm instead of back of hand
    public float transitionSpeed = 10f;

    [Header("Detection Thresholds")]
    [Tooltip("How directly the palm needs to face the camera to appear (0 to 1)")]
    public float facingThreshold = 0.6f;

    // ── Internal State ────────────────────────────────────────────────
    private OVRCameraRig _rig;
    private OVRSkeleton _leftSkel;
    private OVRHand _leftHand;
    
    private CanvasGroup _canvasGroup;
    private RectTransform _panelRT;
    
    private bool _isLookingAtWatch = false;
    private float _currentAlpha = 0f;

    private UmapPointCloud _pointCloud;
    private TextMeshProUGUI _toggleMoveText;

    void Start()
    {
        _rig = FindFirstObjectByType<OVRCameraRig>();
        if (_rig != null)
        {
            _leftSkel = _rig.leftHandAnchor.GetComponentInChildren<OVRSkeleton>();
            _leftHand = _rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
        }

        _pointCloud = FindFirstObjectByType<UmapPointCloud>();

        BuildMenu();
        
        // Start completely hidden
        _canvasGroup.alpha = 0f;
        _currentAlpha = 0f;
        _panelRT.localScale = Vector3.zero;
    }

    void Update()
    {
        if (_pointCloud != null && _toggleMoveText != null)
        {
            string expectedText = _pointCloud.isMovementEnabled ? "Move Mode: ON" : "Move Mode: OFF";
            if (_toggleMoveText.text != expectedText)
            {
                _toggleMoveText.text = expectedText;
            }
        }

        if (_rig == null || _leftSkel == null || !_leftSkel.IsInitialized || _leftHand == null || !_leftHand.IsTracked)
        {
            // If hand is not tracked, smoothly hide the menu
            UpdateMenuVisibility(false, null, Vector3.zero);
            return;
        }

        // Get the wrist bone
        var wristBone = GetBone(_leftSkel, OVRSkeleton.BoneId.Hand_WristRoot);
        
        if (wristBone == null) return;

        // Base anchor point: Wrist joint
        // The center of the palm is generally slightly forward (towards fingers) and 'down' (out of the palm face)
        Vector3 anchorPosition = wristBone.Transform.position;

        // Approximate the palm normal: Meta Quest usually puts "up" out the back of the hand,
        // meaning "down" (-up) is straight out of the palm!
        Vector3 palmNormal = -wristBone.Transform.up; 
        
        // Vector from palm to camera
        Vector3 cameraPos = _rig.centerEyeAnchor.position;
        Vector3 palmToCamera = (cameraPos - anchorPosition).normalized;

        // Calculate if the user is looking at their palm
        float dotProduct = Vector3.Dot(palmNormal, palmToCamera);
        _isLookingAtWatch = dotProduct > facingThreshold;

        UpdateMenuVisibility(_isLookingAtWatch, wristBone.Transform, anchorPosition);
    }

    void UpdateMenuVisibility(bool visible, Transform wristTransform, Vector3 anchorPosition)
    {
        // Smooth transition for alpha only
        float targetAlpha = visible ? 1f : 0f;
        _currentAlpha = Mathf.MoveTowards(_currentAlpha, targetAlpha, Time.deltaTime * transitionSpeed);
        
        _canvasGroup.alpha = _currentAlpha;
        _panelRT.localScale = Vector3.one * globalScale; // DO NOT animate scale! It breaks Poke interaction bounds.

        bool isVisibleThreshold = _currentAlpha > 0.01f;
        
        // Enable/disable rendering components to save performance when hidden
        if (_canvasGroup.gameObject.activeSelf != isVisibleThreshold)
        {
            _canvasGroup.gameObject.SetActive(isVisibleThreshold);
        }

        if (isVisibleThreshold && wristTransform != null)
        {
            // Position the menu relative to the calculated watch anchor point
            Vector3 targetPosition = anchorPosition 
                                   + wristTransform.right * menuOffset.x 
                                   + wristTransform.up * menuOffset.y 
                                   + wristTransform.forward * menuOffset.z;
            
            // Snap exactly to position and firmly lock rotation to the wrist!
            // No billboarding to camera. It stays rigidly attached to the arm geometry.
            _panelRT.position = targetPosition;
            _panelRT.rotation = wristTransform.rotation * Quaternion.Euler(menuRotationOffset);
        }
    }

    private OVRBone GetBone(OVRSkeleton skel, OVRSkeleton.BoneId boneId)
    {
        foreach (var b in skel.Bones)
            if (b.Id == boneId) return b;
        return null;
    }

    // ── Canvas Construction ─────────────────────────────────────────────
    
    void BuildMenu()
    {
        // 1. Root Canvas Object
        GameObject canvasGo = new GameObject("WatchMenuCanvas");
        canvasGo.transform.SetParent(transform, false);
        canvasGo.layer = LayerMask.NameToLayer("UI");
        
        _canvasGroup = canvasGo.AddComponent<CanvasGroup>();
        _panelRT = canvasGo.AddComponent<RectTransform>();
        _panelRT.sizeDelta = new Vector2(panelWidth, panelHeight);
        _panelRT.localScale = Vector3.one * globalScale; // Scale down for VR

        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<GraphicRaycaster>();
        
        // Oculus Interaction setup
        EnsureEventSystemHasPointableCanvasModule();
        var pc = canvasGo.AddComponent<PointableCanvas>();
        pc.InjectAllPointableCanvas(canvas);
        AddPokeInteractable(pc, canvasGo);

        var font = TMP_Settings.defaultFontAsset;

        // 2. Background (Holographic style matching InfoPanel)
        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(_panelRT, false);
        var bgRT = bgGo.AddComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.05f, 0.07f, 0.15f, 0.97f);
        bgImg.raycastTarget = false; // Prevent blocking pokes

        // Optional: Border/glow
        var borderGo = new GameObject("Border");
        borderGo.transform.SetParent(_panelRT, false);
        var borderRT = borderGo.AddComponent<RectTransform>();
        borderRT.anchorMin = Vector2.zero; borderRT.anchorMax = Vector2.one;
        borderRT.offsetMin = borderRT.offsetMax = Vector2.zero;
        var borderImg = borderGo.AddComponent<Image>();
        borderImg.color = new Color(0.2f, 0.8f, 1f, 0.2f); // Subtler border
        borderImg.raycastTarget = false; // Prevent blocking pokes

        // 3. Header
        var headerRT = Pin("Header", bgRT, 0f, 30f);
        var headerImg = headerRT.gameObject.AddComponent<Image>();
        headerImg.color = new Color(0.12f, 0.14f, 0.36f, 1f);
        headerImg.raycastTarget = false; // Prevent blocking pokes
        
        var titleRT = new GameObject("Title").AddComponent<RectTransform>();
        titleRT.SetParent(headerRT, false);
        titleRT.anchorMin = Vector2.zero; titleRT.anchorMax = Vector2.one;
        titleRT.offsetMin = new Vector2(12f, 0f);
        titleRT.offsetMax = new Vector2(-12f, 0f);
        Label(titleRT, font, "PALM MENU", 12f, new Color(0.85f, 0.92f, 1f), FontStyles.Bold, TextAlignmentOptions.Center);

        // 4. Content Buttons
        float y = -30f;
        Divider(bgRT, y); y -= 10f;
        
        // Move Mode Toggle Button
        var btnRT = AbsRow(bgRT, -50f, 60f, 12f); // Taller button centered
        var btnImg = btnRT.gameObject.AddComponent<Image>();
        btnImg.color = new Color(0.10f, 0.12f, 0.26f, 0.9f);
        btnImg.raycastTarget = true; // Ensure this catches the poke
        
        var btn = btnRT.gameObject.AddComponent<Button>();
        btn.targetGraphic = btnImg;
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.15f, 0.20f, 0.40f, 1f);
        colors.pressedColor = new Color(0.20f, 0.25f, 0.50f, 1f);
        btn.colors = colors;
        
        string initialText = (_pointCloud != null && _pointCloud.isMovementEnabled) ? "Move Mode: ON" : "Move Mode: OFF";
        _toggleMoveText = Label(btnRT, font, initialText, 16f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center); // Larger text
        
        btn.onClick.AddListener(OnToggleMoveModeClicked);
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

    void AddPokeInteractable(PointableCanvas pc, GameObject canvasGo)
    {
        var pokeGO = new GameObject("PokeInteractable");
        pokeGO.transform.SetParent(canvasGo.transform, false);

        var surfGO = new GameObject("PokeSurface");
        surfGO.transform.SetParent(pokeGO.transform, false);
        surfGO.transform.localScale = new Vector3(1f, 1f, 0.001f);

        var plane = surfGO.AddComponent<PlaneSurface>();
        plane.InjectAllPlaneSurface(PlaneSurface.NormalFacing.Backward, true);

        var clip = surfGO.AddComponent<BoundsClipper>();
        clip.Size = new Vector3(panelWidth, panelHeight, 500f); // Generous 500 depth ensures absolutely no poke skipping

        var cps = surfGO.AddComponent<ClippedPlaneSurface>();
        cps.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { clip });

        var poke = pokeGO.AddComponent<PokeInteractable>();
        poke.InjectAllPokeInteractable(cps);
        poke.InjectOptionalPointableElement(pc);
    }

    // ── Callbacks ────────────────────────────────────────────────────────

    private void OnToggleMoveModeClicked()
    {
        if (_pointCloud != null)
        {
            _pointCloud.isMovementEnabled = !_pointCloud.isMovementEnabled;
            
            if (_toggleMoveText != null)
            {
                _toggleMoveText.text = _pointCloud.isMovementEnabled ? "Move Mode: ON" : "Move Mode: OFF";
            }
        }
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

    RectTransform AbsRow(RectTransform parent, float y, float height, float padding = 0f)
    {
        var rt = new GameObject("Row").AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(-padding * 2f, height); 
        rt.anchoredPosition = new Vector2(0f, y);
        return rt;
    }

    RectTransform Divider(RectTransform parent, float y)
    {
        var rt = new GameObject("Div").AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(-24f, 1f);
        rt.anchoredPosition = new Vector2(0f, y);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.27f, 0.30f, 0.52f, 0.45f);
        img.raycastTarget = false; // Extremely important! A 1px line overlaying a button can steal pokes!
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
        tmp.font = font; 
        tmp.text = text; 
        tmp.fontSize = size;
        tmp.color = color; 
        tmp.fontStyle = style; 
        tmp.alignment = align;
        tmp.raycastTarget = false;
        
        return tmp;
    }
}
