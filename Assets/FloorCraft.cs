using System.Collections.Generic;
using UnityEngine;

public class FloorCraft : MonoBehaviour
{
    static readonly int Color1 = Shader.PropertyToID("_Color");
    Dancer Lead;
    Dancer Follow;
    
    bool isInitialized = false;

    GameObject leadStepsContainer;
    GameObject followStepsContainer;

    public void Init(Dancer lead, Dancer follow, Material bloomMat, int frameCount)
    {
        Material leadMat = new(Shader.Find("Unlit/Color"));
        leadMat.SetColor(Color1, Color.red);
        Material followMat = new(Shader.Find("Unlit/Color"));
        followMat.SetColor(Color1, Color.white);
        Lead = lead;
        Follow = follow;
        
        leadStepsContainer = new GameObject("Lead Steps");
        followStepsContainer = new GameObject("Follow Steps");
        
        // create a list of every foot position where y == 0 for the lead and follow dancers
        List<Vector2> leadGroundContact = new List<Vector2>();
        List<Vector2> followGroundContact = new List<Vector2>();
        
        for(int i = 0; i < frameCount; i++)
        {
            Vector3 leadLeft = Lead.GetLeftFoot(i);
            Vector3 leadRight = Lead.GetRightFoot(i);
            
            Vector3 followLeft = Follow.GetLeftFoot(i);
            Vector3 followRight = Follow.GetRightFoot(i);
            
            const float minFootY = 0.01f;
            
            if(leadLeft.y < minFootY)
            {
                leadGroundContact.Add(new Vector2(leadLeft.x, leadLeft.z));
            }
            if(leadRight.y < minFootY)
            {
                leadGroundContact.Add(new Vector2(leadRight.x, leadRight.z));
            }
            if(followLeft.y < minFootY)
            {
                followGroundContact.Add(new Vector2(followLeft.x, followLeft.z));
            }
            if(followRight.y < minFootY)
            {
                followGroundContact.Add(new Vector2(followRight.x, followRight.z));
            }
        }

        int count = 0;
        foreach (Vector2 vector2 in leadGroundContact)
        {
            if (count % 5 == 0)
            {
                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.transform.position = new Vector3(vector2.x, 0, vector2.y);
                quad.transform.localScale = Vector3.one * .02f;
                quad.transform.rotation = Quaternion.Euler(90, 0, 0);
                quad.transform.SetParent(leadStepsContainer.transform);
                quad.GetComponent<Renderer>().material = leadMat;
            }
            
            count++;
        }

        count = 0;
        foreach (Vector2 vector2 in followGroundContact)
        {
            if (count % 5 == 0)
            {
                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.transform.position = new Vector3(vector2.x, 0, vector2.y);
                quad.transform.localScale = Vector3.one * .02f;
                quad.transform.rotation = Quaternion.Euler(90, 0, 0);
                quad.transform.SetParent(followStepsContainer.transform);
                quad.GetComponent<Renderer>().material = followMat;
            }
            
            count++;
        }
        
        isInitialized = true;

    }

}