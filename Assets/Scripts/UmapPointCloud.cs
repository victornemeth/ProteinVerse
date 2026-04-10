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

[AddComponentMenu("UMAP/Point Cloud")]
public class UmapPointCloud : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────────

    [Header("Visual")]
    [Tooltip("Base point radius in world-space meters at cloud scale = 1")]
    [SerializeField] private float basePointSize = 0.010f;

    [Header("Info Display")]
    [Tooltip("Optional world-space TextMeshPro that shows the hovered/selected point ID")]
    [SerializeField] private TMPro.TextMeshPro infoLabel;

    [Tooltip("Panel that pops up with full point info when a point is selected")]
    [SerializeField] private PointInfoPanel infoPanel;

    // ─────────────────────────────────────────────────────────────
    //  Data
    // ─────────────────────────────────────────────────────────────

    private Vector3[] localPositions;   // normalized to [-0.5, 0.5]^3
    private Vector3[] rawPositions;     // original UMAP1/2/3 values from CSV
    private Color[]   pointColors;
    private string[]  sequenceIds;
    private int       pointCount;

    // ─────────────────────────────────────────────────────────────
    //  GPU Rendering
    // ─────────────────────────────────────────────────────────────

    private Mesh          quad;
    private Material      cloudMaterial;
    private ComputeBuffer positionBuffer;
    private ComputeBuffer colorBuffer;
    private ComputeBuffer argsBuffer;
    private Bounds        drawBounds;

    private static readonly int ShaderCloudMatrix = Shader.PropertyToID("_CloudMatrix");
    private static readonly int ShaderPointSize   = Shader.PropertyToID("_PointSize");
    private static readonly int ShaderPointCount  = Shader.PropertyToID("_PointCount");
    private static readonly int ShaderPositions   = Shader.PropertyToID("_Positions");
    private static readonly int ShaderColors      = Shader.PropertyToID("_Colors");

    // ─────────────────────────────────────────────────────────────
    //  Highlight marker (a simple sphere — stereo-correct, no shader tricks)
    // ─────────────────────────────────────────────────────────────

    private Transform    highlightMarker;
    private MeshRenderer highlightRenderer;

    // ─────────────────────────────────────────────────────────────
    //  OVR References
    // ─────────────────────────────────────────────────────────────

    private Transform rightAnchor, leftAnchor;
    private Transform rightAimAnchor;
    private Transform cameraTransform;
    private OVRHand   rightHand, leftHand;
    private LineRenderer aimRay;

    // ─────────────────────────────────────────────────────────────
    //  Interaction State
    // ─────────────────────────────────────────────────────────────

    [Header("Interaction")]
    public bool isMovementEnabled = false;

    private bool rGrabActive, lGrabActive;

    private Vector3    grabStartPosR, grabStartPosL;
    private Quaternion grabStartRotR;
    private Vector3    cloudPosAtGrab;
    private Quaternion cloudRotAtGrab;
    private float      cloudScaleAtGrab;
    private float      twoHandDistAtGrab;

    private int  hoveredIndex  = -1;
    private int  selectedIndex = -1;
    private int  hoverFrame;

    // Previous-frame pinch state for edge detection
    private bool wasRightPinch;
    private bool wasLeftPinch;

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
        }
        else
        {
            Debug.LogWarning("[UmapPointCloud] OVRCameraRig not found — interaction will be limited.");
        }
    }

    void Start()
    {
        quad = CreateBillboardQuad();
        SetupAimRay();
        SetupHighlightMarker();
        StartCoroutine(LoadCSV());
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
        var mat = new Material(Shader.Find("Unlit/Color")) { color = Color.white };
        highlightRenderer.material = mat;
    }

    void Update()
    {
        if (cloudMaterial == null || argsBuffer == null) return;

        HandleInteraction();

        cloudMaterial.SetMatrix(ShaderCloudMatrix, transform.localToWorldMatrix);
        cloudMaterial.SetFloat(ShaderPointSize, basePointSize * transform.localScale.x);

        if (++hoverFrame % 5 == 0)
            UpdateHover();

        UpdateAimRay();
        UpdateHighlightMarker();
        UpdateInfoPanelPosition();

        Graphics.DrawMeshInstancedIndirect(quad, 0, cloudMaterial, drawBounds, argsBuffer);
    }

    void OnDestroy()
    {
        positionBuffer?.Release();
        colorBuffer?.Release();
        argsBuffer?.Release();
        if (cloudMaterial != null) Destroy(cloudMaterial);
    }

    // ─────────────────────────────────────────────────────────────
    //  CSV Loading
    // ─────────────────────────────────────────────────────────────

    IEnumerator LoadCSV()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "umap_coordinates_n15.csv");

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

        Debug.Log($"[UmapPointCloud] Parsed {pointCount} points.");
    }

    static bool ParseFloat(string s, out float v) =>
        float.TryParse(s.Trim(),
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out v);

    // ─────────────────────────────────────────────────────────────
    //  GPU Buffer Initialisation
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

        positionBuffer = new ComputeBuffer(pointCount, sizeof(float) * 3);
        var pos = new Vector3[pointCount];
        Array.Copy(localPositions, pos, pointCount);
        positionBuffer.SetData(pos);

        colorBuffer = new ComputeBuffer(pointCount, sizeof(float) * 4);
        var col = new Vector4[pointCount];
        for (int i = 0; i < pointCount; i++)
            col[i] = new Vector4(pointColors[i].r, pointColors[i].g, pointColors[i].b, 1f);
        colorBuffer.SetData(col);

        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(new uint[]
        {
            (uint)quad.GetIndexCount(0),
            (uint)pointCount,
            (uint)quad.GetIndexStart(0),
            (uint)quad.GetBaseVertex(0),
            0u
        });

        cloudMaterial.SetBuffer(ShaderPositions,  positionBuffer);
        cloudMaterial.SetBuffer(ShaderColors,     colorBuffer);
        cloudMaterial.SetInt(ShaderPointCount,    pointCount);

        drawBounds = new Bounds(Vector3.zero, Vector3.one * 2000f);
        Debug.Log("[UmapPointCloud] GPU buffers ready.");
    }

    // ─────────────────────────────────────────────────────────────
    //  Interaction
    // ─────────────────────────────────────────────────────────────

    void HandleInteraction()
    {
        // ── Raw input ───────────────────────────────────────────
        bool rControllerGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool lControllerGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);

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
        // For controllers: index trigger fires a click.
        // For hands: a pinch-start while a point is hovered fires a click (NOT a grab).
        bool clickFired = rTriggerDown
                       || (rPinchDown && hoveredIndex >= 0);

        if (clickFired)
        {
            if (hoveredIndex >= 0)
            {
                selectedIndex = hoveredIndex;
                Debug.Log($"[UmapPointCloud] SELECTED {selectedIndex}: {sequenceIds[selectedIndex]}");

                if (infoPanel != null)
                {
                    infoPanel.Show(cameraTransform, sequenceIds[selectedIndex], rawPositions[selectedIndex]);
                }
                else
                {
                    ShowInfoLabel(selectedIndex, "SELECTED\n");
                }

                StartCoroutine(TriggerSelectHaptics());
            }
            else if (infoPanel != null && infoPanel.gameObject.activeSelf)
            {
                // Click at empty space while panel is open → close it
                infoPanel.Hide();
                selectedIndex = -1;
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
        if (rGrip && lGrip)
        {
            if (!rGrabActive || !lGrabActive)
            {
                rGrabActive = lGrabActive = true;
                grabStartPosR     = rPos;
                grabStartPosL     = lPos;
                cloudPosAtGrab    = transform.position;
                cloudScaleAtGrab  = transform.localScale.x;
                twoHandDistAtGrab = Mathf.Max(Vector3.Distance(rPos, lPos), 0.01f);
            }
            else
            {
                float newDist  = Mathf.Max(Vector3.Distance(rPos, lPos), 0.01f);
                float newScale = Mathf.Clamp(cloudScaleAtGrab * (newDist / twoHandDistAtGrab), 0.05f, 10f);
                transform.localScale = Vector3.one * newScale;
                Vector3 midNow = (rPos + lPos) * 0.5f;
                Vector3 mid0   = (grabStartPosR + grabStartPosL) * 0.5f;
                transform.position = cloudPosAtGrab + (midNow - mid0);
            }
        }
        // ── Right-hand single grab ──────────────────────────────
        else if (rGrip)
        {
            if (!rGrabActive)
            {
                rGrabActive    = true;
                lGrabActive    = false;
                grabStartPosR  = rPos;
                grabStartRotR  = rRot;
                cloudPosAtGrab = transform.position;
                cloudRotAtGrab = transform.rotation;
            }
            else
            {
                Quaternion delta = rRot * Quaternion.Inverse(grabStartRotR);
                transform.rotation = delta * cloudRotAtGrab;
                transform.position = rPos + delta * (cloudPosAtGrab - grabStartPosR);
            }
        }
        // ── Left-hand single grab ───────────────────────────────
        else if (lGrip)
        {
            if (!lGrabActive)
            {
                lGrabActive    = true;
                rGrabActive    = false;
                grabStartPosR  = lPos;
                grabStartRotR  = lRot;
                cloudPosAtGrab = transform.position;
                cloudRotAtGrab = transform.rotation;
            }
            else
            {
                Quaternion delta = lRot * Quaternion.Inverse(grabStartRotR);
                transform.rotation = delta * cloudRotAtGrab;
                transform.position = lPos + delta * (cloudPosAtGrab - grabStartPosR);
            }
        }
        else
        {
            rGrabActive = lGrabActive = false;
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
        if (localPositions == null) return;

        Transform aimSource = rightAimAnchor != null ? rightAimAnchor : rightAnchor;
        if (aimSource == null) return;

        // Transform ray to cloud local space — avoids per-point TransformPoint
        Matrix4x4 w2l         = transform.worldToLocalMatrix;
        Vector3   originLocal = w2l.MultiplyPoint3x4(aimSource.position);
        Vector3   dirLocal    = w2l.MultiplyVector(aimSource.forward);
        dirLocal.Normalize();

        const float threshSq = 0.06f * 0.06f;
        float nearestT = float.MaxValue;
        int   nearest  = -1;

        for (int i = 0; i < pointCount; i++)
        {
            Vector3 toP    = localPositions[i] - originLocal;
            float   t      = Vector3.Dot(toP, dirLocal);
            if (t < 0f) continue;
            Vector3 proj   = toP - dirLocal * t;
            float   perpSq = proj.x * proj.x + proj.y * proj.y + proj.z * proj.z;
            if (perpSq < threshSq && t < nearestT)
            {
                nearestT = t;
                nearest  = i;
            }
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
        if (selectedIndex >= 0)
        {
            // Selected: pulsing, larger
            float pulse = 1f + 0.25f * Mathf.Sin(Time.time * 5f);
            localSize = basePointSize * 8f * pulse;
        }
        else
        {
            // Hovered: quietly bigger
            localSize = basePointSize * 3.5f;
        }
        highlightMarker.localScale = Vector3.one * localSize;

        // Color: match point color but brightened
        Color col = pointColors[activeIdx];
        col = Color.Lerp(col, Color.white, selectedIndex >= 0 ? 0.7f : 0.4f);
        highlightRenderer.material.color = col;
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
        if (aimRay == null) return;
        Transform aimSource = rightAimAnchor != null ? rightAimAnchor : rightAnchor;
        if (aimSource == null) { aimRay.enabled = false; return; }

        aimRay.enabled = true;
        Vector3 start = aimSource.position;
        Vector3 end   = hoveredIndex >= 0
            ? transform.TransformPoint(localPositions[hoveredIndex])
            : start + aimSource.forward * 3f;

        aimRay.SetPosition(0, start);
        aimRay.SetPosition(1, end);
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

    static Mesh CreateBillboardQuad()
    {
        var m = new Mesh { name = "PointBillboard" };
        m.vertices  = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0),
            new Vector3( 0.5f, -0.5f, 0),
            new Vector3( 0.5f,  0.5f, 0),
            new Vector3(-0.5f,  0.5f, 0),
        };
        m.uv        = new Vector2[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        m.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        m.RecalculateNormals();
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        return m;
    }

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
}
