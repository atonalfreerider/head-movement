using UnityEngine;

public class LimbSegment
{
    readonly GameObject quad;
    readonly Material material;

    public LimbSegment(Transform parent)
    {
        quad = new GameObject("LimbSegment");
        quad.transform.SetParent(parent, false);

        MeshFilter meshFilter = quad.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = quad.AddComponent<MeshRenderer>();

        Mesh mesh = new()
        {
            vertices = new Vector3[]
            {
                new(-2f, -0.5f, 0),
                new(2f, -0.5f, 0),
                new(-2f, 0.5f, 0),
                new(2f, 0.5f, 0)
            },
            uv = new Vector2[]
            {
                new(0, 0),
                new(1, 0),
                new(0, 1),
                new(1, 1)
            },
            triangles = new int[]
            {
                0, 2, 1,
                2, 3, 1
            }
        };
        meshFilter.mesh = mesh;

        material = new Material(Shader.Find("Custom/DoubleSidedTransparent"))
        {
            color = new Color(1, 1, 1, 0.03f)
        };
        meshRenderer.material = material;
    }

    public void UpdateSegment(Vector3 startPos3D, Vector3 endPos3D, Vector2 startPos2D, Vector2 endPos2D, 
        Texture2D texture, Vector3 cameraPosition)
    {
        Vector3 segmentDir = endPos3D - startPos3D;
        float segmentLength = segmentDir.magnitude;
        Vector3 center = (startPos3D + endPos3D) * 0.5f;

        // Position and orientation
        quad.transform.position = center;
        Vector3 toCamera = (cameraPosition - center).normalized;
        Vector3 segmentDirNorm = segmentDir.normalized;
        Vector3 right = Vector3.Cross(segmentDirNorm, toCamera).normalized;
        Vector3 forward = Vector3.Cross(right, segmentDirNorm).normalized;
        quad.transform.rotation = Quaternion.LookRotation(forward, segmentDirNorm);

        // Calculate pixel length of the segment in 2D
        float pixelLength2D = Vector2.Distance(startPos2D, endPos2D);
        
        // Calculate the world-to-pixel ratio
        float worldToPixelRatio = segmentLength / pixelLength2D;
        
        // Increase width ratio for limbs
        float widthPixels = pixelLength2D * 0.1f; 
        float widthWorld = widthPixels * worldToPixelRatio;

        quad.transform.localScale = new Vector3(widthWorld, segmentLength, 1);

        // Calculate the direction in 2D texture space
        Vector2 segmentDir2D = (endPos2D - startPos2D).normalized;
        Vector2 perpDir2D = new(-segmentDir2D.y, segmentDir2D.x);

        // Calculate UV coordinates
        float halfWidthPixels = pixelLength2D * 0.125f; // Half of the 1:4 ratio width in pixels

        // Calculate corner points in texture space
        Vector2 textureTopLeft = startPos2D + perpDir2D * halfWidthPixels;
        Vector2 textureTopRight = startPos2D - perpDir2D * halfWidthPixels;
        Vector2 textureBottomLeft = endPos2D + perpDir2D * halfWidthPixels;
        Vector2 textureBottomRight = endPos2D - perpDir2D * halfWidthPixels;

        // Convert to UV coordinates (0-1 range)
        Vector2 uvTopLeft = new(textureTopLeft.x / texture.width, 1 - textureTopLeft.y / texture.height);
        Vector2 uvTopRight = new(textureTopRight.x / texture.width, 1 - textureTopRight.y / texture.height);
        Vector2 uvBottomLeft = new(textureBottomLeft.x / texture.width, 1 - textureBottomLeft.y / texture.height);
        Vector2 uvBottomRight = new(textureBottomRight.x / texture.width, 1 - textureBottomRight.y / texture.height);

        // Update mesh UVs - flip topLeft/bottomLeft and topRight/bottomRight
        Mesh mesh = quad.GetComponent<MeshFilter>().mesh;
        mesh.uv = new[] {
            uvTopLeft,      // Was uvBottomLeft
            uvTopRight,     // Was uvBottomRight
            uvBottomLeft,   // Was uvTopLeft
            uvBottomRight   // Was uvTopRight
        };

        // Apply texture
        material.mainTexture = texture;
        material.mainTextureScale = Vector2.one;
        material.mainTextureOffset = Vector2.zero;
        material.mainTexture.wrapMode = TextureWrapMode.Clamp;
    }
}
