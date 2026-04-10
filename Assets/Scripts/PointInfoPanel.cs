/*
 * PointInfoPanel.cs
 * ------------------
 * World-space UI panel showing info for a selected UMAP point.
 * Spawns in front of the user like a tablet; can be grabbed and moved with hands.
 * Supports Meta SDK Poke Interaction on the close button.
 *
 * SETUP:
 *  1. Create an empty GameObject, name it "InfoPanel".
 *  2. Add this component.
 *  3. Drag it into the "Info Panel" field on the UmapPointCloud component.
 *  4. Leave the GameObject ACTIVE in the scene — Start() hides it after one frame.
 */

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;

public class PointInfoPanel : MonoBehaviour
{
    private TextMeshProUGUI idValueText;
    private TextMeshProUGUI coordText;
    private Transform       closeBtnTransform;   // world-space center of close button

    // Hand references for grab + poke
    private OVRHand     rightHand,     leftHand;
    private Transform   rightAnchor,   leftAnchor;
    private OVRSkeleton rightSkeleton, leftSkeleton;

    private bool      panelGrabbed;
    private Transform grabbingAnchor;
    private Vector3   grabOffset;
    private bool      wasRightPinch, wasLeftPinch;
    private bool      wasPoking;

    void Start()
    {
        BuildCanvas();

        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            rightAnchor   = rig.rightHandAnchor;
            leftAnchor    = rig.leftHandAnchor;
            rightHand     = rig.rightHandAnchor.GetComponentInChildren<OVRHand>();
            leftHand      = rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
            rightSkeleton = rig.rightHandAnchor.GetComponentInChildren<OVRSkeleton>();
            leftSkeleton  = rig.leftHandAnchor.GetComponentInChildren<OVRSkeleton>();
        }

        // Wait one frame so PointableCanvas.Start() runs and registers before we hide.
        StartCoroutine(HideNextFrame());
    }

    System.Collections.IEnumerator HideNextFrame()
    {
        yield return null;
        if (gameObject.activeSelf)
            gameObject.SetActive(false);
    }

    void Update()
    {
        if (!gameObject.activeSelf) return;
        HandleGrab();
        CheckButtonPoke();
    }

    // ─────────────────────────────────────────────────────────────
    //  Hand Grab — pinch anywhere on the panel to move it
    // ─────────────────────────────────────────────────────────────

    void HandleGrab()
    {
        bool rPinch = rightHand != null && rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool lPinch = leftHand  != null && leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool rDown  = rPinch && !wasRightPinch;
        bool lDown  = lPinch && !wasLeftPinch;
        wasRightPinch = rPinch;
        wasLeftPinch  = lPinch;

        const float grabRadius = 0.20f;   // metres — generous so panel is easy to grab

        if (!panelGrabbed)
        {
            if (rDown && rightAnchor != null &&
                Vector3.Distance(rightAnchor.position, transform.position) < grabRadius)
            {
                panelGrabbed    = true;
                grabbingAnchor  = rightAnchor;
                grabOffset      = transform.position - rightAnchor.position;
            }
            else if (lDown && leftAnchor != null &&
                     Vector3.Distance(leftAnchor.position, transform.position) < grabRadius)
            {
                panelGrabbed    = true;
                grabbingAnchor  = leftAnchor;
                grabOffset      = transform.position - leftAnchor.position;
            }
        }
        else
        {
            bool stillPinching = (grabbingAnchor == rightAnchor) ? rPinch : lPinch;
            if (!stillPinching)
            {
                panelGrabbed   = false;
                grabbingAnchor = null;
            }
            else
            {
                transform.position = grabbingAnchor.position + grabOffset;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Fingertip Poke — close button
    // ─────────────────────────────────────────────────────────────

    void CheckButtonPoke()
    {
        if (closeBtnTransform == null) return;

        // Button center in world space (pivot is right-edge; step left by half button width)
        Vector3 btnCenter = closeBtnTransform.position
                          - closeBtnTransform.right * 0.022f;   // 22 mm = half of 44-unit button

        const float pokeRadius = 0.03f;   // 3 cm — covers the whole button comfortably

        bool poking = IsIndexTipNear(rightSkeleton, btnCenter, pokeRadius)
                   || IsIndexTipNear(leftSkeleton,  btnCenter, pokeRadius);

        if (poking && !wasPoking)   // fire on entry only
            Hide();

        wasPoking = poking;
    }

    bool IsIndexTipNear(OVRSkeleton skeleton, Vector3 point, float radius)
    {
        if (skeleton == null || !skeleton.IsInitialized) return false;
        var bones = skeleton.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Id == OVRSkeleton.BoneId.Hand_IndexTip)
                return Vector3.Distance(bones[i].Transform.position, point) < radius;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawns the panel ~0.6 m in front of the user's headset, facing them.
    /// </summary>
    public void Show(Transform camera, string sequenceId, Vector3 rawUmap)
    {
        if (idValueText == null || coordText == null)
        {
            Debug.LogWarning("[PointInfoPanel] Show() called before Start() finished building the canvas.");
            return;
        }

        if (camera != null)
        {
            // Project forward onto the horizontal plane so panel stays upright
            Vector3 forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();

            transform.position = camera.position + forward * 0.65f + Vector3.up * -0.05f;
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        gameObject.SetActive(true);
        idValueText.text = sequenceId;
        coordText.text   = $"UMAP\u2081 {rawUmap.x:F3}   UMAP\u2082 {rawUmap.y:F3}   UMAP\u2083 {rawUmap.z:F3}";
    }

    public void Hide()
    {
        panelGrabbed = false;
        gameObject.SetActive(false);
    }

    // ─────────────────────────────────────────────────────────────
    //  Canvas Construction
    // ─────────────────────────────────────────────────────────────

    void BuildCanvas()
    {
        // ── Canvas ────────────────────────────────────────────
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        gameObject.AddComponent<GraphicRaycaster>();

        // 1 canvas unit = 1 mm  →  panel is 0.32 m × 0.22 m in world space
        transform.localScale = Vector3.one * 0.001f;
        var canvasRT = GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(320, 220);

        // PointableCanvasModule extends PointerInputModule — must be on an EventSystem GameObject
        if (FindFirstObjectByType<PointableCanvasModule>() == null)
        {
            var es = FindFirstObjectByType<EventSystem>();
            if (es != null)
            {
                es.gameObject.AddComponent<PointableCanvasModule>();
                Debug.Log("[PointInfoPanel] Added PointableCanvasModule to existing EventSystem.");
            }
            else
            {
                var esGO = new GameObject("EventSystem");
                esGO.AddComponent<EventSystem>();
                esGO.AddComponent<PointableCanvasModule>();
                Debug.Log("[PointInfoPanel] Auto-created EventSystem + PointableCanvasModule.");
            }
        }

        // Wire Meta SDK poke interaction
        var pc = gameObject.AddComponent<PointableCanvas>();
        pc.InjectAllPointableCanvas(canvas);

        // Add PokeInteractable surface so PokeInteractor (building block) can interact with the panel
        AddPokeInteractable(pc);

        // Grab the default TMP font — MUST be set explicitly on programmatic TMP components
        var font = TMP_Settings.defaultFontAsset;

        // ── Outer background ──────────────────────────────────
        var bg = MakeRect("Background", transform, Vector2.zero, Vector2.one);
        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.color = new Color(0.06f, 0.06f, 0.15f, 0.95f);

        // ── Header ────────────────────────────────────────────
        var header = MakeRect("Header", bg,
            anchorMin: new Vector2(0f, 1f), anchorMax: Vector2.one,
            pivot: new Vector2(0.5f, 1f),
            sizeDelta: new Vector2(0f, 44f));
        var headerImg = header.gameObject.AddComponent<Image>();
        headerImg.color = new Color(0.18f, 0.18f, 0.42f, 1f);

        // Header title text
        var titleRT = MakeRect("TitleText", header, Vector2.zero, Vector2.one,
            offsetMin: new Vector2(12f, 0f), offsetMax: new Vector2(-48f, 0f));
        var titleTMP = titleRT.gameObject.AddComponent<TextMeshProUGUI>();
        titleTMP.font        = font;
        titleTMP.text        = "Point Selected";
        titleTMP.fontSize    = 14f;
        titleTMP.fontStyle   = FontStyles.Bold;
        titleTMP.color       = new Color(0.85f, 0.88f, 1f);
        titleTMP.alignment   = TextAlignmentOptions.MidlineLeft;
        titleTMP.raycastTarget = false;

        // Close button
        var closeBtnRT = MakeRect("CloseButton", header,
            anchorMin: new Vector2(1f, 0f), anchorMax: Vector2.one,
            pivot: new Vector2(1f, 0.5f),
            sizeDelta: new Vector2(44f, 0f));
        var closeBtnImg = closeBtnRT.gameObject.AddComponent<Image>();
        closeBtnImg.color = new Color(0.65f, 0.12f, 0.12f, 1f);

        var closeBtn = closeBtnRT.gameObject.AddComponent<Button>();
        closeBtn.targetGraphic = closeBtnImg;
        var colors = closeBtn.colors;
        colors.highlightedColor = new Color(0.85f, 0.20f, 0.20f);
        colors.pressedColor     = new Color(0.45f, 0.08f, 0.08f);
        closeBtn.colors = colors;
        closeBtn.onClick.AddListener(Hide);
        closeBtnTransform = closeBtnRT.transform;

        var closeXRT = MakeRect("X", closeBtnRT, Vector2.zero, Vector2.one);
        var closeXTMP = closeXRT.gameObject.AddComponent<TextMeshProUGUI>();
        closeXTMP.font      = font;
        closeXTMP.text      = "✕";
        closeXTMP.fontSize  = 16f;
        closeXTMP.fontStyle = FontStyles.Bold;
        closeXTMP.color     = Color.white;
        closeXTMP.alignment = TextAlignmentOptions.Center;
        closeXTMP.raycastTarget = false;

        // ── Content area (below header) ───────────────────────
        float contentTop = -44f;

        // "Sequence ID" label
        var idLabelRT = MakeRect("IdLabel", bg,
            anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
            pivot: new Vector2(0f, 1f),
            sizeDelta: new Vector2(-24f, 18f),
            anchoredPos: new Vector2(12f, contentTop - 8f));
        var idLabelTMP = idLabelRT.gameObject.AddComponent<TextMeshProUGUI>();
        idLabelTMP.font      = font;
        idLabelTMP.text      = "SEQUENCE ID";
        idLabelTMP.fontSize  = 9f;
        idLabelTMP.color     = new Color(0.50f, 0.75f, 1f);
        idLabelTMP.fontStyle = FontStyles.Bold;
        idLabelTMP.alignment = TextAlignmentOptions.MidlineLeft;
        idLabelTMP.raycastTarget = false;

        // Sequence ID value — full hash, word-wrapping
        var idValueRT = MakeRect("IdValue", bg,
            anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
            pivot: new Vector2(0f, 1f),
            sizeDelta: new Vector2(-24f, 68f),
            anchoredPos: new Vector2(12f, contentTop - 30f));
        var idTMP = idValueRT.gameObject.AddComponent<TextMeshProUGUI>();
        idTMP.font               = font;
        idTMP.text               = "—";
        idTMP.fontSize           = 10f;
        idTMP.color              = Color.white;
        idTMP.alignment          = TextAlignmentOptions.TopLeft;
        idTMP.enableWordWrapping = true;
        idTMP.overflowMode       = TextOverflowModes.Truncate;
        idTMP.raycastTarget      = false;
        idValueText = idTMP;

        // Thin divider line
        var divRT = MakeRect("Divider", bg,
            anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(1f, 0f),
            pivot: new Vector2(0.5f, 0f),
            sizeDelta: new Vector2(-24f, 1f),
            anchoredPos: new Vector2(0f, 38f));
        var divImg = divRT.gameObject.AddComponent<Image>();
        divImg.color = new Color(0.30f, 0.30f, 0.50f, 0.5f);

        // UMAP coordinates text — anchored to bottom
        var coordRT = MakeRect("Coords", bg,
            anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(1f, 0f),
            pivot: new Vector2(0f, 0f),
            sizeDelta: new Vector2(-24f, 28f),
            anchoredPos: new Vector2(12f, 8f));
        var coordTMP = coordRT.gameObject.AddComponent<TextMeshProUGUI>();
        coordTMP.font      = font;
        coordTMP.text      = "";
        coordTMP.fontSize  = 10f;
        coordTMP.color     = new Color(0.55f, 0.95f, 0.55f);
        coordTMP.alignment = TextAlignmentOptions.MidlineLeft;
        coordTMP.raycastTarget = false;
        coordText = coordTMP;
    }

    // ─────────────────────────────────────────────────────────────
    //  Poke Surface Setup
    // ─────────────────────────────────────────────────────────────

    // Creates a PokeInteractable covering the whole panel so the PokeInteractor (building block)
    // can detect fingertip contact and forward events to PointableCanvas → PointableCanvasModule.
    void AddPokeInteractable(PointableCanvas pc)
    {
        // Parent for the interactable — child of the canvas, inherits scale 0.001
        var pokeGO = new GameObject("PokeInteractable");
        pokeGO.transform.SetParent(transform, false);
        pokeGO.transform.localPosition = Vector3.zero;
        pokeGO.transform.localRotation = Quaternion.identity;
        pokeGO.transform.localScale    = Vector3.one;

        // Surface child — very thin in Z (a flat plane)
        var surfaceGO = new GameObject("PokeSurface");
        surfaceGO.transform.SetParent(pokeGO.transform, false);
        surfaceGO.transform.localPosition = Vector3.zero;
        surfaceGO.transform.localRotation = Quaternion.identity;
        surfaceGO.transform.localScale    = new Vector3(1f, 1f, 0.001f);

        // PlaneSurface — double-sided so poke works from either direction
        var plane = surfaceGO.AddComponent<PlaneSurface>();
        plane.InjectAllPlaneSurface(PlaneSurface.NormalFacing.Backward, true);

        // BoundsClipper — canvas is 320×220 canvas-units = 0.32×0.22 m world-space
        var clipper = surfaceGO.AddComponent<BoundsClipper>();
        clipper.Size = new Vector3(320f, 220f, 10f);

        // ClippedPlaneSurface implements ISurfacePatch required by PokeInteractable
        var clipped = surfaceGO.AddComponent<ClippedPlaneSurface>();
        clipped.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { clipper });

        // PokeInteractable: wires poke events through PointableCanvas → PointableCanvasModule → Unity UI
        var poke = pokeGO.AddComponent<PokeInteractable>();
        poke.InjectAllPokeInteractable(clipped);
        poke.InjectOptionalPointableElement(pc);
    }

    // ─────────────────────────────────────────────────────────────
    //  RectTransform Helpers
    // ─────────────────────────────────────────────────────────────

    static RectTransform MakeRect(string name, RectTransform parent,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2? pivot         = null,
        Vector2? sizeDelta     = null,
        Vector2? anchoredPos   = null,
        Vector2? offsetMin     = null,
        Vector2? offsetMax     = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        if (pivot.HasValue)       rt.pivot            = pivot.Value;
        if (sizeDelta.HasValue)   rt.sizeDelta        = sizeDelta.Value;
        if (anchoredPos.HasValue) rt.anchoredPosition = anchoredPos.Value;
        if (offsetMin.HasValue)   rt.offsetMin        = offsetMin.Value;
        if (offsetMax.HasValue)   rt.offsetMax        = offsetMax.Value;
        return rt;
    }

    static RectTransform MakeRect(string name, Transform parent,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2? offsetMin = null, Vector2? offsetMax = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin ?? Vector2.zero;
        rt.offsetMax = offsetMax ?? Vector2.zero;
        return rt;
    }
}
