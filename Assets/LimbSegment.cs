using UnityEngine;

public class LimbSegment
{
    public GameObject quad;
    public Material material;
    private readonly SmplJoint startJoint;
    private readonly SmplJoint endJoint;

    public LimbSegment(SmplJoint startJoint, SmplJoint endJoint, Transform parent)
    {
        this.startJoint = startJoint;
        this.endJoint = endJoint;

        quad = new GameObject("LimbSegment");
        quad.transform.SetParent(parent, false);

        MeshFilter meshFilter = quad.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = quad.AddComponent<MeshRenderer>();

        Mesh mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-2f, -0.5f, 0),
            new Vector3(2f, -0.5f, 0),
            new Vector3(-2f, 0.5f, 0),
            new Vector3(2f, 0.5f, 0)
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(0, 1),
            new Vector2(1, 1)
        };
        mesh.triangles = new int[]
        {
            0, 2, 1,
            2, 3, 1
        };
        meshFilter.mesh = mesh;

        material = new Material(Shader.Find("Custom/DoubleSidedTransparent"))
        {
            color = new Color(1, 1, 1, 0.5f)
        };
        meshRenderer.material = material;
    }

    public void UpdateSegment(Vector3 startPos3D, Vector3 endPos3D, Vector2 startPos2D, Vector2 endPos2D, 
        Texture2D texture, Vector3 cameraPosition)
    {
        Vector3 segmentDir = endPos3D - startPos3D;
        float segmentLength = segmentDir.magnitude;
        Vector3 center = (startPos3D + endPos3D) * 0.5f;

        // Position at center of segment
        quad.transform.position = center;

        // Orient towards camera while maintaining segment direction
        Vector3 toCamera = (cameraPosition - center).normalized;
        Vector3 segmentDirNorm = segmentDir.normalized;
        Vector3 right = Vector3.Cross(segmentDirNorm, toCamera).normalized;
        Vector3 forward = Vector3.Cross(right, segmentDirNorm).normalized;
        quad.transform.rotation = Quaternion.LookRotation(forward, segmentDirNorm);

        // Calculate scale
        float heightScale = segmentLength; // Scale height based on segment length

        // Apply overextension only for calves and forearms
        float overextendFactor = (startJoint == SmplJoint.L_Knee && endJoint == SmplJoint.L_Ankle) ||
                                 (startJoint == SmplJoint.R_Knee && endJoint == SmplJoint.R_Ankle) ||
                                 (startJoint == SmplJoint.L_Elbow && endJoint == SmplJoint.L_Wrist) ||
                                 (startJoint == SmplJoint.R_Elbow && endJoint == SmplJoint.R_Wrist) ? 1.2f : 1.0f;

        // Adjust the quad shape to be 1:4 width-to-height ratio
        float widthScale = heightScale / 4;

        quad.transform.localScale = new Vector3(widthScale, heightScale * overextendFactor, 1);

        // Calculate UV coordinates to map texture along segment
        float startU = startPos2D.x / texture.width;
        float endU = endPos2D.x / texture.width;
        float startV = 1 - startPos2D.y / texture.height; // Flip V coordinate
        float endV = 1 - endPos2D.y / texture.height; // Flip V coordinate

        // Calculate the aspect ratio
        float aspectRatio = (float)texture.width / texture.height;

        // Calculate the scale ratio to achieve 1:1 optical scale
        float uvWidthScale = aspectRatio * 0.25f; // Make the texture width scale much wider

        // Apply UV transformation to material
        material.mainTexture = texture;
        material.mainTextureScale = new Vector2(uvWidthScale, endV - startV); // Adjust the texture scale to achieve 1:1 optical scale
        material.mainTextureOffset = new Vector2(startU - (uvWidthScale / 2), startV); // Center the texture width

        // Set texture wrap mode to clamp to prevent repeating
        material.mainTexture.wrapMode = TextureWrapMode.Clamp;
    }
}
