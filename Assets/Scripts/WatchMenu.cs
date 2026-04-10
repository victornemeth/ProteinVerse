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
    public float panelWidth = 300f;
    public float panelHeight = 400f;
    [Tooltip("Overall scale of the menu in the world. Reduce this to make everything (including fonts) smaller.")]
    public float globalScale = 0.0002f; // 5x smaller than 0.001, makes a 300x400 canvas be 6cm x 8cm in world space
    [Tooltip("Offset relative to the wrist bone")]
    public Vector3 menuOffset = new Vector3(-0.05f, 0.15f, 0.05f); 
    public float transitionSpeed = 10f;
    
    [Header("Detection Thresholds")]
    [Tooltip("How directly the watch needs to face the camera to appear (0 to 1)")]
    public float facingThreshold = 0.5f;

    // ── Internal State ────────────────────────────────────────────────
    private OVRCameraRig _rig;
    private OVRSkeleton _leftSkel;
    private OVRHand _leftHand;
    
    private CanvasGroup _canvasGroup;
    private RectTransform _panelRT;
    private LineRenderer _projectionLine;
    
    private bool _isLookingAtWatch = false;
    private float _currentAlpha = 0f;

    void Start()
    {
        _rig = FindFirstObjectByType<OVRCameraRig>();
        if (_rig != null)
        {
            _leftSkel = _rig.leftHandAnchor.GetComponentInChildren<OVRSkeleton>();
            _leftHand = _rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
        }

        BuildMenu();
        
        // Start completely hidden
        _canvasGroup.alpha = 0f;
        _currentAlpha = 0f;
        _panelRT.localScale = Vector3.zero;
        _projectionLine.enabled = false;
    }

    void Update()
    {
        if (_rig == null || _leftSkel == null || !_leftSkel.IsInitialized || _leftHand == null || !_leftHand.IsTracked)
        {
            // If hand is not tracked, smoothly hide the menu
            UpdateMenuVisibility(false, null);
            return;
        }

        // Get the wrist bone
        var wristBone = GetBone(_leftSkel, OVRSkeleton.BoneId.Hand_WristRoot);
        var middleMetacarpal = GetBone(_leftSkel, OVRSkeleton.BoneId.Hand_Middle1);
        
        if (wristBone == null) return;

        // Calculate "Up" direction of the watch (usually the back of the hand)
        // For Meta Quest hands, back of the left hand is usually roughly the -Up or -Forward of the wrist bone,
        // but it can vary. A robust way is cross product of fingers, or just using palm normal negated.
        // Let's approximate the watch face normal:
        Vector3 watchNormal = wristBone.Transform.up; 
        
        // Vector from watch to camera
        Vector3 cameraPos = _rig.centerEyeAnchor.position;
        Vector3 watchToCamera = (cameraPos - wristBone.Transform.position).normalized;

        // Calculate if the user is looking at the watch
        float dotProduct = Vector3.Dot(watchNormal, watchToCamera);
        _isLookingAtWatch = dotProduct > facingThreshold;

        UpdateMenuVisibility(_isLookingAtWatch, wristBone.Transform);
    }

    void UpdateMenuVisibility(bool visible, Transform wristTransform)
    {
        // Smooth transition
        float targetAlpha = visible ? 1f : 0f;
        _currentAlpha = Mathf.Lerp(_currentAlpha, targetAlpha, Time.deltaTime * transitionSpeed);
        
        _canvasGroup.alpha = _currentAlpha;
        _panelRT.localScale = Vector3.one * Mathf.Lerp(globalScale * 0.5f, globalScale, _currentAlpha);

        bool isVisibleThreshold = _currentAlpha > 0.05f;
        
        // Enable/disable rendering components to save performance when hidden
        if (_canvasGroup.gameObject.activeSelf != isVisibleThreshold)
        {
            _canvasGroup.gameObject.SetActive(isVisibleThreshold);
            _projectionLine.enabled = isVisibleThreshold;
        }

        if (isVisibleThreshold && wristTransform != null)
        {
            // Position the menu relative to the wrist
            Vector3 targetPosition = wristTransform.position 
                                   + wristTransform.right * menuOffset.x 
                                   + wristTransform.up * menuOffset.y 
                                   + wristTransform.forward * menuOffset.z;
            
            // Make the panel face the camera
            Vector3 cameraPos = _rig.centerEyeAnchor.position;
            Vector3 lookDir = targetPosition - cameraPos;
            
            _panelRT.position = Vector3.Lerp(_panelRT.position, targetPosition, Time.deltaTime * transitionSpeed);
            if (lookDir != Vector3.zero)
            {
                Quaternion targetRot = Quaternion.LookRotation(lookDir, Vector3.up);
                _panelRT.rotation = Quaternion.Slerp(_panelRT.rotation, targetRot, Time.deltaTime * transitionSpeed);
            }

            // Update projection line
            _projectionLine.SetPosition(0, wristTransform.position);
            _projectionLine.SetPosition(1, _panelRT.position - _panelRT.up * (panelHeight * globalScale * 0.5f)); // Bottom of the panel
            
            // Fade line with panel
            Color lineColor = new Color(0.2f, 0.8f, 1f, _currentAlpha * 0.5f);
            _projectionLine.startColor = lineColor;
            _projectionLine.endColor = new Color(0.2f, 0.8f, 1f, 0f); // Fade out towards menu
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

        // 2. Background (Holographic style)
        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(_panelRT, false);
        var bgRT = bgGo.AddComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.05f, 0.15f, 0.3f, 0.85f); // Dark blue translucent

        // Optional: Border/glow
        var borderGo = new GameObject("Border");
        borderGo.transform.SetParent(_panelRT, false);
        var borderRT = borderGo.AddComponent<RectTransform>();
        borderRT.anchorMin = Vector2.zero; borderRT.anchorMax = Vector2.one;
        borderRT.offsetMin = borderRT.offsetMax = Vector2.zero;
        var borderImg = borderGo.AddComponent<Image>();
        borderImg.color = new Color(0.2f, 0.8f, 1f, 0.4f);
        // Note: In a real project you might use a sprite with borders here.

        // 3. Header
        var headerRT = Pin("Header", bgRT, 0f, 50f);
        headerRT.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.3f, 0.6f, 0.9f);
        Label(headerRT, font, "SYSTEM MENU", 18f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);

        // 4. Dummy Content Buttons
        float y = -60f;
        string[] dummyLabels = { "Status", "Inventory", "Map", "Settings" };
        
        foreach (string label in dummyLabels)
        {
            var btnRT = AbsRow(bgRT, y, 45f, 10f);
            var btnImg = btnRT.gameObject.AddComponent<Image>();
            btnImg.color = new Color(0.1f, 0.25f, 0.4f, 0.8f);
            
            var btn = btnRT.gameObject.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.2f, 0.4f, 0.7f, 1f);
            colors.pressedColor = new Color(0.3f, 0.6f, 1f, 1f);
            btn.colors = colors;
            
            Label(btnRT, font, label, 14f, new Color(0.8f, 0.9f, 1f), FontStyles.Normal, TextAlignmentOptions.Center);
            
            y -= 55f;
        }
        
        // 5. Footer stats
        var footerRT = Pin("Footer", bgRT, -panelHeight + 40f, 40f);
        footerRT.anchorMin = new Vector2(0f, 0f);
        footerRT.anchorMax = new Vector2(1f, 0f);
        footerRT.pivot = new Vector2(0.5f, 0f);
        footerRT.anchoredPosition = Vector2.zero;
        footerRT.gameObject.AddComponent<Image>().color = new Color(0.05f, 0.1f, 0.2f, 0.9f);
        Label(footerRT, font, "Network: CONNECTED   Battery: 84%", 10f, new Color(0.4f, 0.9f, 0.6f), FontStyles.Normal, TextAlignmentOptions.Center);

        // 6. Projection Line setup
        var lineGo = new GameObject("ProjectionLine");
        lineGo.transform.SetParent(transform, false);
        _projectionLine = lineGo.AddComponent<LineRenderer>();
        _projectionLine.useWorldSpace = true;
        _projectionLine.startWidth = 0.0005f;
        _projectionLine.endWidth = 0.002f;
        _projectionLine.positionCount = 2;
        // Basic line material (can be upgraded in editor)
        _projectionLine.material = new Material(Shader.Find("Sprites/Default"));
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
        clip.Size = new Vector3(panelWidth, panelHeight, 10f);

        var cps = surfGO.AddComponent<ClippedPlaneSurface>();
        cps.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { clip });

        var poke = pokeGO.AddComponent<PokeInteractable>();
        poke.InjectAllPokeInteractable(cps);
        poke.InjectOptionalPointableElement(pc);
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
