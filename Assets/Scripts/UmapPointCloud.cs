/*
 * UmapPointCloud.cs
 * -----------------
 * Renders and enables VR interaction with a 3D UMAP point cloud on Meta Quest 3.
 *
 * SETUP (one-time in Unity Editor):
 *  1. Create an empty GameObject in SampleScene, name it "UmapPointCloud".
 *  2. Add this script as a component.
 *  3. Set its Transform Position to roughly (0, 1.4, -1.0) so it appears at eye level in front of you.
 *  4. (Optional) Create a world-space TextMeshPro object for the info label and drag it into the
 *     "Info Label" field on this component.
 *  5. Press Play / Build & Run — the CSV is read from StreamingAssets at runtime.
 *
 * CONTROLS (Quest 3):
 *  Controllers:
 *    Right grip          — grab & move/rotate the cloud
 *    Left grip           — grab with left hand
 *    Both grips          — two-hand scale (spread = zoom in)
 *    Right joystick Y    — fine scale
 *    Right index trigger — select hovered point
 *
 *  Hand tracking:
 *    Index pinch (thumb+index) while pointing at EMPTY SPACE — grab & move/rotate
 *    Index pinch (thumb+index) while pointing at a POINT     — select that point
 *    Both hands pinching                                      — two-hand scale
 */

using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;

[AddComponentMenu("UMAP/Point Cloud")]
public class UmapPointCloud : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────────

    [Header("Visual")]
    [Tooltip("Base point radius in world-space meters at cloud scale = 1")]
    [SerializeField] private float basePointSize = 0.010f;

    [Tooltip("World-space radius (meters) around the index fingertip that detects nearby points")]
    [SerializeField] private float proximityRadius = 0.05f;

    [Header("Info Display")]
    [Tooltip("Optional world-space TextMeshPro that shows the hovered/selected point ID")]
    [SerializeField] private TMPro.TextMeshPro infoLabel;

    [Tooltip("Panel that pops up with full point info when a point is selected")]
    [SerializeField] private PointInfoPanel infoPanel;

    // ─────────────────────────────────────────────────────────────
    //  Data
    // ─────────────────────────────────────────────────────────────

    private Vector3[] localPositions;   // normalized to [-0.5, 0.5]^3
    private NativeArray<Vector3> nativePositions;
    private NativeArray<int> nativeResultIndex;
    private Vector3[] rawPositions;     // original UMAP1/2/3 values from CSV
    private Color[]   pointColors;
    private string[]  sequenceIds;
    private int       pointCount;

    // ─────────────────────────────────────────────────────────────
    //  Rendering  (MeshRenderer — stereo-safe, no DrawMeshInstancedIndirect)
    // ─────────────────────────────────────────────────────────────

    private Mesh         cloudMesh;
    private Material     cloudMaterial;
    private MeshRenderer cloudRenderer;
    private Color32[]    _meshColors;       // CPU copy of vertex colors for runtime pin-color updates
    private Color32[]    _metadataColors;   // per-point colors loaded from local binary (detection method)
    private bool         _isMetadataColorMode = false;
    public  bool         IsMetadataColorMode => _isMetadataColorMode;

    private static readonly int ShaderPointSize = Shader.PropertyToID("_PointSize");
    private static readonly int ShaderMoveTint  = Shader.PropertyToID("_MoveTint");
    private bool _prevMovementEnabled = false;

    // ─────────────────────────────────────────────────────────────
    //  Highlight marker (a simple sphere — stereo-correct, no shader tricks)
    // ─────────────────────────────────────────────────────────────

    private Transform    highlightMarker;
    private MeshRenderer highlightRenderer;
    private Material     highlightMat;       // cached instance — avoids .material accessor overhead

    // ─────────────────────────────────────────────────────────────
    //  OVR References
    // ─────────────────────────────────────────────────────────────

    private Transform rightAnchor, leftAnchor;
    private Transform rightAimAnchor;
    private Transform cameraTransform;
    private OVRHand     rightHand, leftHand;
    private OVRSkeleton rightSkel, leftSkel;
    private LineRenderer aimRay;

    // ─────────────────────────────────────────────────────────────
    //  Interaction State
    // ─────────────────────────────────────────────────────────────

    [Header("Interaction")]
    public bool isMovementEnabled = false;
    private bool wasControllerGrab = false;

    private enum GrabState { None, Right, Left, Both }
    private GrabState currentGrabState = GrabState.None;

    private Vector3    grabStartPosR, grabStartPosL;
    private Quaternion grabStartRotR, grabStartRotL;
    private Vector3    cloudPosAtGrab;
    private Quaternion cloudRotAtGrab;
    private float      cloudScaleAtGrab;
    private float      twoHandDistAtGrab;

    private int  hoveredIndex  = -1;
    private int  selectedIndex = -1;

    // Previous-frame pinch state for edge detection
    private bool wasRightPinch;
    private bool wasLeftPinch;

    // Cooldown to suppress rapid-fire re-selections from pinch jitter
    private float _lastSelectTime = -10f;
    private const float SelectCooldown = 0.5f;

    // ── Pinned panels ─────────────────────────────────────────────
    private readonly System.Collections.Generic.List<PointInfoPanel> _pinnedPanels =
        new System.Collections.Generic.List<PointInfoPanel>();
    private readonly System.Collections.Generic.Dictionary<int, GameObject> _pinnedMarkers =
        new System.Collections.Generic.Dictionary<int, GameObject>();
    private int _nextPinColorIdx = 0;
    private static readonly Color[] PinColors = {
        new Color(1.00f, 0.45f, 0.00f),  // orange
        new Color(0.95f, 0.15f, 0.85f),  // pink
        new Color(0.60f, 0.15f, 1.00f),  // purple
        new Color(0.90f, 0.90f, 0.00f),  // yellow
        new Color(0.15f, 1.00f, 0.15f),  // green
        new Color(1.00f, 0.20f, 0.20f),  // red
    };

    // ─────────────────────────────────────────────────────────────
    //  Unity Lifecycle
    // ─────────────────────────────────────────────────────────────

    void Awake()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            rightAnchor     = rig.rightHandAnchor;
            leftAnchor      = rig.leftHandAnchor;
            rightAimAnchor  = rig.rightControllerAnchor;
            cameraTransform = rig.centerEyeAnchor;

            rightHand = rig.rightHandAnchor.GetComponentInChildren<OVRHand>();
            leftHand  = rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
            rightSkel = rig.rightHandAnchor.GetComponentInChildren<OVRSkeleton>();
            leftSkel  = rig.leftHandAnchor.GetComponentInChildren<OVRSkeleton>();
        }
        else
        {
            Debug.LogWarning("[UmapPointCloud] OVRCameraRig not found — interaction will be limited.");
        }
    }

    void Start()
    {
        SetupAimRay();
        SetupHighlightMarker();
        StartCoroutine(LoadCSV());
        StartCoroutine(LoadMetadataColors());

        // Deselect the point whenever the info panel is closed (close button, or click-off)
        if (infoPanel != null)
            WireActivePanel(infoPanel);
    }

    void SetupAimRay()
    {
        var go = new GameObject("AimRay");
        go.transform.SetParent(transform.parent);
        aimRay = go.AddComponent<LineRenderer>();
        aimRay.positionCount     = 2;
        aimRay.useWorldSpace     = true;
        aimRay.startWidth        = 0.004f;
        aimRay.endWidth          = 0.001f;
        aimRay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        aimRay.receiveShadows    = false;
        var mat = new Material(Shader.Find("Sprites/Default"));
        aimRay.material    = mat;
        aimRay.startColor  = new Color(0.4f, 0.9f, 1f, 0.9f);
        aimRay.endColor    = new Color(0.4f, 0.9f, 1f, 0f);
        aimRay.enabled     = false;
    }

    void SetupHighlightMarker()
    {
        // A sphere child of this transform — scales/moves with the cloud automatically.
        // Being a real GameObject it renders correctly in both eyes without any shader tricks.
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "HighlightMarker";
        go.transform.SetParent(transform);
        go.transform.localScale    = Vector3.zero; // hidden until first hover
        go.transform.localPosition = Vector3.zero;
        Destroy(go.GetComponent<SphereCollider>());

        highlightMarker   = go.transform;
        highlightRenderer = go.GetComponent<MeshRenderer>();
        highlightRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        highlightRenderer.receiveShadows    = false;

        // Unlit so it's fully visible in passthrough regardless of lighting
        highlightMat = new Material(Shader.Find("Unlit/Color")) { color = Color.white };
        highlightRenderer.material = highlightMat;
    }

    void Update()
    {
        if (cloudMaterial == null || cloudRenderer == null) return;

        HandleInteraction();

        // When the cloud scale changes, keep selection/pin markers at constant world size.
        if (transform.hasChanged)
        {
            float invScale = transform.localScale.x > 1e-5f ? 1f / transform.localScale.x : 1f;
            foreach (var kvp in _pinnedMarkers)
                if (kvp.Value != null)
                    kvp.Value.transform.localScale = Vector3.one * basePointSize * 1.5f * invScale;
            transform.hasChanged = false;
        }

        // Tint all points when in move mode so the mode is visually unambiguous.
        if (isMovementEnabled != _prevMovementEnabled)
        {
            _prevMovementEnabled = isMovementEnabled;
            cloudMaterial.SetColor(ShaderMoveTint,
                isMovementEnabled ? new Color(1f, 0.55f, 0.15f) : Color.white);
        }

        UpdateHover();

        UpdateAimRay();
        UpdateHighlightMarker();
        UpdateInfoPanelPosition();
        // MeshRenderer draws automatically — no manual Draw* call needed.
    }

    void OnDestroy()
    {
        if (nativePositions.IsCreated) nativePositions.Dispose();
        if (nativeResultIndex.IsCreated) nativeResultIndex.Dispose();
        if (cloudMesh != null)     Destroy(cloudMesh);
        if (cloudMaterial != null) Destroy(cloudMaterial);
        foreach (var go in _pinnedMarkers.Values) if (go != null) Destroy(go);
        _pinnedMarkers.Clear();
    }

    // ─────────────────────────────────────────────────────────────
    //  CSV Loading
    // ─────────────────────────────────────────────────────────────

    IEnumerator LoadCSV()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "umap_thinned.csv");

#if UNITY_ANDROID && !UNITY_EDITOR
        using var req = UnityWebRequest.Get(path);
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[UmapPointCloud] Failed to load CSV: {req.error}");
            yield break;
        }
        string csvText = req.downloadHandler.text;
#else
        string csvText = File.ReadAllText(path);
        yield return null;
#endif
        ParseCSV(csvText);
        InitGPU();
    }

    void ParseCSV(string text)
    {
        string[] lines = text.Split('\n');

        int n = 0;
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Trim().Length > 6) n++;

        localPositions = new Vector3[n];
        rawPositions   = new Vector3[n];
        pointColors    = new Color[n];
        sequenceIds    = new string[n];

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        int idx = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length < 6) continue;
            string[] parts = line.Split(',');
            if (parts.Length < 4) continue;
            if (!ParseFloat(parts[0], out float u1) ||
                !ParseFloat(parts[1], out float u2) ||
                !ParseFloat(parts[2], out float u3)) continue;

            localPositions[idx] = new Vector3(u1, u2, u3);
            rawPositions[idx]   = new Vector3(u1, u2, u3);   // keep original before normalisation
            sequenceIds[idx]    = parts[3].Trim();
            if (u1 < minX) minX = u1; if (u1 > maxX) maxX = u1;
            if (u2 < minY) minY = u2; if (u2 > maxY) maxY = u2;
            if (u3 < minZ) minZ = u3; if (u3 > maxZ) maxZ = u3;
            idx++;
        }

        pointCount = idx;
        float range = Mathf.Max(maxX - minX, maxY - minY, maxZ - minZ);
        if (range < 1e-5f) range = 1f;
        var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);

        for (int i = 0; i < pointCount; i++)
        {
            localPositions[i] = (localPositions[i] - center) / range;
            float t = Mathf.InverseLerp(-0.5f, 0.5f, localPositions[i].z);
            pointColors[i] = Viridis(t);
        }

        nativePositions = new NativeArray<Vector3>(localPositions, Allocator.Persistent);
        nativeResultIndex = new NativeArray<int>(1, Allocator.Persistent);

        Debug.Log($"[UmapPointCloud] Parsed {pointCount} points.");
    }

    static bool ParseFloat(string s, out float v) =>
        float.TryParse(s.Trim(),
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out v);

    // ─────────────────────────────────────────────────────────────
    //  Metadata Color Loading
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the per-point RGB binary from StreamingAssets (3 bytes per point, matching CSV order).
    /// File: color_detection_method_RBPdetect2.bin — produced by the download script.
    /// </summary>
    IEnumerator LoadMetadataColors()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "color_detection_method_RBPdetect2.bin");

#if UNITY_ANDROID && !UNITY_EDITOR
        using var req = UnityWebRequest.Get(path);
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        { Debug.LogWarning($"[UmapPointCloud] Could not load metadata colors: {req.error}"); yield break; }
        byte[] raw = req.downloadHandler.data;
#else
        if (!File.Exists(path))
        { Debug.LogWarning($"[UmapPointCloud] Metadata color file not found: {path}"); yield break; }
        byte[] raw = File.ReadAllBytes(path);
        yield return null;
#endif

        int n = raw.Length / 3;
        _metadataColors = new Color32[n * 4]; // 4 verts per point
        for (int i = 0; i < n; i++)
        {
            var c = new Color32(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2], 255);
            for (int k = 0; k < 4; k++)
                _metadataColors[i * 4 + k] = c;
        }
        Debug.Log($"[UmapPointCloud] Loaded metadata colors for {n} points.");
    }

    /// <summary>
    /// Toggles between viridis (depth-based) coloring and the metadata color scheme.
    /// Pinned point colors are preserved on top of whichever base is active.
    /// </summary>
    public void ToggleMetadataColors()
    {
        if (_meshColors == null || cloudMesh == null) return;
        if (_metadataColors == null) { Debug.LogWarning("[UmapPointCloud] Metadata colors not loaded yet."); return; }

        _isMetadataColorMode = !_isMetadataColorMode;

        // Rebuild base colors from the appropriate source
        Color32[] source = _isMetadataColorMode ? _metadataColors : null;
        for (int i = 0; i < pointCount; i++)
        {
            Color32 c = source != null ? source[i * 4] : (Color32)pointColors[i];
            for (int k = 0; k < 4; k++)
                _meshColors[i * 4 + k] = c;
        }

        // Re-apply pinned point colors on top (they override the base)
        foreach (var kvp in _pinnedPanels)
        {
            int idx = kvp.CurrentPointIndex;
            if (idx >= 0 && idx < pointCount)
            {
                Color32 pc = kvp.PinColor;
                for (int k = 0; k < 4; k++)
                    _meshColors[idx * 4 + k] = pc;
            }
        }

        cloudMesh.colors32 = _meshColors;
    }

    // ─────────────────────────────────────────────────────────────
    //  Mesh + Renderer Initialisation
    // ─────────────────────────────────────────────────────────────

    void InitGPU()
    {
        var shader = Shader.Find("Custom/PointCloud");
        if (shader == null)
        {
            Debug.LogError("[UmapPointCloud] Shader 'Custom/PointCloud' not found.");
            return;
        }
        cloudMaterial = new Material(shader) { name = "UmapPointCloudMaterial" };

        // Build a static mesh: 4 vertices per point (billboard quad).
        // All 4 vertices share the same cloud-local centre position; the shader
        // offsets them in clip-space based on UV corner, so they expand into a disc.
        //
        // Using a real Mesh + MeshRenderer lets Unity's standard rendering pipeline
        // dispatch stereo (multiview / SPI / multi-pass) correctly without any
        // manual instance-ID arithmetic.
        int vCount = pointCount * 4;

        var verts  = new Vector3[vCount];
        var uvs    = new Vector2[vCount];
        var cols   = new Color32[vCount];
        var tris   = new int[pointCount * 6];

        // UV corners map to billboard offsets: (uv - 0.5) in the vertex shader.
        var corners = new Vector2[]
        {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1),
        };

        for (int i = 0; i < pointCount; i++)
        {
            Color32 c = pointColors[i];
            for (int k = 0; k < 4; k++)
            {
                int vi    = i * 4 + k;
                verts[vi] = localPositions[i];  // same centre for all 4 corners
                uvs[vi]   = corners[k];
                cols[vi]  = c;
            }
            int ti = i * 6, v0 = i * 4;
            tris[ti+0] = v0;   tris[ti+1] = v0+2; tris[ti+2] = v0+1;
            tris[ti+3] = v0;   tris[ti+4] = v0+3; tris[ti+5] = v0+2;
        }

        _meshColors = cols;  // keep CPU copy so we can recolor individual points at runtime

        cloudMesh          = new Mesh { name = "PointCloudMesh" };
        cloudMesh.vertices = verts;
        cloudMesh.uv       = uvs;
        cloudMesh.colors32 = cols;
        cloudMesh.triangles = tris;
        // Local bounds cover the normalised [-0.5, 0.5]³ cloud with a little margin.
        cloudMesh.bounds   = new Bounds(Vector3.zero, Vector3.one * 1.5f);
        // Note: not calling UploadMeshData(true) so we can update vertex colors for pinned points

        var mf         = gameObject.AddComponent<MeshFilter>();
        mf.sharedMesh  = cloudMesh;

        cloudRenderer                    = gameObject.AddComponent<MeshRenderer>();
        cloudRenderer.sharedMaterial     = cloudMaterial;
        cloudRenderer.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
        cloudRenderer.receiveShadows     = false;

        cloudMaterial.SetFloat(ShaderPointSize, basePointSize);

        Debug.Log($"[UmapPointCloud] Mesh ready ({pointCount} points, {vCount} verts).");
    }

    // ─────────────────────────────────────────────────────────────
    //  Interaction
    // ─────────────────────────────────────────────────────────────

    void HandleInteraction()
    {
        // ── Raw input ───────────────────────────────────────────
        bool rTracked = OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch);
        bool lTracked = OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch);

        bool rControllerGrip = rTracked && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool lControllerGrip = lTracked && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);

        // If holding active controller grips, immediately activate move mode implicitly
        if (rControllerGrip || lControllerGrip)
        {
            if (!wasControllerGrab)
            {
                isMovementEnabled = true;
                wasControllerGrab = true;
            }
        }
        else if (wasControllerGrab)
        {
            isMovementEnabled = false;
            wasControllerGrab = false;
        }

        bool rIndexPinch = rightHand != null && rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool lIndexPinch = leftHand  != null && leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);

        // Edge: pinch started this frame
        bool rPinchDown = rIndexPinch && !wasRightPinch;
        bool lPinchDown = lIndexPinch && !wasLeftPinch;
        wasRightPinch   = rIndexPinch;
        wasLeftPinch    = lIndexPinch;

        // Controller index trigger (separate button from grip — no conflict)
        bool rTriggerDown = OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger);

        // ── Click detection ─────────────────────────────────────
        bool anyInput = (!isMovementEnabled) && (rTriggerDown || rPinchDown || lPinchDown);

        if (anyInput)
        {
            if (hoveredIndex >= 0 && Time.time - _lastSelectTime >= SelectCooldown)
            {
                // Point within proximity — select it
                _lastSelectTime = Time.time;
                selectedIndex = hoveredIndex;
                Debug.Log($"[UmapPointCloud] SELECTED {selectedIndex}: {sequenceIds[selectedIndex]}");

                if (infoPanel != null)
                    infoPanel.Show(cameraTransform, sequenceIds[selectedIndex], rawPositions[selectedIndex], selectedIndex);
                else
                    ShowInfoLabel(selectedIndex, "SELECTED\n");

                StartCoroutine(TriggerSelectHaptics());
            }
            else if (infoPanel != null && infoPanel.gameObject.activeSelf)
            {
                // Don't dismiss if either hand is near any panel face (active or pinned)
                bool handNearPanel = false;
                bool anyProteinReleased = false;

                if (infoPanel != null)
                {
                    handNearPanel |= (rightAnchor != null && infoPanel.IsHandNearForGrab(rightAnchor.position)) ||
                                     (leftAnchor  != null && infoPanel.IsHandNearForGrab(leftAnchor.position));
                    anyProteinReleased |= infoPanel.IsProteinReleased;
                }

                foreach (var p in _pinnedPanels)
                {
                    handNearPanel |= (rightAnchor != null && p.IsHandNearForGrab(rightAnchor.position)) ||
                                     (leftAnchor  != null && p.IsHandNearForGrab(leftAnchor.position));
                    anyProteinReleased |= p.IsProteinReleased;
                }

                // Don't dismiss if the released protein is floating — the pinch is almost
                // certainly a protein grab, not an intent to close the panel.
                if (!handNearPanel && !anyProteinReleased)
                    infoPanel.Hide();
            }

            return; // don't start a grab on the same frame as a click
        }

        // ── Grab input: controller grip OR pinch-while-NOT-hovering ─
        // Pinch while hovering = click (handled above), so a pinch reaching here means empty space.
        bool rGrip = (rControllerGrip || rIndexPinch) && isMovementEnabled;
        bool lGrip = (lControllerGrip || lIndexPinch) && isMovementEnabled;

        Vector3    rPos = rightAnchor ? rightAnchor.position : Vector3.zero;
        Vector3    lPos = leftAnchor  ? leftAnchor.position  : Vector3.zero;
        Quaternion rRot = rightAnchor ? rightAnchor.rotation : Quaternion.identity;
        Quaternion lRot = leftAnchor  ? leftAnchor.rotation  : Quaternion.identity;

        // ── Two-hand grab: scale + translate ───────────────────
        GrabState newState = GrabState.None;
        if (rGrip && lGrip) newState = GrabState.Both;
        else if (rGrip)     newState = GrabState.Right;
        else if (lGrip)     newState = GrabState.Left;

        // If the state changed (e.g. dropping one hand, or grabbing a new hand), snapshot the anchors!
        if (newState != currentGrabState)
        {
            currentGrabState = newState;
            cloudPosAtGrab = transform.position;
            cloudRotAtGrab = transform.rotation;

            if (newState == GrabState.Both)
            {
                grabStartPosR     = rPos;
                grabStartPosL     = lPos;
                cloudScaleAtGrab  = transform.localScale.x;
                twoHandDistAtGrab = Mathf.Max(Vector3.Distance(rPos, lPos), 0.01f);
            }
            else if (newState == GrabState.Right)
            {
                grabStartPosR = rPos;
                grabStartRotR = rRot;
            }
            else if (newState == GrabState.Left)
            {
                grabStartPosL = lPos;
                grabStartRotL = lRot;
            }
        }

        // Apply movement based on the current state
        if (currentGrabState == GrabState.Both)
        {
            float newDist  = Mathf.Max(Vector3.Distance(rPos, lPos), 0.01f);
            float newScale = Mathf.Clamp(cloudScaleAtGrab * (newDist / twoHandDistAtGrab), 0.05f, 10f);
            transform.localScale = Vector3.one * newScale;
            Vector3 midNow = (rPos + lPos) * 0.5f;
            Vector3 mid0   = (grabStartPosR + grabStartPosL) * 0.5f;
            transform.position = cloudPosAtGrab + (midNow - mid0);
        }
        else if (currentGrabState == GrabState.Right)
        {
            Quaternion delta = rRot * Quaternion.Inverse(grabStartRotR);
            transform.rotation = delta * cloudRotAtGrab;
            transform.position = rPos + delta * (cloudPosAtGrab - grabStartPosR);
        }
        else if (currentGrabState == GrabState.Left)
        {
            Quaternion delta = lRot * Quaternion.Inverse(grabStartRotL);
            transform.rotation = delta * cloudRotAtGrab;
            transform.position = lPos + delta * (cloudPosAtGrab - grabStartPosL);
        }

        // ── Right joystick Y → fine scale ──────────────────────
        float joy = isMovementEnabled ? OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y : 0f;
        if (Mathf.Abs(joy) > 0.1f)
        {
            float s = Mathf.Clamp(transform.localScale.x * (1f + joy * Time.deltaTime * 1.5f), 0.05f, 10f);
            transform.localScale = Vector3.one * s;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Hover Detection
    // ─────────────────────────────────────────────────────────────

    void UpdateHover()
    {
        if (localPositions == null || !nativePositions.IsCreated) return;

        if (isMovementEnabled)
        {
            if (hoveredIndex >= 0)
            {
                hoveredIndex = -1;
                if (infoLabel != null && selectedIndex < 0) infoLabel.gameObject.SetActive(false);
            }
            return;
        }

        Matrix4x4 w2l      = transform.worldToLocalMatrix;
        float     worldScale = transform.lossyScale.x;
        float     localR     = worldScale > 1e-4f ? proximityRadius / worldScale : proximityRadius;
        float     threshSq   = localR * localR;

        // Try right hand first; fall back to left hand if nothing found in range
        Vector3 rTipLocal = w2l.MultiplyPoint3x4(GetHandTipPosition(rightSkel, rightAnchor));
        new FindNearestProximityJob
        {
            positions = nativePositions, tipLocal = rTipLocal,
            threshSq  = threshSq, pointCount = pointCount, resultIndex = nativeResultIndex,
        }.Schedule().Complete();

        int nearest = nativeResultIndex[0];

        if (nearest < 0)
        {
            Vector3 lTipLocal = w2l.MultiplyPoint3x4(GetHandTipPosition(leftSkel, leftAnchor));
            new FindNearestProximityJob
            {
                positions = nativePositions, tipLocal = lTipLocal,
                threshSq  = threshSq, pointCount = pointCount, resultIndex = nativeResultIndex,
            }.Schedule().Complete();
            nearest = nativeResultIndex[0];
        }
        if (nearest == hoveredIndex) return;
        hoveredIndex = nearest;

        if (infoLabel == null || selectedIndex >= 0) return;

        if (hoveredIndex >= 0)
        {
            Vector3 wp = transform.TransformPoint(localPositions[hoveredIndex]);
            infoLabel.transform.position = wp + transform.up * 0.07f;
            infoLabel.text = "ID: " + TruncateId(sequenceIds[hoveredIndex], 20);
            infoLabel.gameObject.SetActive(true);
        }
        else
        {
            infoLabel.gameObject.SetActive(false);
        }
    }

    // Returns the world-space index fingertip position for the given hand.
    //
    // Quest 3 uses the XRHand bone system (SkeletonType.XRHandRight/Left), NOT the old Hand_* system.
    // The two systems share numeric values that COLLIDE:
    //   Hand_IndexTip  = 20  ==  XRHand_RingTip  (ring finger tip!)
    //   Hand_Index3    =  8  ==  XRHand_IndexIntermediate
    //   XRHand_IndexTip    = 10  (correct for XR)
    //   XRHand_IndexDistal =  9  (distal joint, ~1 cm from tip)
    // We therefore check the skeleton type at runtime and pick the right constant.
    Vector3 GetHandTipPosition(OVRSkeleton skel, Transform anchor)
    {
        if (skel != null && skel.IsInitialized)
        {
            bool isXR = skel.GetSkeletonType() == OVRSkeleton.SkeletonType.XRHandRight
                     || skel.GetSkeletonType() == OVRSkeleton.SkeletonType.XRHandLeft;

            OVRSkeleton.BoneId tipId    = isXR ? OVRSkeleton.BoneId.XRHand_IndexTip
                                               : OVRSkeleton.BoneId.Hand_IndexTip;
            OVRSkeleton.BoneId distalId = isXR ? OVRSkeleton.BoneId.XRHand_IndexDistal
                                               : OVRSkeleton.BoneId.Hand_Index3;

            Vector3 distalPos   = Vector3.zero;
            bool    foundDistal = false;
            foreach (var b in skel.Bones)
            {
                if (b.Id == tipId)    return b.Transform.position;
                if (b.Id == distalId) { distalPos = b.Transform.position; foundDistal = true; }
            }
            if (foundDistal) return distalPos;
        }
        return anchor != null ? anchor.position : Vector3.zero;
    }

    // ─────────────────────────────────────────────────────────────
    //  Highlight Sphere
    // ─────────────────────────────────────────────────────────────

    void UpdateHighlightMarker()
    {
        if (highlightMarker == null || localPositions == null) return;

        int activeIdx = selectedIndex >= 0 ? selectedIndex : hoveredIndex;

        if (activeIdx < 0)
        {
            highlightMarker.localScale = Vector3.zero;
            return;
        }

        highlightMarker.localPosition = localPositions[activeIdx];

        // Local-space size: cloud spans ~1.0 units; basePointSize is world-space at scale=1
        // so in local space it equals basePointSize (before the cloud's own scale is applied).
        float localSize;
        Color col = pointColors[activeIdx];
        if (selectedIndex >= 0)
        {
            // Selected: very subtle scale bounce — small amplitude, slow frequency
            float bounce = 1f + 0.04f * Mathf.Sin(Time.time * 2f);
            localSize = basePointSize * 2.5f * bounce;
            // Distinct cyan accent so selected is unambiguous without being huge
            col = Color.Lerp(col, new Color(0.2f, 0.95f, 1f), 0.85f);
        }
        else
        {
            // Proximity hover: gentle pulse signals "pinch here to select"
            float pulse = 1f + 0.08f * Mathf.Sin(Time.time * 3f);
            localSize = basePointSize * 2.8f * pulse;
            col = Color.Lerp(col, Color.white, 0.45f);
        }
        // Divide by cloud scale so the sphere stays constant world size
        float cloudScale = transform.localScale.x > 1e-5f ? transform.localScale.x : 1f;
        highlightMarker.localScale = Vector3.one * localSize / cloudScale;
        highlightMat.color = col;
    }

    // ─────────────────────────────────────────────────────────────
    //  Haptics
    // ─────────────────────────────────────────────────────────────

    // Double-tap haptic: strong click, tiny gap, softer click
    // Uses OVRInput.SetControllerVibration which works regardless of OVRHaptics init state
    System.Collections.IEnumerator TriggerSelectHaptics()
    {
        var right = OVRInput.Controller.RTouch;

        // First tap — strong
        OVRInput.SetControllerVibration(0.5f, 1.0f, right);
        yield return new WaitForSeconds(0.06f);

        // Brief silence
        OVRInput.SetControllerVibration(0f, 0f, right);
        yield return new WaitForSeconds(0.04f);

        // Second tap — softer
        OVRInput.SetControllerVibration(0.3f, 0.6f, right);
        yield return new WaitForSeconds(0.05f);

        OVRInput.SetControllerVibration(0f, 0f, right);
    }

    void UpdateInfoPanelPosition()
    {
        // Panel is now a free-floating tablet — grab handled by PointInfoPanel itself.
    }

    // ─────────────────────────────────────────────────────────────
    //  Aim Ray
    // ─────────────────────────────────────────────────────────────

    void UpdateAimRay()
    {
        // Aim ray removed — selection is now proximity-based, not ray-cast.
        if (aimRay != null) aimRay.enabled = false;
    }

    // ─────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────

    void ShowInfoLabel(int index, string prefix = "")
    {
        if (infoLabel == null) return;
        Vector3 wp = transform.TransformPoint(localPositions[index]);
        infoLabel.transform.position = wp + transform.up * 0.09f;
        infoLabel.text = prefix + TruncateId(sequenceIds[index], 24);
        infoLabel.gameObject.SetActive(true);
    }

    static string TruncateId(string id, int maxLen) =>
        id.Length <= maxLen ? id : id.Substring(0, maxLen) + "…";

static Color Viridis(float t)
    {
        t = Mathf.Clamp01(t);
        Color c0 = new Color(0.267f, 0.005f, 0.329f);
        Color c1 = new Color(0.190f, 0.407f, 0.574f);
        Color c2 = new Color(0.128f, 0.566f, 0.551f);
        Color c3 = new Color(0.369f, 0.788f, 0.384f);
        Color c4 = new Color(0.993f, 0.906f, 0.144f);
        if (t < 0.25f) return Color.Lerp(c0, c1, t / 0.25f);
        if (t < 0.50f) return Color.Lerp(c1, c2, (t - 0.25f) / 0.25f);
        if (t < 0.75f) return Color.Lerp(c2, c3, (t - 0.50f) / 0.25f);
        return                Color.Lerp(c3, c4, (t - 0.75f) / 0.25f);
    }

    // ─────────────────────────────────────────────────────────────
    //  Panel pinning
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Wire up the active (unpinned) info panel: assign a pin color, hook onHide to reset
    /// selection state, and hook onPin to handle the panel being pinned.
    /// </summary>
    void WireActivePanel(PointInfoPanel p)
    {
        p.PinColor = PinColors[_nextPinColorIdx % PinColors.Length];
        _nextPinColorIdx++;
        // Use assignment (not +=) so we always replace any leftover handlers from before
        p.onHide = () => { selectedIndex = -1; infoLabel?.gameObject.SetActive(false); };
        p.onPin  = (pointIdx, color) => OnPanelPinned(p, pointIdx, color);
    }

    /// <summary>
    /// Called when the user pins an info panel. Records it as pinned, colors the cloud point,
    /// replaces its onHide with the pinned-panel cleanup handler, and creates a fresh active panel.
    /// </summary>
    void OnPanelPinned(PointInfoPanel panel, int pointIdx, Color color)
    {
        _pinnedPanels.Add(panel);

        // Color the cloud point and add a slightly-larger sphere marker to match the pin
        if (pointIdx >= 0)
        {
            SetPointColor(pointIdx, color);
            CreatePinnedMarker(pointIdx, color);
        }

        // Replace the active-panel onHide with pinned-panel cleanup
        panel.onHide = () =>
        {
            if (pointIdx >= 0) RestorePointColor(pointIdx);
            _pinnedPanels.Remove(panel);
        };
        panel.onPin = null;  // prevent double-pinning

        // Create a new active panel for the next selection
        CreateNewActivePanel();
    }

    void CreateNewActivePanel()
    {
        var go = new GameObject("InfoPanel");
        infoPanel = go.AddComponent<PointInfoPanel>();
        WireActivePanel(infoPanel);
    }

    void SetPointColor(int index, Color color)
    {
        if (_meshColors == null || cloudMesh == null) return;
        if (index < 0 || index >= pointCount) return;
        Color32 c32 = color;
        for (int k = 0; k < 4; k++)
            _meshColors[index * 4 + k] = c32;
        cloudMesh.colors32 = _meshColors;
    }

    void RestorePointColor(int index)
    {
        if (pointColors == null || index < 0 || index >= pointCount) return;
        Color baseColor = (_isMetadataColorMode && _metadataColors != null)
            ? (Color)_metadataColors[index * 4]
            : pointColors[index];
        SetPointColor(index, baseColor);
        DestroyPinnedMarker(index);
    }

    void CreatePinnedMarker(int index, Color color)
    {
        if (_pinnedMarkers.ContainsKey(index) || localPositions == null) return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = $"PinnedMarker_{index}";
        go.transform.SetParent(transform);
        go.transform.localPosition = localPositions[index];
        // 1.5× normal point size in world space — compensate for current cloud scale
        float invScale = transform.localScale.x > 1e-5f ? 1f / transform.localScale.x : 1f;
        go.transform.localScale = Vector3.one * basePointSize * 1.5f * invScale;
        Destroy(go.GetComponent<SphereCollider>());

        var mr = go.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
        mr.material = new Material(Shader.Find("Unlit/Color")) { color = color };

        _pinnedMarkers[index] = go;
    }

    void DestroyPinnedMarker(int index)
    {
        if (_pinnedMarkers.TryGetValue(index, out var go))
        {
            Destroy(go);
            _pinnedMarkers.Remove(index);
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────

    public string GetSequenceId(int index) =>
        (sequenceIds != null && index >= 0 && index < pointCount) ? sequenceIds[index] : null;

    public Vector3 GetLocalPosition(int index) =>
        (localPositions != null && index >= 0 && index < pointCount) ? localPositions[index] : Vector3.zero;

    public void ClearSelection()
    {
        selectedIndex = -1;
        infoLabel?.gameObject.SetActive(false);
        infoPanel?.Hide();
    }

    // Finds the single nearest point within a sphere around the fingertip.
    // Returns -1 when no point falls inside the threshold radius.
    [BurstCompile]
    private struct FindNearestProximityJob : IJob
    {
        [ReadOnly] public NativeArray<Vector3> positions;
        public Vector3 tipLocal;
        public float   threshSq;   // local-space radius²
        public int     pointCount;
        public NativeArray<int> resultIndex;

        public void Execute()
        {
            float bestSq = threshSq;
            int   best   = -1;
            for (int i = 0; i < pointCount; i++)
            {
                Vector3 d = positions[i] - tipLocal;
                float distSq = d.x * d.x + d.y * d.y + d.z * d.z;
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    best   = i;
                }
            }
            resultIndex[0] = best;
        }
    }
}
