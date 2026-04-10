/*
 * PointInfoPanel.cs
 * -----------------
 * World-space control-panel UI for a selected UMAP point.
 * Fetches protein data from pharp.ugent.be and displays:
 *   • Source ID + basic properties (MW, pI, hydropathy, aromaticity)
 *   • Domain architecture bar (proportional, colour-coded, with legend)
 *   • Associated proteins list
 *
 * SETUP: Create an empty GameObject named "InfoPanel", add this component,
 *        drag it into the "Info Panel" field on UmapPointCloud. Keep it ACTIVE.
 */

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

public class PointInfoPanel : MonoBehaviour
{
    // ── Panel size (canvas units → ×0.001 = world metres) ────────
    const float W = 420f;
    const float H = 520f;

    // ── Static text refs ─────────────────────────────────────────
    private TextMeshProUGUI _sourceIdText;
    private TextMeshProUGUI _lengthVal, _mwVal, _piVal, _hydroVal, _aromVal, _sourceVal;
    private TextMeshProUGUI _umapText;
    private TextMeshProUGUI _domainStatus, _proteinStatus;

    // ── Dynamic containers ────────────────────────────────────────
    private RectTransform _domainBarsRT;    // backbone + coloured domain bars
    private RectTransform _domainLegendRT;  // text legend rows
    private RectTransform _proteinsRT;      // protein entry rows
    private RectTransform _divider2RT;
    private RectTransform _assocProtSectionRT;
    private RectTransform _proteinStatusRT;

    // ── Close button (for fingertip poke detection) ───────────────
    private Transform _closeBtnTransform;

    // ── Shared API state ─────────────────────────────────────────
    private int    _proteinLength = -1;     // set by /basic/
    private JArray _pendingDomains;         // domains awaiting protein length

    // ── Hand / grab ──────────────────────────────────────────────
    private OVRHand     _rightHand,  _leftHand;
    private Transform   _rightAnchor,_leftAnchor;
    private OVRSkeleton _rightSkel,  _leftSkel;
    private bool        _grabbed;
    private Transform   _grabAnchor;
    private Vector3     _grabOffset;
    private bool        _wasRPinch, _wasLPinch, _wasPoking;

    // ── Domain colour palette ─────────────────────────────────────
    static readonly Color[] Palette = {
        new Color(0.30f, 0.70f, 1.00f),
        new Color(1.00f, 0.55f, 0.15f),
        new Color(0.25f, 0.88f, 0.48f),
        new Color(0.95f, 0.28f, 0.58f),
        new Color(0.90f, 0.82f, 0.12f),
        new Color(0.68f, 0.32f, 0.95f),
        new Color(0.20f, 0.92f, 0.88f),
        new Color(0.98f, 0.68f, 0.22f),
    };

    // ─────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────

    void Start()
    {
        BuildCanvas();

        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            _rightAnchor = rig.rightHandAnchor;
            _leftAnchor  = rig.leftHandAnchor;
            _rightHand   = rig.rightHandAnchor.GetComponentInChildren<OVRHand>();
            _leftHand    = rig.leftHandAnchor.GetComponentInChildren<OVRHand>();
            _rightSkel   = rig.rightHandAnchor.GetComponentInChildren<OVRSkeleton>();
            _leftSkel    = rig.leftHandAnchor.GetComponentInChildren<OVRSkeleton>();
        }

        StartCoroutine(HideNextFrame());
    }

    IEnumerator HideNextFrame()
    {
        yield return null;
        if (gameObject.activeSelf) gameObject.SetActive(false);
    }

    void Update()
    {
        if (!gameObject.activeSelf) return;
        HandleGrab();
        CheckButtonPoke();
    }

    // ─────────────────────────────────────────────────────────────
    //  Hand grab
    // ─────────────────────────────────────────────────────────────

    void HandleGrab()
    {
        bool rP = _rightHand != null && _rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool lP = _leftHand  != null && _leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool rD = rP && !_wasRPinch;
        bool lD = lP && !_wasLPinch;
        _wasRPinch = rP; _wasLPinch = lP;

        const float R = 0.22f;
        if (!_grabbed)
        {
            if (rD && _rightAnchor && Vector3.Distance(_rightAnchor.position, transform.position) < R)
            { _grabbed = true; _grabAnchor = _rightAnchor; _grabOffset = transform.position - _rightAnchor.position; }
            else if (lD && _leftAnchor && Vector3.Distance(_leftAnchor.position, transform.position) < R)
            { _grabbed = true; _grabAnchor = _leftAnchor;  _grabOffset = transform.position - _leftAnchor.position; }
        }
        else
        {
            bool still = _grabAnchor == _rightAnchor ? rP : lP;
            if (!still) { _grabbed = false; _grabAnchor = null; }
            else transform.position = _grabAnchor.position + _grabOffset;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Close-button fingertip poke
    // ─────────────────────────────────────────────────────────────

    void CheckButtonPoke()
    {
        if (_closeBtnTransform == null) return;
        // Button pivot is right-edge; step left by half button width (22 mm)
        Vector3 c = _closeBtnTransform.position - _closeBtnTransform.right * 0.022f;
        bool p = IndexTipNear(_rightSkel, c, 0.03f) || IndexTipNear(_leftSkel, c, 0.03f);
        if (p && !_wasPoking) Hide();
        _wasPoking = p;
    }

    bool IndexTipNear(OVRSkeleton sk, Vector3 pt, float r)
    {
        if (sk == null || !sk.IsInitialized) return false;
        foreach (var b in sk.Bones)
            if (b.Id == OVRSkeleton.BoneId.Hand_IndexTip)
                return Vector3.Distance(b.Transform.position, pt) < r;
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────

    public void Show(Transform camera, string sequenceId, Vector3 rawUmap)
    {
        if (_umapText == null) { Debug.LogWarning("[PointInfoPanel] Not built yet."); return; }

        if (camera != null)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(camera.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 pos = camera.position + fwd * 0.45f + Vector3.up * -0.40f;
            transform.position = pos;
            transform.rotation = Quaternion.LookRotation(-(camera.position - pos).normalized, Vector3.up);
        }

        gameObject.SetActive(true);
        _umapText.text = $"UMAP\u2081 {rawUmap.x:F2}  UMAP\u2082 {rawUmap.y:F2}  UMAP\u2083 {rawUmap.z:F2}";

        _proteinLength  = -1;
        _pendingDomains = null;
        ResetToLoading();

        StartCoroutine(FetchBasic(sequenceId));
        StartCoroutine(FetchDomains(sequenceId));
        StartCoroutine(FetchProteins(sequenceId));
    }

    public void Hide() { _grabbed = false; gameObject.SetActive(false); }

    // ─────────────────────────────────────────────────────────────
    //  Reset / loading state
    // ─────────────────────────────────────────────────────────────

    void ResetToLoading()
    {
        _sourceIdText.text = "Loading…";
        _lengthVal.text = _mwVal.text = _piVal.text =
        _hydroVal.text  = _aromVal.text = _sourceVal.text = "—";

        _domainStatus.text  = "Loading…";
        _proteinStatus.text = "Loading…";
        _domainStatus.gameObject.SetActive(true);
        _proteinStatus.gameObject.SetActive(true);

        ClearChildren(_domainBarsRT);
        ClearChildren(_domainLegendRT);
        ClearChildren(_proteinsRT);
    }

    static void ClearChildren(RectTransform rt)
    {
        if (rt == null) return;
        for (int i = rt.childCount - 1; i >= 0; i--)
            Destroy(rt.GetChild(i).gameObject);
    }

    // ─────────────────────────────────────────────────────────────
    //  API — /basic/
    // ─────────────────────────────────────────────────────────────

    IEnumerator FetchBasic(string id)
    {
        using var req = UnityWebRequest.Get($"https://pharp.ugent.be/api/sequence/{id}/basic/");
        req.timeout = 15;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success) yield break;

        JObject j;
        try { j = JObject.Parse(req.downloadHandler.text); }
        catch (Exception e) { Debug.LogError($"[Panel] basic parse: {e.Message}"); yield break; }

        _sourceIdText.text = j["source_id"]?.Value<string>() ?? "—";
        _sourceVal.text    = j["source"]   ?.Value<string>() ?? "—";

        int len = j["length"]?.Value<int>() ?? 0;
        _lengthVal.text = len > 0 ? $"{len} aa" : "—";

        var props = j["properties"] as JObject;
        if (props != null)
        {
            float mw   = props["molecular_weight"]?.Value<float>() ?? 0f;
            float pi   = props["pI"]              ?.Value<float>() ?? 0f;
            float hydr = props["hydropathy"]       ?.Value<float>() ?? 0f;
            float arom = props["aromaticity"]      ?.Value<float>() ?? 0f;
            _mwVal.text    = mw   > 0 ? $"{mw / 1000f:F2} kDa" : "—";
            _piVal.text    = pi   != 0 ? $"{pi:F2}"    : "—";
            _hydroVal.text = $"{hydr:F3}";
            _aromVal.text  = arom > 0 ? $"{arom:F3}"   : "—";
        }

        _proteinLength = len;

        // If domains arrived first, build bars now
        if (_pendingDomains != null)
        {
            BuildDomainBars(_pendingDomains, _proteinLength);
            _pendingDomains = null;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  API — /domains/
    // ─────────────────────────────────────────────────────────────

    IEnumerator FetchDomains(string id)
    {
        using var req = UnityWebRequest.Get($"https://pharp.ugent.be/api/sequence/{id}/domains/");
        req.timeout = 15;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        { _domainStatus.text = "Unavailable"; yield break; }

        JObject j;
        try { j = JObject.Parse(req.downloadHandler.text); }
        catch (Exception e) { Debug.LogError($"[Panel] domain parse: {e.Message}"); yield break; }

        var domains = j["domains"] as JArray ?? new JArray();

        if (domains.Count == 0)
        { _domainStatus.text = "No domains found"; yield break; }

        _domainStatus.gameObject.SetActive(false);

        if (_proteinLength > 0) BuildDomainBars(domains, _proteinLength);
        else _pendingDomains = domains;   // basic info hasn't arrived yet
    }

    // ─────────────────────────────────────────────────────────────
    //  Domain bar visualisation
    // ─────────────────────────────────────────────────────────────

    void BuildDomainBars(JArray domains, int totalLen)
    {
        ClearChildren(_domainBarsRT);
        ClearChildren(_domainLegendRT);
        if (totalLen <= 0) totalLen = 1;

        // ── Backbone (grey full-width) ────────────────────────────
        var bbRT = MakeChild("Backbone", _domainBarsRT);
        bbRT.anchorMin = Vector2.zero; bbRT.anchorMax = Vector2.one;
        bbRT.offsetMin = new Vector2(0f,  5f);
        bbRT.offsetMax = new Vector2(0f, -5f);
        bbRT.gameObject.AddComponent<Image>().color = new Color(0.30f, 0.30f, 0.45f, 0.6f);

        // Domain position tick marks (small vertical lines at start/end boundaries)
        // ── Coloured domain bars ──────────────────────────────────
        for (int i = 0; i < domains.Count; i++)
        {
            var d    = domains[i];
            int  s   = d["start"]?.Value<int>() ?? 0;
            int  e   = Mathf.Max(d["end"]?.Value<int>() ?? totalLen, s + 1);
            var col  = Palette[i % Palette.Length];

            float x0 = Mathf.Clamp01((float)s / totalLen);
            float x1 = Mathf.Clamp01((float)e / totalLen);

            var barRT = MakeChild($"DomBar{i}", _domainBarsRT);
            barRT.anchorMin = new Vector2(x0, 0f);
            barRT.anchorMax = new Vector2(x1, 1f);
            barRT.offsetMin = new Vector2( 1f, 2f);
            barRT.offsetMax = new Vector2(-1f, -2f);
            barRT.gameObject.AddComponent<Image>().color = col;

            // Domain ID label centred on the bar (only if bar is wide enough)
            if (x1 - x0 > 0.10f)
            {
                var lblRT = MakeChild($"DomBarLbl{i}", barRT);
                lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one;
                var tmp = lblRT.gameObject.AddComponent<TextMeshProUGUI>();
                tmp.font      = TMP_Settings.defaultFontAsset;
                tmp.text      = d["name_short"]?.Value<string>() ?? d["domain_id"]?.Value<string>() ?? "";
                tmp.fontSize  = 6.5f;
                tmp.color     = new Color(1f,1f,1f,0.85f);
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.raycastTarget = false;
                tmp.overflowMode  = TextOverflowModes.Ellipsis;
            }
        }

        // ── Legend rows ───────────────────────────────────────────
        var font = TMP_Settings.defaultFontAsset;
        for (int i = 0; i < domains.Count; i++)
        {
            var d      = domains[i];
            var col    = Palette[i % Palette.Length];
            string did  = d["domain_id"]    ?.Value<string>() ?? "?";
            string name = d["name"]         ?.Value<string>() ?? "";
            string db   = d["source_database"]?.Value<string>() ?? "";
            int    s    = d["start"]?.Value<int>() ?? 0;
            int    e    = d["end"]  ?.Value<int>() ?? totalLen;

            // Row
            var rowRT = MakeChild($"LegRow{i}", _domainLegendRT);
            rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
            rowRT.pivot     = new Vector2(0.5f, 1f);
            rowRT.sizeDelta = new Vector2(0f, 22f);
            rowRT.anchoredPosition = new Vector2(0f, -i * 23f);

            // Colour swatch
            var swRT = MakeChild("Swatch", rowRT);
            swRT.anchorMin = new Vector2(0f, 0.1f); swRT.anchorMax = new Vector2(0f, 0.9f);
            swRT.pivot = new Vector2(0f, 0.5f);
            swRT.sizeDelta = new Vector2(8f, 0f);
            swRT.anchoredPosition = Vector2.zero;
            swRT.gameObject.AddComponent<Image>().color = col;

            // Text
            var txtRT = MakeChild("Txt", rowRT);
            txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = new Vector2(12f, 0f); txtRT.offsetMax = Vector2.zero;
            var txt = txtRT.gameObject.AddComponent<TextMeshProUGUI>();
            txt.font      = font;
            txt.fontSize  = 8f;
            txt.color     = new Color(0.88f, 0.94f, 1f);
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            txt.raycastTarget = false;
            txt.enableWordWrapping = false;
            txt.overflowMode = TextOverflowModes.Ellipsis;
            txt.text = $"<b>{did}</b>  <color=#88aabb>{s}–{e} aa</color>  " +
                       $"<color=#6688aa>{db}</color>\n" +
                       $"<size=7><color=#aabbcc>{name}</color></size>";
        }
        
        // Update legend height dynamically based on domains count
        _domainLegendRT.sizeDelta = new Vector2(-24f, domains.Count * 23f);
        
        UpdateDynamicLayout();
    }

    // ─────────────────────────────────────────────────────────────
    //  API — /proteins/
    // ─────────────────────────────────────────────────────────────

    IEnumerator FetchProteins(string id)
    {
        using var req = UnityWebRequest.Get($"https://pharp.ugent.be/api/sequence/{id}/proteins/");
        req.timeout = 15;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        { _proteinStatus.text = "Unavailable"; yield break; }

        JObject j;
        try { j = JObject.Parse(req.downloadHandler.text); }
        catch (Exception e) { Debug.LogError($"[Panel] protein parse: {e.Message}"); yield break; }

        var proteins = j["proteins"] as JArray ?? new JArray();

        if (proteins.Count == 0)
        { _proteinStatus.text = "None found"; yield break; }

        _proteinStatus.gameObject.SetActive(false);

        var font  = TMP_Settings.defaultFontAsset;
        int  show = proteins.Count;

        _proteinsRT.sizeDelta = new Vector2(-24f, show * 34f);
        UpdateDynamicLayout();

        for (int i = 0; i < show; i++)
        {
            var p     = proteins[i];
            string nm  = p["protein_name"]?.Value<string>() ?? "—";
            string src = p["source_id"]   ?.Value<string>() ?? "";
            string typ = p["type"]        ?.Value<string>() ?? "";
            string phg = p["phage_name"]  ?.Value<string>() ?? "";

            var entRT = MakeChild($"Prot{i}", _proteinsRT);
            entRT.anchorMin = new Vector2(0f, 1f); entRT.anchorMax = new Vector2(1f, 1f);
            entRT.pivot     = new Vector2(0.5f, 1f);
            entRT.sizeDelta = new Vector2(0f, 32f);
            entRT.anchoredPosition = new Vector2(0f, -i * 34f);

            entRT.gameObject.AddComponent<Image>().color = new Color(0.10f, 0.12f, 0.26f, 0.75f);

            var txtRT = MakeChild("Txt", entRT);
            txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = new Vector2(6f, 2f); txtRT.offsetMax = new Vector2(-6f, -2f);
            var txt = txtRT.gameObject.AddComponent<TextMeshProUGUI>();
            txt.font      = font;
            txt.fontSize  = 8.5f;
            txt.color     = Color.white;
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            txt.raycastTarget      = false;
            txt.enableWordWrapping = false;
            txt.overflowMode       = TextOverflowModes.Ellipsis;
            txt.text = $"<b><color=#aaddff>{src}</color></b>  {nm}\n" +
                       $"<size=7.5><color=#7799aa>{phg}</color>  <color=#88bbaa>{typ}</color></size>";
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Dynamic Layout Update
    // ─────────────────────────────────────────────────────────────

    void UpdateDynamicLayout()
    {
        if (_domainLegendRT == null || _divider2RT == null || _assocProtSectionRT == null || _proteinStatusRT == null || _proteinsRT == null) return;

        float y = _domainLegendRT.anchoredPosition.y; // Start below domain bars
        y -= _domainLegendRT.sizeDelta.y;
        
        y -= 2f; // spacing
        _divider2RT.anchoredPosition = new Vector2(0f, y);
        y -= 6f; // spacing
        
        y -= 2f;
        _assocProtSectionRT.anchoredPosition = new Vector2(0f, y);
        y -= 18f;

        _proteinStatusRT.anchoredPosition = new Vector2(0f, y);
        _proteinsRT.anchoredPosition = new Vector2(0f, y);
        y -= _proteinsRT.sizeDelta.y;

        // Update content container height
        var contentRT = (RectTransform)_domainLegendRT.parent;
        contentRT.sizeDelta = new Vector2(0f, -y + 20f);
    }

    // ─────────────────────────────────────────────────────────────
    //  Canvas construction
    // ─────────────────────────────────────────────────────────────

    void BuildCanvas()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        gameObject.AddComponent<GraphicRaycaster>();
        transform.localScale = Vector3.one * 0.001f;
        GetComponent<RectTransform>().sizeDelta = new Vector2(W, H);

        // PointableCanvasModule must be on EventSystem
        if (FindFirstObjectByType<PointableCanvasModule>() == null)
        {
            var es = FindFirstObjectByType<EventSystem>();
            if (es != null) es.gameObject.AddComponent<PointableCanvasModule>();
            else
            {
                var g = new GameObject("EventSystem");
                g.AddComponent<EventSystem>(); g.AddComponent<PointableCanvasModule>();
            }
        }

        var pc = gameObject.AddComponent<PointableCanvas>();
        pc.InjectAllPointableCanvas(canvas);
        AddPokeInteractable(pc);

        var font = TMP_Settings.defaultFontAsset;

        // ── Background
        var bg = Stretch("BG", transform);
        bg.gameObject.AddComponent<Image>().color = new Color(0.05f, 0.07f, 0.15f, 0.97f);

        // ── Header (44 units)
        var hdrRT = Pin("Header", bg, 0f, 44f);
        hdrRT.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.14f, 0.36f, 1f);

        var titleRT = Stretch("Title", hdrRT.transform);
        ((RectTransform)titleRT.transform).offsetMin = new Vector2(12f, 0f);
        ((RectTransform)titleRT.transform).offsetMax = new Vector2(-52f, 0f);
        Label(titleRT, font, "Protein Info", 15f, new Color(0.85f, 0.92f, 1f), FontStyles.Bold, TextAlignmentOptions.MidlineLeft);

        // Close button
        var cbRT = new GameObject("CloseBtn").AddComponent<RectTransform>();
        cbRT.SetParent(hdrRT, false);
        cbRT.anchorMin = new Vector2(1f, 0f); cbRT.anchorMax = Vector2.one;
        cbRT.pivot     = new Vector2(1f, 0.5f); cbRT.sizeDelta = new Vector2(44f, 0f);
        var cbImg = cbRT.gameObject.AddComponent<Image>(); cbImg.color = new Color(0.60f, 0.10f, 0.10f, 1f);
        var cb    = cbRT.gameObject.AddComponent<Button>(); cb.targetGraphic = cbImg;
        var cbc   = cb.colors; cbc.highlightedColor = new Color(0.85f, 0.20f, 0.20f);
        cbc.pressedColor = new Color(0.40f, 0.06f, 0.06f); cb.colors = cbc;
        cb.onClick.AddListener(Hide);
        _closeBtnTransform = cbRT.transform;
        var cbXRT = Stretch("X", cbRT.transform);
        Label(cbXRT, font, "✕", 18f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);

        // ── Scroll View Setup ─────────────────────────────────────
        var scrollViewRT = new GameObject("ScrollView").AddComponent<RectTransform>();
        scrollViewRT.SetParent(bg, false);
        scrollViewRT.anchorMin = new Vector2(0f, 0f);
        scrollViewRT.anchorMax = new Vector2(1f, 1f);
        scrollViewRT.offsetMin = new Vector2(0f, 24f); // Leave room for footer
        scrollViewRT.offsetMax = new Vector2(0f, -44f); // Leave room for header

        var viewportRT = new GameObject("Viewport").AddComponent<RectTransform>();
        viewportRT.SetParent(scrollViewRT, false);
        viewportRT.anchorMin = Vector2.zero; viewportRT.anchorMax = Vector2.one;
        viewportRT.offsetMin = viewportRT.offsetMax = Vector2.zero;
        viewportRT.gameObject.AddComponent<RectMask2D>();

        var contentRT = new GameObject("Content").AddComponent<RectTransform>();
        contentRT.SetParent(viewportRT, false);
        contentRT.anchorMin = new Vector2(0f, 1f); contentRT.anchorMax = new Vector2(1f, 1f);
        contentRT.pivot = new Vector2(0.5f, 1f);
        contentRT.anchoredPosition = Vector2.zero;
        var contentImg = contentRT.gameObject.AddComponent<Image>();
        contentImg.color = new Color(0, 0, 0, 0); // Transparent background to catch drag events

        var scrollRect = scrollViewRT.gameObject.AddComponent<ScrollRect>();
        scrollRect.content = contentRT;
        scrollRect.viewport = viewportRT;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.scrollSensitivity = 20f;
        // Optional: add inertia, movement type
        scrollRect.movementType = ScrollRect.MovementType.Elastic;
        scrollRect.inertia = true;
        scrollRect.decelerationRate = 0.135f;

        // ── Content: track Y from top (negative = downward)
        float y = -10f;

        // Source ID ──────────────────────────────────────────────
        y -= 8f;
        var sidRT = AbsRow(contentRT, y, 22f);
        _sourceIdText = Label(sidRT, font, "—", 12f, Color.white, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
        y -= 22f;

        Divider(contentRT, y -= 4f); y -= 5f;

        // Properties ─────────────────────────────────────────────
        SectionHead(contentRT, y -= 2f, "PROPERTIES", font); y -= 16f;

        (_lengthVal, _mwVal)   = StatRow(contentRT, y, font, "Length",        "Mol. Weight"); y -= 28f;
        (_piVal,    _hydroVal) = StatRow(contentRT, y, font, "pI",            "Hydropathy");  y -= 28f;
        (_aromVal,  _sourceVal)= StatRow(contentRT, y, font, "Aromaticity",   "Source");      y -= 28f;

        Divider(contentRT, y -= 4f); y -= 6f;

        // Domain architecture ─────────────────────────────────────
        SectionHead(contentRT, y -= 2f, "DOMAIN ARCHITECTURE", font); y -= 18f;

        // Bars container (backbone + domain bars)
        _domainBarsRT = AbsRow(contentRT, y, 28f);
        // Status overlay (hidden once bars populate)
        var dsRT = AbsRow(contentRT, y, 28f);
        _domainStatus = Label(dsRT, font, "Loading…", 9f, new Color(0.60f, 0.72f, 0.92f), FontStyles.Normal, TextAlignmentOptions.Center);
        y -= 30f;

        // Legend container
        var legRT = new GameObject("DomLegend").AddComponent<RectTransform>();
        legRT.SetParent(contentRT, false);
        legRT.anchorMin = new Vector2(0f, 1f); legRT.anchorMax = new Vector2(1f, 1f);
        legRT.pivot = new Vector2(0.5f, 1f);
        legRT.sizeDelta = new Vector2(-24f, 100f);
        legRT.anchoredPosition = new Vector2(0f, y);
        _domainLegendRT = legRT;
        y -= 102f;

        _divider2RT = Divider(contentRT, y -= 2f); y -= 6f;

        // Associated Proteins ─────────────────────────────────────
        _assocProtSectionRT = SectionHead(contentRT, y -= 2f, "ASSOCIATED PROTEINS", font); y -= 18f;

        _proteinStatusRT = AbsRow(contentRT, y, 20f);
        _proteinStatus = Label(_proteinStatusRT, font, "Loading…", 9f, new Color(0.60f, 0.72f, 0.92f), FontStyles.Normal, TextAlignmentOptions.Center);

        var protRT = new GameObject("ProtList").AddComponent<RectTransform>();
        protRT.SetParent(contentRT, false);
        protRT.anchorMin = new Vector2(0f, 1f); protRT.anchorMax = new Vector2(1f, 1f);
        protRT.pivot = new Vector2(0.5f, 1f);
        protRT.sizeDelta = new Vector2(-24f, 180f);
        protRT.anchoredPosition = new Vector2(0f, y);
        _proteinsRT = protRT;
        y -= 182f;

        // Adjust content height based on total items
        contentRT.sizeDelta = new Vector2(0f, -y + 20f);

        // UMAP footer ─────────────────────────────────────────────
        Divider(bg, -H + 26f);
        var umapRT = new GameObject("UMAP").AddComponent<RectTransform>();
        umapRT.SetParent(bg, false);
        umapRT.anchorMin = new Vector2(0f, 0f); umapRT.anchorMax = new Vector2(1f, 0f);
        umapRT.pivot = new Vector2(0.5f, 0f);
        umapRT.sizeDelta = new Vector2(-24f, 20f);
        umapRT.anchoredPosition = new Vector2(0f, 5f);
        _umapText = Label(umapRT, font, "", 8.5f, new Color(0.40f, 0.92f, 0.42f), FontStyles.Normal, TextAlignmentOptions.Center);
    }

    // ─────────────────────────────────────────────────────────────
    //  Poke surface (PokeInteractor → PokeInteractable → PointableCanvas)
    // ─────────────────────────────────────────────────────────────

    void AddPokeInteractable(PointableCanvas pc)
    {
        var pokeGO = new GameObject("PokeInteractable");
        pokeGO.transform.SetParent(transform, false);

        var surfGO = new GameObject("PokeSurface");
        surfGO.transform.SetParent(pokeGO.transform, false);
        surfGO.transform.localScale = new Vector3(1f, 1f, 0.001f);

        var plane = surfGO.AddComponent<PlaneSurface>();
        plane.InjectAllPlaneSurface(PlaneSurface.NormalFacing.Backward, true);

        var clip = surfGO.AddComponent<BoundsClipper>();
        clip.Size = new Vector3(W, H, 10f);

        var cps = surfGO.AddComponent<ClippedPlaneSurface>();
        cps.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { clip });

        var poke = pokeGO.AddComponent<PokeInteractable>();
        poke.InjectAllPokeInteractable(cps);
        poke.InjectOptionalPointableElement(pc);
    }

    // ─────────────────────────────────────────────────────────────
    //  Layout helpers
    // ─────────────────────────────────────────────────────────────

    // Two-column stat row: label on left, label on right, returns value text refs
    (TextMeshProUGUI left, TextMeshProUGUI right) StatRow(RectTransform parent,
        float y, TMP_FontAsset font, string lLabel, string rLabel)
    {
        // Left column label
        var llRT = new GameObject("LL").AddComponent<RectTransform>();
        llRT.SetParent(parent, false);
        llRT.anchorMin = new Vector2(0f, 1f); llRT.anchorMax = new Vector2(0.5f, 1f);
        llRT.pivot = new Vector2(0f, 1f); llRT.sizeDelta = new Vector2(-14f, 12f);
        llRT.anchoredPosition = new Vector2(12f, y);
        var llT = llRT.gameObject.AddComponent<TextMeshProUGUI>();
        llT.font = font; llT.fontSize = 7.5f;
        llT.color = new Color(0.50f, 0.72f, 1f); llT.fontStyle = FontStyles.Bold;
        llT.alignment = TextAlignmentOptions.MidlineLeft; llT.raycastTarget = false;
        llT.text = lLabel.ToUpper();

        // Left value
        var lvRT = new GameObject("LV").AddComponent<RectTransform>();
        lvRT.SetParent(parent, false);
        lvRT.anchorMin = new Vector2(0f, 1f); lvRT.anchorMax = new Vector2(0.5f, 1f);
        lvRT.pivot = new Vector2(0f, 1f); lvRT.sizeDelta = new Vector2(-14f, 14f);
        lvRT.anchoredPosition = new Vector2(12f, y - 12f);
        var lvT = lvRT.gameObject.AddComponent<TextMeshProUGUI>();
        lvT.font = font; lvT.fontSize = 11f; lvT.color = Color.white;
        lvT.fontStyle = FontStyles.Bold;
        lvT.alignment = TextAlignmentOptions.MidlineLeft; lvT.raycastTarget = false;
        lvT.text = "—";

        // Right column label
        var rlRT = new GameObject("RL").AddComponent<RectTransform>();
        rlRT.SetParent(parent, false);
        rlRT.anchorMin = new Vector2(0.5f, 1f); rlRT.anchorMax = new Vector2(1f, 1f);
        rlRT.pivot = new Vector2(0f, 1f); rlRT.sizeDelta = new Vector2(-14f, 12f);
        rlRT.anchoredPosition = new Vector2(4f, y);
        var rlT = rlRT.gameObject.AddComponent<TextMeshProUGUI>();
        rlT.font = font; rlT.fontSize = 7.5f;
        rlT.color = new Color(0.50f, 0.72f, 1f); rlT.fontStyle = FontStyles.Bold;
        rlT.alignment = TextAlignmentOptions.MidlineLeft; rlT.raycastTarget = false;
        rlT.text = rLabel.ToUpper();

        // Right value
        var rvRT = new GameObject("RV").AddComponent<RectTransform>();
        rvRT.SetParent(parent, false);
        rvRT.anchorMin = new Vector2(0.5f, 1f); rvRT.anchorMax = new Vector2(1f, 1f);
        rvRT.pivot = new Vector2(0f, 1f); rvRT.sizeDelta = new Vector2(-14f, 14f);
        rvRT.anchoredPosition = new Vector2(4f, y - 12f);
        var rvT = rvRT.gameObject.AddComponent<TextMeshProUGUI>();
        rvT.font = font; rvT.fontSize = 11f; rvT.color = Color.white;
        rvT.fontStyle = FontStyles.Bold;
        rvT.alignment = TextAlignmentOptions.MidlineLeft; rvT.raycastTarget = false;
        rvT.text = "—";

        return (lvT, rvT);
    }

    RectTransform SectionHead(RectTransform parent, float y, string text, TMP_FontAsset font)
    {
        var rt = AbsRow(parent, y, 14f, padding: 12f);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = font; tmp.fontSize = 8f; tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(0.38f, 0.62f, 1f);
        tmp.characterSpacing = 2f;
        tmp.alignment = TextAlignmentOptions.MidlineLeft; tmp.raycastTarget = false;
        tmp.text = text;
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
        rt.gameObject.AddComponent<Image>().color = new Color(0.27f, 0.30f, 0.52f, 0.45f);
        return rt;
    }

    // Horizontal strip anchored to top at a given y position
    RectTransform AbsRow(RectTransform parent, float y, float height, float padding = 0f)
    {
        var rt = new GameObject("Row").AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(-24f + padding * 2f, height); // padding adjusts from both sides if negative
        if (padding > 0)
        {
            rt.sizeDelta        = new Vector2(-(24f - 0f), height);
            rt.anchoredPosition = new Vector2(0f, y);
        }
        else
        {
            rt.sizeDelta        = new Vector2(-24f, height);
            rt.anchoredPosition = new Vector2(0f, y);
        }
        return rt;
    }

    // Full-stretch child of any transform
    RectTransform Stretch(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    // Anchored-to-top strip of fixed height
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

    // Create a RectTransform child filling its parent
    RectTransform MakeChild(string name, RectTransform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    // Add a TMP to an existing RectTransform (fills it)
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
