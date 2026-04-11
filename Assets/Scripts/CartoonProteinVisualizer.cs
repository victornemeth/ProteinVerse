using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public struct ExtendedSplineSegment
{
    public Vector3 startPoint, endPoint;
    public Vector3 startTangent, endTangent;
    public Vector3 startNormal, endNormal;
    public Color startColor, endColor;
    public Vector3 startScale, endScale;
    public float startRadius, endRadius;
}

public class CartoonProteinVisualizer : MonoBehaviour
{
    public Material splineMaterial;
    
    private ComputeBuffer segmentBuffer;
    private ComputeBuffer argsBuffer;
    private Mesh instancedMesh;
    private Bounds renderBounds;
    private bool isReady = false;

    private void Start()
    {
        if (splineMaterial == null)
        {
            splineMaterial = new Material(Shader.Find("Nanover/Spline/ExtendedTetrahedral"));
        }
        CreateCylinderMesh();
    }

    private void OnDestroy()
    {
        if (segmentBuffer != null) segmentBuffer.Release();
        if (argsBuffer != null) argsBuffer.Release();
    }

    private void Update()
    {
        if (isReady && splineMaterial != null && segmentBuffer != null && argsBuffer != null)
        {
            splineMaterial.SetBuffer("_Segments", segmentBuffer);
            Graphics.DrawMeshInstancedIndirect(instancedMesh, 0, splineMaterial, renderBounds, argsBuffer);
        }
    }

    public void FetchAndRender(string url)
    {
        StartCoroutine(DownloadAndParse(url));
    }

    private IEnumerator DownloadAndParse(string url)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                ParseAndRender(req.downloadHandler.text);
                transform.localScale = Vector3.one * 0.1f;
            }
            else
            {
                Debug.LogError($"Failed to download PDB: {req.error}");
            }
        }
    }

    private enum SecStruct { Loop, Helix, Sheet }

    private class Residue
    {
        public int id;
        public string chain;
        public Vector3 CA, O, N;
        public bool hasCA, hasO, hasN;
        public SecStruct ss = SecStruct.Loop;
        public Color color = Color.white;
    }

    public void ParseAndRender(string pdbContent)
    {
        List<Residue> residues = new List<Residue>();
        Residue currentResidue = null;
        int lastResId = -1;
        string lastChain = "";

        // Secondary structure ranges
        List<(string chain, int start, int end, SecStruct ss)> secStructs = new List<(string, int, int, SecStruct)>();

        using (StringReader reader = new StringReader(pdbContent))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("HELIX "))
                {
                    string chain = line.Substring(19, 1).Trim();
                    int start = int.Parse(line.Substring(21, 4).Trim());
                    int end = int.Parse(line.Substring(33, 4).Trim());
                    secStructs.Add((chain, start, end, SecStruct.Helix));
                }
                else if (line.StartsWith("SHEET "))
                {
                    string chain = line.Substring(21, 1).Trim();
                    int start = int.Parse(line.Substring(22, 4).Trim());
                    int end = int.Parse(line.Substring(33, 4).Trim());
                    secStructs.Add((chain, start, end, SecStruct.Sheet));
                }
                else if (line.StartsWith("ATOM  ") || line.StartsWith("HETATM"))
                {
                    string atomName = line.Substring(12, 4).Trim();
                    if (atomName != "CA" && atomName != "O" && atomName != "N") continue;

                    string chain = line.Substring(21, 1).Trim();
                    int resId = int.Parse(line.Substring(22, 4).Trim());

                    if (resId != lastResId || chain != lastChain)
                    {
                        if (currentResidue != null && currentResidue.hasCA)
                        {
                            residues.Add(currentResidue);
                        }
                        currentResidue = new Residue { id = resId, chain = chain };
                        lastResId = resId;
                        lastChain = chain;
                    }

                    float x = float.Parse(line.Substring(30, 8).Trim(), CultureInfo.InvariantCulture);
                    float y = float.Parse(line.Substring(38, 8).Trim(), CultureInfo.InvariantCulture);
                    float z = float.Parse(line.Substring(46, 8).Trim(), CultureInfo.InvariantCulture);
                    Vector3 pos = new Vector3(x, y, z);

                    if (atomName == "CA") { currentResidue.CA = pos; currentResidue.hasCA = true; }
                    else if (atomName == "O") { currentResidue.O = pos; currentResidue.hasO = true; }
                    else if (atomName == "N") { currentResidue.N = pos; currentResidue.hasN = true; }
                }
            }
            if (currentResidue != null && currentResidue.hasCA) residues.Add(currentResidue);
        }

        // Apply secondary structures
        foreach (var res in residues)
        {
            foreach (var ss in secStructs)
            {
                if (res.chain == ss.chain && res.id >= ss.start && res.id <= ss.end)
                {
                    res.ss = ss.ss;
                    break;
                }
            }
            
            // Simple color scheme based on SS
            if (res.ss == SecStruct.Helix) res.color = new Color(1f, 0.4f, 0.4f);
            else if (res.ss == SecStruct.Sheet) res.color = new Color(0.4f, 0.8f, 1f);
            else res.color = new Color(0.8f, 0.8f, 0.8f);
        }

        List<ExtendedSplineSegment> segments = new List<ExtendedSplineSegment>();
        Vector3 center = Vector3.zero;

        // Split by chains
        List<List<Residue>> chains = new List<List<Residue>>();
        List<Residue> currentChain = new List<Residue>();
        
        for (int i = 0; i < residues.Count; i++)
        {
            if (i > 0 && (residues[i].chain != residues[i - 1].chain || Vector3.Distance(residues[i].CA, residues[i - 1].CA) > 5.0f))
            {
                if (currentChain.Count > 0) chains.Add(new List<Residue>(currentChain));
                currentChain.Clear();
            }
            currentChain.Add(residues[i]);
            center += residues[i].CA;
        }
        if (currentChain.Count > 0) chains.Add(currentChain);
        
        if (residues.Count > 0) center /= residues.Count;

        foreach (var chain in chains)
        {
            if (chain.Count < 2) continue;

            Vector3[] tangents = new Vector3[chain.Count];
            Vector3[] normals = new Vector3[chain.Count];

            for (int i = 0; i < chain.Count; i++)
            {
                Vector3 p_prev = i > 0 ? chain[i - 1].CA : chain[i].CA - (chain[i + 1].CA - chain[i].CA);
                Vector3 p_next = i < chain.Count - 1 ? chain[i + 1].CA : chain[i].CA + (chain[i].CA - chain[i - 1].CA);
                tangents[i] = (p_next - p_prev) * 0.5f;

                if (chain[i].hasN && chain[i].hasO)
                {
                    normals[i] = Vector3.Cross(chain[i].N - chain[i].CA, chain[i].O - chain[i].CA).normalized;
                }
                else
                {
                    normals[i] = i > 0 ? normals[i - 1] : Vector3.up;
                }
            }

            for (int i = 0; i < chain.Count - 1; i++)
            {
                var r1 = chain[i];
                var r2 = chain[i + 1];

                Vector3 scale1 = GetScaleForSS(r1.ss);
                Vector3 scale2 = GetScaleForSS(r2.ss);

                // Arrowhead logic for end of sheet
                if (r1.ss == SecStruct.Sheet && r2.ss != SecStruct.Sheet)
                {
                    scale2 = new Vector3(scale1.x * 1.5f, scale1.y, scale1.z); // Flare out
                }
                else if (i > 0 && chain[i - 1].ss == SecStruct.Sheet && r1.ss == SecStruct.Sheet && r2.ss != SecStruct.Sheet)
                {
                    scale1 = new Vector3(scale1.x * 1.5f, scale1.y, scale1.z);
                    scale2 = new Vector3(0.1f, scale1.y, 0.1f); // Pointy tip
                }

                ExtendedSplineSegment seg = new ExtendedSplineSegment
                {
                    startPoint = r1.CA - center,
                    endPoint = r2.CA - center,
                    startTangent = tangents[i],
                    endTangent = tangents[i + 1],
                    startNormal = normals[i],
                    endNormal = normals[i + 1],
                    startColor = r1.color,
                    endColor = r2.color,
                    startScale = scale1,
                    endScale = scale2,
                    startRadius = 0.4f,
                    endRadius = 0.4f
                };
                segments.Add(seg);
            }
        }

        if (segments.Count == 0) return;

        if (segmentBuffer != null) segmentBuffer.Release();
        int stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(ExtendedSplineSegment));
        segmentBuffer = new ComputeBuffer(segments.Count, stride);
        segmentBuffer.SetData(segments.ToArray());

        if (argsBuffer != null) argsBuffer.Release();
        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
        args[0] = (uint)instancedMesh.GetIndexCount(0);
        args[1] = (uint)segments.Count;
        args[2] = (uint)instancedMesh.GetIndexStart(0);
        args[3] = (uint)instancedMesh.GetBaseVertex(0);
        argsBuffer.SetData(args);

        renderBounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        isReady = true;
    }

    private Vector3 GetScaleForSS(SecStruct ss)
    {
        if (ss == SecStruct.Helix) return new Vector3(4f, 0.5f, 1f);
        if (ss == SecStruct.Sheet) return new Vector3(4f, 0.5f, 1f);
        return Vector3.one;
    }

    private void CreateCylinderMesh()
    {
        int radialSegments = 8;
        int heightSegments = 10;
        
        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<int> triangles = new List<int>();

        for (int y = 0; y <= heightSegments; y++)
        {
            float v = (float)y / heightSegments;
            for (int x = 0; x <= radialSegments; x++)
            {
                float u = (float)x / radialSegments;
                float angle = u * Mathf.PI * 2f;
                
                float px = Mathf.Cos(angle);
                float pz = Mathf.Sin(angle);
                
                vertices.Add(new Vector3(px, v, pz));
                normals.Add(new Vector3(px, 0, pz).normalized);
            }
        }

        for (int y = 0; y < heightSegments; y++)
        {
            for (int x = 0; x < radialSegments; x++)
            {
                int i0 = y * (radialSegments + 1) + x;
                int i1 = i0 + 1;
                int i2 = i0 + (radialSegments + 1);
                int i3 = i2 + 1;

                triangles.Add(i0);
                triangles.Add(i2);
                triangles.Add(i1);

                triangles.Add(i1);
                triangles.Add(i2);
                triangles.Add(i3);
            }
        }

        instancedMesh = new Mesh();
        instancedMesh.SetVertices(vertices);
        instancedMesh.SetNormals(normals);
        instancedMesh.SetTriangles(triangles, 0);
        instancedMesh.RecalculateBounds();
    }
}