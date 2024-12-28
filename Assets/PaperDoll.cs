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
    GameObject quad;
    Material material;

    public void Init(List<List<List<int>>> intPoses, string imageFolderPath)
    {
        // Convert List<List<int>> to List<List<Vector2>>
        poses2D = new List<List<Vector2>>();
        foreach (List<Vector2> pose2DVector2 in intPoses
                     .Select(pose2D => pose2D.Select(joint2D => new Vector2(
                         joint2D[0], 
                         joint2D[1])).ToList()))
        {
            poses2D.Add(pose2DVector2);
        }

        this.imageFolderPath = imageFolderPath;
    }

    void Awake()
    {
        quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(transform, false);
        material = new Material(Shader.Find("Custom/DoubleSidedTransparent"))
        {
            color = new Color(1, 1, 1, 0.1f)
        };
        quad.GetComponent<Renderer>().material = material;
    }

    public void SetToFrame(int frameNumber, Vector3 leftShoulder3D, Vector3 rightHip3D)
    {
        if (frameNumber < 0 || frameNumber >= poses2D.Count) return;

        string imagePath = Path.Combine(imageFolderPath, frameNumber.ToString("D4") + ".png");
        StartCoroutine(LoadImage(imagePath, frameNumber, leftShoulder3D, rightHip3D));
    }

    IEnumerator LoadImage(string imagePath, int frameNumber, Vector3 leftShoulder3D, Vector3 rightHip3D)
    {
        using UnityWebRequest www = UnityWebRequestTexture.GetTexture(imagePath);
        yield return www.SendWebRequest();
        if (www.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError(www.error);
        }
        else
        {
            Texture2D texture = DownloadHandlerTexture.GetContent(www);
            material.mainTexture = texture;

            Vector2 leftShoulder2D = poses2D[frameNumber][Extensions.GetSmplJointIndex("L_Shoulder")];
            Vector2 rightHip2D = poses2D[frameNumber][Extensions.GetSmplJointIndex("R_Hip")];

            Vector3 cameraPosition = new Vector3(0, 1.2f, 0);
            Vector3 forward = (leftShoulder3D - cameraPosition).normalized;
                
            // Calculate scale first
            float imageHeight = texture.height;
            float worldDistance = Vector3.Distance(leftShoulder3D, rightHip3D);
            float pixelDistance = Vector2.Distance(leftShoulder2D, rightHip2D);
            float scale = worldDistance / (pixelDistance / imageHeight);
            float aspectRatio = (float)texture.width / texture.height;
                
            // Calculate UV coordinates of the shoulder (normalized 0-1 space)
            Vector2 shoulderUV = new Vector2(
                leftShoulder2D.x / texture.width,
                leftShoulder2D.y / texture.height
            );
                
            // Calculate offset from center to shoulder in world units
            Vector3 offsetX = quad.transform.right * (shoulderUV.x - 0.5f) * scale * aspectRatio;
            Vector3 offsetY = quad.transform.up * (shoulderUV.y - 0.5f) * scale;
                
            // Position the quad
            quad.transform.rotation = Quaternion.LookRotation(forward);
            quad.transform.position = leftShoulder3D + offsetX + offsetY;
                
            // Apply the scale
            quad.transform.localScale = new Vector3(scale * aspectRatio, scale, 1);
        }
    }
}
