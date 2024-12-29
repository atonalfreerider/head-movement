using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

public class PaperDoll : MonoBehaviour 
{
    List<List<Vector2>> poses2D;
    string imageFolderPath;
    GameObject projectionPlane;
    Material projectionMaterial;
    SkeletonColliders skeletonColliders;

    Vector2 lastScale = Vector2.one;
    float lastRotation = 0f;
    static readonly Vector3 CameraPosition = new(0, 1.2f, 0);

    public void Init(List<List<List<int>>> intPoses, string imageFolderPath)
    {
        // Convert List<List<int>> to List<List<Vector2>> and flip Y coordinates
        poses2D = new List<List<Vector2>>();
        foreach (var pose2D in intPoses)
        {
            List<Vector2> pose2DVector2 = new List<Vector2>();
            foreach (var joint2D in pose2D)
            {
                // Flip Y coordinate since texture coordinates are bottom-up
                pose2DVector2.Add(new Vector2(joint2D[0], joint2D[1]));
            }
            poses2D.Add(pose2DVector2);
        }

        this.imageFolderPath = imageFolderPath;

        // Create projection plane with double-sided material
        projectionPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
        projectionPlane.transform.SetParent(transform, false);
        projectionMaterial = new Material(Shader.Find("Custom/ProjectedTexture"))
        {
            renderQueue = 3000 // Ensure transparent rendering
        };
        projectionPlane.GetComponent<Renderer>().material = projectionMaterial;

        // Initialize skeleton colliders
        skeletonColliders = gameObject.AddComponent<SkeletonColliders>();
    }

    public void SetToFrame(int frameNumber, List<Vector3> pose3D)
    {
        if (frameNumber < 0 || frameNumber >= poses2D.Count) return;

        string imagePath = Path.Combine(imageFolderPath, frameNumber.ToString("D4") + ".png");
        StartCoroutine(LoadImage(imagePath, frameNumber, pose3D));
    }

    IEnumerator LoadImage(string imagePath, int frameNumber, List<Vector3> pose3D)
    {
        using UnityWebRequest www = UnityWebRequestTexture.GetTexture(imagePath);
        yield return www.SendWebRequest();
        
        if (www.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError(www.error);
            yield break;
        }

        Texture2D texture = DownloadHandlerTexture.GetContent(www);
        List<Vector2> pose2D = poses2D[frameNumber];

        // Update skeleton colliders with expanded collision volumes
        skeletonColliders.UpdateColliders(pose3D);

        // Find closest point and corresponding 2D point
        Vector3 spine3D = pose3D[(int)SmplJoint.Spine2];
        Vector2 spine2D = pose2D[(int)SmplJoint.Spine2];
        
        // Convert 2D coordinates to match Unity's coordinate system
        spine2D.y = texture.height - spine2D.y; // Flip Y coordinate

        // Calculate scale using corrected coordinates
        Vector2 head2D = new Vector2(
            pose2D[(int)SmplJoint.Head].x,
            texture.height - pose2D[(int)SmplJoint.Head].y
        );
        Vector2 foot2D = new Vector2(
            (pose2D[(int)SmplJoint.L_Ankle].x + pose2D[(int)SmplJoint.R_Ankle].x) * 0.5f,
            texture.height - ((pose2D[(int)SmplJoint.L_Ankle].y + pose2D[(int)SmplJoint.R_Ankle].y) * 0.5f)
        );
        
        float imageHeight = Vector2.Distance(head2D, foot2D);
        float personHeight3D = Vector3.Distance(pose3D[(int)SmplJoint.Head], 
            (pose3D[(int)SmplJoint.L_Ankle] + pose3D[(int)SmplJoint.R_Ankle]) * 0.5f);
        
        float scale = personHeight3D / imageHeight;

        // Position and orient the plane
        projectionPlane.transform.position = spine3D;
        projectionPlane.transform.LookAt(2 * spine3D - CameraPosition); // Face away from camera
        
        // Calculate UV coordinates
        Vector2 texCenter = new Vector2(
            spine2D.x / texture.width,
            spine2D.y / texture.height
        );

        projectionMaterial.mainTextureScale = Vector2.one;
        projectionMaterial.mainTextureOffset = -texCenter;
        
        // Scale plane
        projectionPlane.transform.localScale = new Vector3(
            scale * texture.width,
            scale * texture.height,
            1
        );

        // Update shader parameters
        projectionMaterial.mainTexture = texture;
        projectionMaterial.SetVector("_CameraPosition", CameraPosition);
        projectionMaterial.SetMatrix("_CollisionPrimitives", skeletonColliders.GetCollisionMatrix());
        projectionMaterial.SetFloat("_DepthFade", 0.05f);
        projectionMaterial.SetFloat("_CollisionSoftness", 0.02f); // Tighter collision boundary
        projectionMaterial.SetFloat("_DeformStrength", 1.0f); // Stronger deformation
    }
}
