using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class ProteinVisualizer : MonoBehaviour
{
    public string pdbUrl = "https://pharp.ugent.be/static/pdbs/51efad3821e6294a824509b773c539cbf48031b9f04eb109a7dde3751064e9f1.pdb";
    public float ribbonWidth = 1.3f;
    public float ribbonThickness = 0.4f;
    public int splineDivisions = 10;
    public int radialSegments = 8;
    public float rotationSpeed = 8f;
    public bool autoRotate = true;
    public bool autoPosition = false;

    [Header("Grab Interaction")]
    [Tooltip("World-space radius (meters) within which a pinch grabs the protein")]
    public float grabRadius = 0.12f;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;

    // OVR hand references (populated at runtime)
    private OVRHand      _rightHand;
    private OVRHand      _leftHand;
    private OVRSkeleton  _rightSkel;
    private OVRSkeleton  _leftSkel;
    private Transform    _rightHandAnchor;
    private Transform    _leftHandAnchor;

    // Derived from mesh bounds after build; used for grab detection
    private float        _meshBoundsRadius;

    // Single-hand grab state
    private bool         _isGrabbed;
    private OVRHand      _grabHand;
    private Transform    _grabAnchorTransform;
    private Vector3      _grabLocalOffset;
    private Quaternion   _grabRotOffset;

    // Two-hand scale state
    private bool         _isTwoHandScaling;
    private float        _scaleAtScaleStart;
    private float        _twoHandDistAtStart;
    private Vector3      _midpointAtStart;
    private Vector3      _posAtScaleStart;

    private bool         _wasRPinch;
    private bool         _wasLPinch;

    /// <summary>True while the user is holding the protein.</summary>
    public bool IsGrabbed => _isGrabbed;

    /// <summary>True after Release() has been called (protein is free-floating).</summary>
    public bool IsReleased { get; private set; }

    public enum SSType { Coil, Helix, Sheet }

    public class Residue
    {
        public int residueIndex;
        public string residueName;
        public string chain;
        public Vector3 CA;
        public Vector3 O;
        public bool hasCA;
        public bool hasO;
        
        public SSType ssType = SSType.Coil;
        public float width;
        public float thickness;
        public float bFactor;
        public Color color;
    }

    struct SSBlock
    {
        public string chain;
        public int start;
        public int end;
        public SSType type;
    }

    void Start()
    {
        // Find OVR hand references for grab interaction
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            _rightHand        = rig.rightHandAnchor.GetComponentInChildren<OVRHand>();
            _leftHand         = rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
            _rightSkel        = rig.rightHandAnchor.GetComponentInChildren<OVRSkeleton>();
            _leftSkel         = rig.leftHandAnchor.GetComponentInChildren<OVRSkeleton>();
            _rightHandAnchor  = rig.rightHandAnchor;
            _leftHandAnchor   = rig.leftHandAnchor;
        }

        // Setup components
        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        
        // Use the custom solid vertex color shader to avoid transparent blending issues
        Shader shader = Shader.Find("Custom/SolidVertexColor");
        if (shader == null) shader = Shader.Find("Mobile/Particles/Alpha Blended");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        
        Material mat = new Material(shader);
        
        // Force the render queue to render after standard geometry and UI, but still write Z
        // A UI canvas usually renders at 3000 (Transparent). Rendering at 3001 ensures 
        // the protein is drawn on top of the panel's background.
        mat.renderQueue = 3001; 
        
        meshRenderer.material = mat;

        // Optionally start if a URL was already set (or wait for FetchAndRender)
        // StartCoroutine(FetchAndRenderPDB());
    }

    public void FetchAndRender(string url)
    {
        pdbUrl = url;
        StartCoroutine(FetchAndRenderPDB());
    }

    void Update()
    {
        // Only allow grab interaction once the protein has been released from the panel
        if (!IsReleased)
        {
            if (autoRotate)
                transform.Rotate(Vector3.up * rotationSpeed * Time.deltaTime, Space.World);
            return;
        }

        bool rP = _rightHand != null && _rightHand.IsTracked
               && _rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool lP = _leftHand  != null && _leftHand.IsTracked
               && _leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool rDown = rP && !_wasRPinch;
        bool lDown = lP && !_wasLPinch;
        _wasRPinch = rP;
        _wasLPinch = lP;

        Vector3 rTip = GetFingertipPosition(_rightSkel, _rightHandAnchor);
        Vector3 lTip = GetFingertipPosition(_leftSkel,  _leftHandAnchor);

        // ── Two-hand scale (takes priority over single-hand grab) ────────
        if (rP && lP)
        {
            if (!_isTwoHandScaling)
            {
                // Transition into two-hand mode: snapshot everything
                _isTwoHandScaling      = true;
                _isGrabbed             = false;   // release any single-hand grab
                _grabHand              = null;
                _grabAnchorTransform   = null;
                _scaleAtScaleStart     = transform.localScale.x;
                _twoHandDistAtStart    = Mathf.Max(Vector3.Distance(rTip, lTip), 0.001f);
                _midpointAtStart       = (rTip + lTip) * 0.5f;
                _posAtScaleStart       = transform.position;
            }
            else
            {
                float newDist  = Mathf.Max(Vector3.Distance(rTip, lTip), 0.001f);
                float newScale = Mathf.Clamp(
                    _scaleAtScaleStart * (newDist / _twoHandDistAtStart),
                    0.0005f, 0.05f);
                transform.localScale = Vector3.one * newScale;

                // Translate so the midpoint between hands tracks the protein centre
                Vector3 midNow = (rTip + lTip) * 0.5f;
                transform.position = _posAtScaleStart + (midNow - _midpointAtStart);
            }
            return;
        }

        // Both hands no longer both pinching — exit two-hand mode
        if (_isTwoHandScaling)
        {
            _isTwoHandScaling = false;
            // Snapshot current state so a single-hand grab can pick up smoothly
        }

        // ── Single-hand grab ─────────────────────────────────────────────
        if (_isGrabbed)
        {
            bool still = _grabHand == _rightHand ? rP : lP;
            if (still)
            {
                transform.position = _grabAnchorTransform.position
                                   + _grabAnchorTransform.rotation * _grabLocalOffset;
                transform.rotation = _grabAnchorTransform.rotation * _grabRotOffset;
            }
            else
            {
                _isGrabbed           = false;
                _grabHand            = null;
                _grabAnchorTransform = null;
            }
            return;
        }

        // Rising-edge only so we don't fight the panel grab on the same frame
        if (rDown) TryStartGrab(_rightHand, _rightHandAnchor, rTip);
        if (lDown && !_isGrabbed) TryStartGrab(_leftHand, _leftHandAnchor, lTip);
    }

    void TryStartGrab(OVRHand hand, Transform anchor, Vector3 fingertip)
    {
        if (hand == null || anchor == null) return;

        // Grab anywhere within the protein's bounding sphere + 6 cm buffer
        float worldRadius = _meshBoundsRadius > 0f
            ? _meshBoundsRadius * transform.lossyScale.x + 0.06f
            : grabRadius;

        if (Vector3.Distance(fingertip, transform.position) > worldRadius) return;

        _isGrabbed           = true;
        _grabHand            = hand;
        _grabAnchorTransform = anchor;
        // Store protein's position and rotation in anchor-local space
        _grabLocalOffset     = Quaternion.Inverse(anchor.rotation) * (transform.position - anchor.position);
        _grabRotOffset       = Quaternion.Inverse(anchor.rotation) * transform.rotation;
    }

    /// <summary>
    /// Detaches the protein from the panel, moves it to <paramref name="worldPosition"/>,
    /// scales it up for easier interaction, and stops auto-rotation.
    /// </summary>
    public void Release(Vector3 worldPosition)
    {
        transform.SetParent(null, worldPositionStays: true);
        transform.position   = worldPosition;
        transform.localScale *= 3f;   // larger target for grabbing
        autoRotate           = false;
        IsReleased           = true;
        _isGrabbed           = false;
        _wasRPinch           = false;
        _wasLPinch           = false;
    }

    // Returns the world-space index fingertip position.
    // Quest 3 uses XRHandRight skeleton — bone IDs collide with the legacy Hand_* system,
    // so we detect the skeleton type at runtime (same approach as UmapPointCloud).
    Vector3 GetFingertipPosition(OVRSkeleton skel, Transform fallbackAnchor)
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
        return fallbackAnchor != null ? fallbackAnchor.position : Vector3.zero;
    }

    IEnumerator FetchAndRenderPDB()
    {
        string url = pdbUrl;
        Debug.Log($"Fetching PDB from: {url}");
        
        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Error downloading PDB: " + webRequest.error);
            }
            else
            {
                string pdbContent = webRequest.downloadHandler.text;
                List<Residue> residues = ParsePDB(pdbContent);
                GenerateCartoonMesh(residues);
                
                // Position right in front of the camera based on mesh bounds
                if (autoPosition && Camera.main != null && meshFilter.mesh != null)
                {
                    // Get the radius of the bounds
                    float maxExtents = meshFilter.mesh.bounds.extents.magnitude;
                    
                    // Calculate optimal distance using camera FOV so the protein fits entirely on screen
                    float fov = Camera.main.fieldOfView * Mathf.Deg2Rad;
                    float optimalDistance = maxExtents / Mathf.Tan(fov * 0.5f);
                    
                    // Add a slight margin
                    optimalDistance *= 1.2f;

                    // Place the GameObject exactly at the calculated distance in front of the camera.
                    // Because the mesh vertices are already centered around (0,0,0) locally, 
                    // setting the transform.position to this point automatically centers the protein!
                    transform.position = Camera.main.transform.position + Camera.main.transform.forward * optimalDistance;
                }
            }
        }
    }

    List<Residue> ParsePDB(string pdbData)
    {
        Dictionary<string, Residue> resDict = new Dictionary<string, Residue>();
        List<Residue> orderedResidues = new List<Residue>();
        List<SSBlock> ssBlocks = new List<SSBlock>();

        string[] lines = pdbData.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            try
            {
                if (line.StartsWith("HELIX "))
                {
                    string chain = line.Substring(19, 1).Trim();
                    int start = ParseIntSafe(line.Substring(21, 4));
                    int end = ParseIntSafe(line.Substring(33, 4));
                    ssBlocks.Add(new SSBlock { chain = chain, start = start, end = end, type = SSType.Helix });
                }
                else if (line.StartsWith("SHEET "))
                {
                    string chain = line.Substring(21, 1).Trim();
                    int start = ParseIntSafe(line.Substring(22, 4));
                    int end = ParseIntSafe(line.Substring(33, 4));
                    ssBlocks.Add(new SSBlock { chain = chain, start = start, end = end, type = SSType.Sheet });
                }
                else if (line.StartsWith("ATOM  ") || line.StartsWith("HETATM"))
                {
                    string atomName = line.Substring(12, 4).Trim();
                    string resName = line.Substring(17, 3).Trim();
                    string chain = line.Substring(21, 1).Trim();
                    int resSeq = ParseIntSafe(line.Substring(22, 4));
                    float x = float.Parse(line.Substring(30, 8).Trim());
                    float y = float.Parse(line.Substring(38, 8).Trim());
                    float z = float.Parse(line.Substring(46, 8).Trim());
                    float bFactor = line.Length >= 66 ? float.Parse(line.Substring(60, 6).Trim()) : 0f;

                    string resKey = chain + "_" + resSeq;

                    if (!resDict.ContainsKey(resKey))
                    {
                        Residue newRes = new Residue { residueIndex = resSeq, residueName = resName, chain = chain };
                        resDict[resKey] = newRes;
                        orderedResidues.Add(newRes);
                    }

                    Residue res = resDict[resKey];

                    // Capture Alpha Carbons (CA) and Carbonyl Oxygens (O) for orientation
                    if (atomName == "CA")
                    {
                        res.CA = new Vector3(x, y, z);
                        res.bFactor = bFactor;
                        res.hasCA = true;
                    }
                    else if (atomName == "O")
                    {
                        res.O = new Vector3(x, y, z);
                        res.hasO = true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Failed to parse PDB line: " + line + "\n" + e.Message);
            }
        }
        
        // Filter out residues that don't have a backbone CA
        orderedResidues.RemoveAll(r => !r.hasCA);

        // Simple Secondary Structure Assignment if missing (common in AlphaFold PDBs)
        if (ssBlocks.Count == 0 && orderedResidues.Count > 0)
        {
            for (int i = 0; i < orderedResidues.Count; i++)
            {
                bool isHelix = false;
                bool isSheet = false;

                if (i >= 2 && i < orderedResidues.Count - 2)
                {
                    float d13 = Vector3.Distance(orderedResidues[i - 1].CA, orderedResidues[i + 1].CA);
                    float d15 = Vector3.Distance(orderedResidues[i - 2].CA, orderedResidues[i + 2].CA);

                    // Alpha helix is very compact
                    if (d13 < 6.0f && d15 < 8.0f) 
                    {
                        isHelix = true;
                    }
                    // Beta sheet is highly extended
                    else if (d13 > 6.0f && d15 > 10.0f) 
                    {
                        isSheet = true;
                    }
                }

                if (isHelix) orderedResidues[i].ssType = SSType.Helix;
                else if (isSheet) orderedResidues[i].ssType = SSType.Sheet;
            }

            // Smooth out 1-residue anomalies
            for (int i = 1; i < orderedResidues.Count - 1; i++)
            {
                if (orderedResidues[i - 1].ssType == orderedResidues[i + 1].ssType)
                {
                    orderedResidues[i].ssType = orderedResidues[i - 1].ssType;
                }
            }
        }

        // Assign Secondary Structure types from blocks if they exist
        if (ssBlocks.Count > 0)
        {
            foreach (var r in orderedResidues)
            {
                r.ssType = SSType.Coil;
                foreach (var block in ssBlocks)
                {
                    if (block.chain == r.chain && r.residueIndex >= block.start && r.residueIndex <= block.end)
                    {
                        r.ssType = block.type;
                        break;
                    }
                }
            }
        }

        // Calculate thickness, widths and colors per-residue
        for (int i = 0; i < orderedResidues.Count; i++)
        {
            var r = orderedResidues[i];
            
            // Base Coil
            r.width = 0.4f;
            r.thickness = 0.4f;

            if (r.ssType == SSType.Helix)
            {
                r.width = 2.4f;
                r.thickness = 0.4f; 
            }
            else if (r.ssType == SSType.Sheet)
            {
                r.width = 2.4f;
                r.thickness = 0.4f;

                // Check for arrowhead (last residue in sheet block)
                bool isEndOfSheet = (i == orderedResidues.Count - 1) || (orderedResidues[i+1].ssType != SSType.Sheet) || (orderedResidues[i+1].chain != r.chain);
                if (isEndOfSheet)
                {
                    r.width = 0.1f; // Arrow tip
                }
                else
                {
                    bool isBaseOfArrow = (i == orderedResidues.Count - 2) || (orderedResidues[i+2].ssType != SSType.Sheet) || (orderedResidues[i+2].chain != r.chain);
                    if (isBaseOfArrow)
                    {
                        r.width = 3.2f; // Arrow wide base
                    }
                }
            }

            // Color by B-factor (AlphaFold3 style)
            if (r.bFactor > 90f)
                r.color = new Color(0f, 0.325f, 0.839f); // Dark Blue (Very high confidence)
            else if (r.bFactor > 70f)
                r.color = new Color(0.396f, 0.796f, 0.953f); // Light Blue (Confident)
            else if (r.bFactor > 50f)
                r.color = new Color(1f, 0.859f, 0.075f); // Yellow (Low confidence)
            else
                r.color = new Color(1f, 0.49f, 0.27f); // Orange (Very low confidence)
        }

        return orderedResidues;
    }

    int ParseIntSafe(string s)
    {
        string digits = "";
        foreach (char c in s) if (char.IsDigit(c) || c == '-') digits += c;
        if (int.TryParse(digits, out int val)) return val;
        return -1;
    }

    // Port of subdivide_spline (Catmull-Rom)
    List<Vector3> SubdivideSpline(List<Vector3> points, int div)
    {
        List<Vector3> ret = new List<Vector3>();
        if (points.Count < 2) return ret;

        // Duplicate first and last points for bounds
        List<Vector3> paddedPoints = new List<Vector3>();
        paddedPoints.Add(points[0]);
        paddedPoints.AddRange(points);
        paddedPoints.Add(points[points.Count - 1]);

        int size = paddedPoints.Count;

        for (int i = 0; i <= size - 4; i++)
        {
            Vector3 p0 = paddedPoints[i];
            Vector3 p1 = paddedPoints[i + 1];
            Vector3 p2 = paddedPoints[i + 2];
            Vector3 p3 = paddedPoints[i + 3];

            Vector3 v0 = (p2 - p0) * 0.5f;
            Vector3 v1 = (p3 - p1) * 0.5f;

            for (int j = 0; j < div; j++)
            {
                float t = 1.0f / div * j;
                float t2 = t * t;
                float t3 = t2 * t;

                float x = p1.x + t * v0.x + t2 * (-3 * p1.x + 3 * p2.x - 2 * v0.x - v1.x) + t3 * (2 * p1.x - 2 * p2.x + v0.x + v1.x);
                float y = p1.y + t * v0.y + t2 * (-3 * p1.y + 3 * p2.y - 2 * v0.y - v1.y) + t3 * (2 * p1.y - 2 * p2.y + v0.y + v1.y);
                float z = p1.z + t * v0.z + t2 * (-3 * p1.z + 3 * p2.z - 2 * v0.z - v1.z) + t3 * (2 * p1.z - 2 * p2.z + v0.z + v1.z);

                ret.Add(new Vector3(x, y, z));
            }
        }
        ret.Add(points[points.Count - 1]);
        return ret;
    }

    List<float> SubdivideLinear(List<float> points, int div)
    {
        List<float> ret = new List<float>();
        if (points.Count < 2) return ret;

        // Apply same padding count as the cubic spline (1 front, 1 back)
        // so the array sizes match exactly!
        List<float> padded = new List<float>();
        padded.Add(points[0]);
        padded.AddRange(points);
        padded.Add(points[points.Count - 1]);

        int size = padded.Count;
        for (int i = 0; i <= size - 4; i++)
        {
            float p1 = padded[i + 1];
            float p2 = padded[i + 2];

            for (int j = 0; j < div; j++)
            {
                float t = (float)j / div;
                ret.Add(Mathf.Lerp(p1, p2, t));
            }
        }
        ret.Add(points[points.Count - 1]);
        return ret;
    }

    List<Color> SubdivideColor(List<Color> points, int div)
    {
        List<Color> ret = new List<Color>();
        if (points.Count < 2) return ret;

        List<Color> padded = new List<Color>();
        padded.Add(points[0]);
        padded.AddRange(points);
        padded.Add(points[points.Count - 1]);

        int size = padded.Count;
        for (int i = 0; i <= size - 4; i++)
        {
            Color p1 = padded[i + 1];
            Color p2 = padded[i + 2];

            for (int j = 0; j < div; j++)
            {
                float t = (float)j / div;
                ret.Add(Color.Lerp(p1, p2, t));
            }
        }
        ret.Add(points[points.Count - 1]);
        return ret;
    }

    void GenerateCartoonMesh(List<Residue> residues)
    {
        if (residues.Count < 2) return;

        List<Vector3> backbonePoints = new List<Vector3>();
        List<Vector3> orientPoints = new List<Vector3>();
        List<float> widths = new List<float>();
        List<float> thicknesses = new List<float>();
        List<Color> rawColors = new List<Color>();

        for (int i = 0; i < residues.Count; i++)
        {
            Residue r = residues[i];
            backbonePoints.Add(r.CA);
            widths.Add(r.width);
            thicknesses.Add(r.thickness);
            rawColors.Add(r.color);

            if (r.hasO)
            {
                // Orientation vector points roughly from CA to O
                orientPoints.Add((r.O - r.CA).normalized);
            }
            else
            {
                // Fallback orientation if O is missing
                if (i > 0 && i < residues.Count - 1)
                {
                    Vector3 fwd = (residues[i+1].CA - residues[i-1].CA).normalized;
                    orientPoints.Add(Vector3.Cross(fwd, Vector3.up).normalized);
                }
                else
                {
                    orientPoints.Add(Vector3.right);
                }
            }
        }

        // 1. Interpolate backbone and orientation
        List<Vector3> smoothBackbone = SubdivideSpline(backbonePoints, splineDivisions);
        List<Vector3> smoothOrient = SubdivideSpline(orientPoints, splineDivisions);
        List<float> smoothWidth = SubdivideLinear(widths, splineDivisions);
        List<float> smoothThickness = SubdivideLinear(thicknesses, splineDivisions);
        List<Color> smoothColor = SubdivideColor(rawColors, splineDivisions);

        // Calculate parallel transport frames (Rotation Minimizing Frame with Twist Correction)
        Vector3[] tangents = new Vector3[smoothBackbone.Count];
        for (int i = 0; i < smoothBackbone.Count; i++)
        {
            if (i == 0) tangents[i] = (smoothBackbone[1] - smoothBackbone[0]).normalized;
            else if (i == smoothBackbone.Count - 1) tangents[i] = (smoothBackbone[i] - smoothBackbone[i - 1]).normalized;
            else tangents[i] = (smoothBackbone[i + 1] - smoothBackbone[i - 1]).normalized;
        }

        Vector3[] upVectors = new Vector3[smoothBackbone.Count];
        // Initial up vector orthogonal to tangent and orientation (90 degrees rotated from Right)
        upVectors[0] = Vector3.Cross(tangents[0], smoothOrient[0]).normalized;
        if (upVectors[0].sqrMagnitude < 0.1f) upVectors[0] = Vector3.Cross(tangents[0], Vector3.right).normalized; // Fallback

        for (int i = 1; i < smoothBackbone.Count; i++)
        {
            Vector3 T0 = tangents[i - 1];
            Vector3 T1 = tangents[i];

            // Parallel Transport step
            Vector3 axis = Vector3.Cross(T0, T1);
            float dot = Mathf.Clamp(Vector3.Dot(T0, T1), -1f, 1f);
            
            Vector3 transportedUp;
            if (axis.sqrMagnitude > 1e-6f)
            {
                float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;
                transportedUp = Quaternion.AngleAxis(angle, axis.normalized) * upVectors[i - 1];
            }
            else
            {
                transportedUp = upVectors[i - 1];
            }

            // Target up vector from chemical orientation (Oxygens)
            // By crossing Tangent with the Oxygen vector (which points sideways), we get a true Up vector!
            Vector3 curvature = (T1 - T0);
            Vector3 targetUp;
            
            // For helices and curved beta barrels, the normal should point radially OUTWARD.
            // The curvature vector (T1 - T0) points directly INWARD towards the center of curvature.
            // So -curvature points OUTWARD.
            if (curvature.sqrMagnitude > 1e-5f)
            {
                targetUp = -curvature.normalized;
            }
            else
            {
                targetUp = Vector3.Cross(T1, smoothOrient[i]).normalized;
            }
            
            // Twist correction: smoothly align the transported frame with the chemical target
            // to avoid both sudden flips and drift.
            if (Vector3.Dot(transportedUp, targetUp) < 0f) targetUp = -targetUp;
            upVectors[i] = Vector3.Slerp(transportedUp, targetUp, 0.15f).normalized;
        }

        // 2. Build Ribbon Mesh
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Color> colors = new List<Color>();

        // Generate strip
        for (int i = 0; i < smoothBackbone.Count; i++)
        {
            Vector3 curr = smoothBackbone[i];
            Vector3 forward = tangents[i];
            Vector3 up = upVectors[i];
            Vector3 right = Vector3.Cross(up, forward).normalized;

            float halfWidth = smoothWidth[i] * 0.5f;
            float halfThick = smoothThickness[i] * 0.5f;

            int vOffset = vertices.Count;
            Color nodeColor = smoothColor[i];

            // Generate an elliptical/rounded cross section instead of a flat 4-sided rectangle
            // This guarantees ribbons have true volume and look like tubes!
            for (int j = 0; j < radialSegments; j++)
            {
                float angle = ((float)j / radialSegments) * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                // Right maps to width, Up maps to thickness
                Vector3 pt = curr + right * (cos * halfWidth) + up * (sin * halfThick);
                vertices.Add(pt);
                colors.Add(nodeColor);
            }

            // Connect to previous cross section
            if (i > 0)
            {
                int prevOffset = vOffset - radialSegments;

                for (int j = 0; j < radialSegments; j++)
                {
                    int next_j = (j + 1) % radialSegments;

                    int p1 = prevOffset + j;
                    int p2 = prevOffset + next_j;
                    int p3 = vOffset + next_j;
                    int p4 = vOffset + j;

                    // Triangle 1
                    triangles.Add(p1);
                    triangles.Add(p2);
                    triangles.Add(p3);

                    // Triangle 2
                    triangles.Add(p1);
                    triangles.Add(p3);
                    triangles.Add(p4);
                }
            }
        }

        // 3. Center the mesh around the local origin
        if (vertices.Count > 0)
        {
            Vector3 minBound = vertices[0];
            Vector3 maxBound = vertices[0];
            foreach (var v in vertices)
            {
                minBound = Vector3.Min(minBound, v);
                maxBound = Vector3.Max(maxBound, v);
            }
            
            Vector3 centerOffset = (minBound + maxBound) * 0.5f;
            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] -= centerOffset;
            }
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; // Allow more than 65k vertices
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.colors = colors.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        meshFilter.mesh = mesh;

        // Store local-space bounding sphere radius; world-space grab radius is computed
        // at grab-time using lossyScale so it stays correct after Release() scales the protein.
        _meshBoundsRadius = mesh.bounds.extents.magnitude;
    }
}
