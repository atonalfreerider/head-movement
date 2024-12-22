using Shapes;
using UnityEngine;

public class ContactDetection : MonoBehaviour
{
    Dancer Lead;
    Dancer Follow;

    Polygon LRTri;
    Polygon RLTri;
    
    public void Init(Dancer lead, Dancer follow, Material bloomMat)
    {
        Lead = lead;
        Follow = follow;

        LRTri = Instantiate(PolygonFactory.Instance.tetra).GetComponent<Polygon>();
        LRTri.transform.localScale = Vector3.one * .02f;
        LRTri.gameObject.SetActive(true);
        
        RLTri = Instantiate(PolygonFactory.Instance.tetra).GetComponent<Polygon>();
        RLTri.transform.localScale = Vector3.one * .02f;
        RLTri.gameObject.SetActive(true);
    }

    public void DetectContact(int frameNumber)
    {
        Vector3 leadLeftHandPos = Lead.GetLeftHandContact(frameNumber);
        Vector3 leadRightHandPos = Lead.GetRightHandContact(frameNumber);

        Vector3 followLeftHandPos = Follow.GetLeftHandContact(frameNumber);
        Vector3 followRightHandPos = Follow.GetRightHandContact(frameNumber);

        Vector3 LRMid = Vector3.Lerp(leadLeftHandPos, followRightHandPos, 0.5f);
        float LRDistance = Vector3.Distance(leadLeftHandPos, followRightHandPos);
        Vector3 RLMid = Vector3.Lerp(leadRightHandPos, followLeftHandPos, 0.5f);
        float RLDistance = Vector3.Distance(leadRightHandPos, followLeftHandPos);

        LRTri.transform.position = LRMid;
        RLTri.transform.position = RLMid;

        Color LRColor = Cividis.CividisColor(Mathf.Min(1, LRDistance));
        Color RLColor = Cividis.CividisColor(Mathf.Min(1, RLDistance));

        LRColor *= Mathf.Pow(2, 3 - LRDistance*2);
        RLColor *= Mathf.Pow(2, 3 - RLDistance*2);
        
        LRTri.SetColor(LRColor);
        RLTri.SetColor(RLColor);

        Vector3 LLMid = Vector3.Lerp(leadLeftHandPos, followLeftHandPos, 0.5f);
        Vector3 RRMid = Vector3.Lerp(leadRightHandPos, followRightHandPos, 0.5f);
    }

}