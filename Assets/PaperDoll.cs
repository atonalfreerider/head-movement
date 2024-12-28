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
    Dictionary<(SmplJoint, SmplJoint), LimbSegment> limbSegments;
    GameObject bodyQuad;
    Material bodyMaterial;

    private static readonly (SmplJoint, SmplJoint)[] limbDefinitions = new[]
    {
        (SmplJoint.L_Shoulder, SmplJoint.L_Elbow),    // Left upper arm
        (SmplJoint.L_Elbow, SmplJoint.L_Wrist),       // Left lower arm
        (SmplJoint.L_Hip, SmplJoint.L_Knee),          // Left thigh
        (SmplJoint.L_Knee, SmplJoint.L_Ankle),        // Left calf
        (SmplJoint.R_Shoulder, SmplJoint.R_Elbow),    // Right upper arm
        (SmplJoint.R_Elbow, SmplJoint.R_Wrist),       // Right lower arm
        (SmplJoint.R_Hip, SmplJoint.R_Knee),          // Right thigh
        (SmplJoint.R_Knee, SmplJoint.R_Ankle),        // Right calf
    };

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
        limbSegments = new Dictionary<(SmplJoint, SmplJoint), LimbSegment>();
        foreach (var (start, end) in limbDefinitions)
        {
            limbSegments.Add((start, end), new LimbSegment(start, end, transform));
        }

        // Create body quad
        bodyQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bodyQuad.transform.SetParent(transform, false);
        bodyMaterial = new Material(Shader.Find("Custom/DoubleSidedTransparent"))
        {
            color = new Color(1, 1, 1, 0.5f)
        };
        bodyQuad.GetComponent<Renderer>().material = bodyMaterial;
    }

    public void SetToFrame(int frameNumber, List<Vector3> pose3D)
    {
        if (frameNumber < 0 || frameNumber >= poses2D.Count) return;

        string imagePath = Path.Combine(imageFolderPath, frameNumber.ToString("D4") + ".png");
        StartCoroutine(LoadImage(imagePath, frameNumber, pose3D));
    }

    IEnumerator LoadImage(string imagePath, int frameNumber, List<Vector3> pose3D)
    {
        using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(imagePath))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
            {
                Debug.LogError(www.error);
            }
            else
            {
                Texture2D texture = DownloadHandlerTexture.GetContent(www);
                Vector3 cameraPosition = new Vector3(0, 1.2f, 0);

                // Update all limb segments
                foreach (var (start, end) in limbDefinitions)
                {
                    Vector3 startPos3D = pose3D[(int)start];
                    Vector3 endPos3D = pose3D[(int)end];
                    Vector2 startPos2D = poses2D[frameNumber][(int)start];
                    Vector2 endPos2D = poses2D[frameNumber][(int)end];

                    limbSegments[(start, end)].UpdateSegment(
                        startPos3D, endPos3D, startPos2D, endPos2D, texture, cameraPosition);
                }

                // Update body quad
                UpdateBodyQuad(frameNumber, pose3D, texture, cameraPosition);
            }
        }
    }

    private void UpdateBodyQuad(int frameNumber, List<Vector3> pose3D, Texture2D texture, Vector3 cameraPosition)
    {
        Vector3 headPos = pose3D[(int)SmplJoint.Head];
        Vector3 pelvisPos = pose3D[(int)SmplJoint.Pelvis];
        Vector2 head2D = poses2D[frameNumber][(int)SmplJoint.Head];
        Vector2 pelvis2D = poses2D[frameNumber][(int)SmplJoint.Pelvis];

        // Position and orient body quad
        float bodyHeight = Vector3.Distance(headPos, pelvisPos);
        float pixel2DHeight = Vector2.Distance(head2D, pelvis2D);
        float scale = bodyHeight / pixel2DHeight;
        float aspectRatio = (float)texture.width / texture.height;

        Vector3 centerPos = (headPos + pelvisPos) * 0.5f;
        bodyQuad.transform.position = centerPos;
        bodyQuad.transform.LookAt(cameraPosition);
        bodyQuad.transform.localScale = new Vector3(scale * aspectRatio, bodyHeight, 1);

        // Calculate UV coordinates to map texture along torso
        float startU = head2D.x / texture.width;
        float endU = pelvis2D.x / texture.width;
        float startV = 1 - head2D.y / texture.height; // Flip V coordinate
        float endV = 1 - pelvis2D.y / texture.height; // Flip V coordinate

        // Apply UV transformation to material
        bodyMaterial.mainTexture = texture;
        bodyMaterial.mainTextureScale = new Vector2(1, endV - startV); // Keep the texture scale constant
        bodyMaterial.mainTextureOffset = new Vector2(startU, startV);
    }
}
