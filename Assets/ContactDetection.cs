using System;
using System.Collections.Generic;
using Shapes;
using UnityEngine;

public class ContactDetection : MonoBehaviour
{
    Dancer Lead;
    Dancer Follow;

    Polygon leadLefHandOrb;
    Polygon leadRightHandOrb;

    LineRenderer leadLeftArmContact;
    LineRenderer leadRightArmContact;
    LineRenderer leadBodyContact;
    LineRenderer leadRightThighContact;
    
    bool isInitialized = false;

    public void Init(Dancer lead, Dancer follow, Material bloomMat)
    {
        Lead = lead;
        Follow = follow;

        leadLeftArmContact = NewLineRenderer(0.02f, bloomMat);
        leadRightArmContact = NewLineRenderer(0.02f, bloomMat);
        leadBodyContact = NewLineRenderer(0.02f, bloomMat);
        leadRightThighContact = NewLineRenderer(0.02f, bloomMat);

        leadLefHandOrb = Instantiate(PolygonFactory.Instance.icosahedron0).GetComponent<Polygon>();
        leadLefHandOrb.transform.localScale = Vector3.one * .01f;
        leadLefHandOrb.gameObject.SetActive(true);

        leadRightHandOrb = Instantiate(PolygonFactory.Instance.icosahedron0).GetComponent<Polygon>();
        leadRightHandOrb.transform.localScale = Vector3.one * .01f;
        leadRightHandOrb.gameObject.SetActive(true);

        isInitialized = true;
    }

    public void Reset()
    {
        if (!isInitialized) return;
        
        Destroy(leadLefHandOrb.gameObject);
        Destroy(leadRightHandOrb.gameObject);
        Destroy(leadLeftArmContact.gameObject);
        Destroy(leadRightArmContact.gameObject);
        Destroy(leadBodyContact.gameObject);
        Destroy(leadRightThighContact.gameObject);
        
        isInitialized = false;
    }

    /// <summary>
    /// For every valid contact point, find the corresponding closest point
    ///
    /// Lead Right arm line, start the line at the end of hand (or up the arm) and continue to the shoulder top
    /// Lead Left arm line, same
    /// Body contact, start at chin and go straight down to right hip
    /// Leg - arc from inner to outer thigh
    /// </summary>
    /// <param name="frameNumber"></param>
    public void DetectContact(int frameNumber)
    {
        // for the lead, there are 4 lines of contact with the follow

        // lead left arm   
        Vector3 leadLeftHandPos = Lead.GetLeftHandContact(frameNumber);
        Vector3 leadLeftForearmPos = Lead.GetLeftForearm(frameNumber);
        Vector3 leadLeftElbowPos = Lead.GetLeftElbow(frameNumber);
        Vector3 leadLeftUpperArmPos = Lead.GetLeftUpperArm(frameNumber);
        Vector3 leadLeftShoulderPos = Lead.GetLeftShoulder(frameNumber);

        List<Vector3> leadLeftArm = new()
        {
            leadLeftHandPos,
            leadLeftForearmPos,
            leadLeftElbowPos,
            leadLeftUpperArmPos,
            leadLeftShoulderPos
        };

        // lead right arm
        Vector3 leadRightHandPos = Lead.GetRightHandContact(frameNumber);
        Vector3 leadRightForearmPos = Lead.GetRightForearm(frameNumber);
        Vector3 leadRightElbowPos = Lead.GetRightElbow(frameNumber);
        Vector3 leadRightUpperArmPos = Lead.GetRightUpperArm(frameNumber);
        Vector3 leadRightShoulderPos = Lead.GetRightShoulder(frameNumber);

        List<Vector3> leadRightArm = new()
        {
            leadRightHandPos,
            leadRightForearmPos,
            leadRightElbowPos,
            leadRightUpperArmPos,
            leadRightShoulderPos
        };

        // lead right body ventral line
        Vector3 leadRightChinPos = Lead.GetRightChin(frameNumber);
        Vector3 leadRightPectoralPos = Lead.GetRightPectoral(frameNumber);

        List<Vector3> leadBody = new()
        {
            leadRightChinPos,
            leadRightPectoralPos
        };

        // lead right thigh
        Vector3 leadRightHipPos = Lead.GetRightHip(frameNumber);
        Vector3 leadRightThighPos = Lead.GetRightThigh(frameNumber);
        Vector3 leadRightKneePos = Lead.GetRightKnee(frameNumber);

        List<Vector3> leadRightThigh = new()
        {
            leadRightHipPos,
            leadRightThighPos,
            leadRightKneePos
        };

        // for the follow, all points are merged together
        // follow left arm
        Vector3 followLeftHandPos = Follow.GetLeftHandContact(frameNumber);
        Vector3 followLeftForearmPos = Follow.GetLeftForearm(frameNumber);
        Vector3 followLeftElbowPos = Follow.GetLeftElbow(frameNumber);
        Vector3 followLeftUpperArmPos = Follow.GetLeftUpperArm(frameNumber);
        Vector3 followLeftShoulderPos = Follow.GetLeftShoulder(frameNumber);
        // follow right arm
        Vector3 followRightHandPos = Follow.GetRightHandContact(frameNumber);
        Vector3 followRightForearmPos = Follow.GetRightForearm(frameNumber);
        Vector3 followRightElbowPos = Follow.GetRightElbow(frameNumber);
        Vector3 followRightUpperArmPos = Follow.GetRightUpperArm(frameNumber);
        Vector3 followRightShoulderPos = Follow.GetRightShoulder(frameNumber);

        Vector3 followRightChinPos = Follow.GetRightChin(frameNumber);
        Vector3 followLeftChinPos = Follow.GetLeftChin(frameNumber);

        Vector3 followRightPectoralPos = Follow.GetRightPectoral(frameNumber);
        Vector3 followLeftPectoralPos = Follow.GetLeftPectoral(frameNumber);

        // follow back
        Vector3 followNeckNapePos = Follow.GetNeckNape(frameNumber);

        Vector3 followRightHipPos = Follow.GetRightHip(frameNumber);
        Vector3 followRightThighPos = Follow.GetRightThigh(frameNumber);

        Vector3 followLeftHipPos = Follow.GetLeftHip(frameNumber);
        Vector3 followLeftThighPos = Follow.GetLeftThigh(frameNumber);

        Vector3 followSpine1Pos = Follow.GetSpine1(frameNumber);
        Vector3 followSpine2Pos = Follow.GetSpine2(frameNumber);
        Vector3 followSpine3Pos = Follow.GetSpine3(frameNumber);

        List<Vector3> allFollowPoints = new()
        {
            followLeftHandPos,
            followLeftForearmPos,
            followLeftElbowPos,
            followLeftUpperArmPos,
            followLeftShoulderPos,
            followRightHandPos,
            followRightForearmPos,
            followRightElbowPos,
            followRightUpperArmPos,
            followRightShoulderPos,
            followRightChinPos,
            followLeftChinPos,
            followRightPectoralPos,
            followLeftPectoralPos,
            followNeckNapePos,
            followRightHipPos,
            followRightThighPos,
            followLeftHipPos,
            followLeftThighPos,
            followSpine1Pos,
            followSpine2Pos,
            followSpine3Pos
        };


        
        UpdateHandOrbs(leadLeftHandPos, leadRightHandPos, followLeftHandPos, followRightHandPos, leadLeftElbowPos, leadRightElbowPos);
    }

    void UpdateHandOrbs(Vector3 leadLeftHandPos, Vector3 leadRightHandPos, Vector3 followLeftHandPos, Vector3 followRightHandPos, Vector3 leadLeftElbowPos, Vector3 leadRightElbowPos)
    {
        float LRDistance = Vector3.Distance(leadLeftHandPos, followRightHandPos);
        float LLDistance = Vector3.Distance(leadLeftHandPos, followLeftHandPos);
        float leadLeftD = LRDistance;
        bool leftNormal = true;
        Vector3 leadLeftPos = Vector3.Lerp(leadLeftHandPos, followRightHandPos, 0.5f);
        if (LLDistance < LRDistance)
        {
            leftNormal = false;
            leadLeftD = LLDistance;
            leadLeftPos = Vector3.Lerp(leadLeftHandPos, followLeftHandPos, 0.5f);
        }

        float RLDistance = Vector3.Distance(leadRightHandPos, followLeftHandPos);
        float RRDistance = Vector3.Distance(leadRightHandPos, followRightHandPos);
        float leadRightD = RLDistance;
        bool rightNormal = true;
        Vector3 leadRightPos = Vector3.Lerp(leadRightHandPos, followLeftHandPos, 0.5f);
        if (RRDistance < RLDistance)
        {
            rightNormal = false;
            leadRightD = RRDistance;
            leadRightPos = Vector3.Lerp(leadRightHandPos, followRightHandPos, 0.5f);
        }

        leadLefHandOrb.transform.position = leadLeftPos;
        leadRightHandOrb.transform.position = leadRightPos;

        Color LRColor = ColorUtils.CividisColor(Mathf.Min(1, leadLeftD));
        Color RLColor = ColorUtils.CividisColor(Mathf.Min(1, leadRightD));

        LRColor *= Mathf.Pow(2, 3 - LRDistance * 2);
        RLColor *= Mathf.Pow(2, 3 - RLDistance * 2);

        leadLefHandOrb.SetColor(LRColor);
        leadRightHandOrb.SetColor(RLColor);

        leadLefHandOrb.gameObject.SetActive(leadLeftD < .8f);
        leadRightHandOrb.gameObject.SetActive(leadRightD < .8f);

        if (leftNormal && !rightNormal && leadLeftD < leadRightD)
        {
            leadRightHandOrb.gameObject.SetActive(false);
        }
        else if (!leftNormal && rightNormal && leadRightD < leadLeftD)
        {
            leadLefHandOrb.gameObject.SetActive(false);
        }
        
        // if the vector that points from the left elbow to the left hand, as it extends away from the left hand is
        // pointing away from the current left orb position, hide the left orb
        Vector3 leftElbowToHand = leadLeftHandPos - leadLeftElbowPos;
        Vector3 leftOrbToHand = leadLefHandOrb.transform.position - leadLeftHandPos;
        if (leadLeftD > .12f && Vector3.Dot(leftElbowToHand, leftOrbToHand) < 0)
        {
            leadLefHandOrb.gameObject.SetActive(false);
        }

        Vector3 rightElbowToHand = leadRightHandPos - leadRightElbowPos;
        Vector3 rightOrbToHand = leadRightHandOrb.transform.position - leadRightHandPos;
        if (leadRightD > .12f && Vector3.Dot(rightElbowToHand, rightOrbToHand) < 0)
        {
            leadRightHandOrb.gameObject.SetActive(false);
        }
    }

    static Tuple<Vector3[], float[]> ContactLine(List<Vector3> leadLine, List<Vector3> allFollowPoints)
    {
        List<Vector3> contactPts = new();
        List<float> distances = new();
        foreach (Vector3 vector3 in leadLine)
        {
            int closestIndex = -1;
            float closestDistance = float.MaxValue;
            foreach (Vector3 followPoint in allFollowPoints)
            {
                float distance = Vector3.Distance(vector3, followPoint);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestIndex = allFollowPoints.IndexOf(followPoint);
                }
            }

            if (Vector3.Distance(vector3, allFollowPoints[closestIndex]) < .4f)
            {
                contactPts.Add(Vector3.Lerp(vector3, allFollowPoints[closestIndex], 0.5f));
                distances.Add(Vector3.Distance(vector3, allFollowPoints[closestIndex]));
            }
        }

        return new Tuple<Vector3[], float[]>(CatmullRomSpline.Generate(contactPts.ToArray()), distances.ToArray());
    }

    static LineRenderer NewLineRenderer(float LW, Material mat)
    {
        GameObject parent = new("line rend");
        LineRenderer lineRenderer = parent.AddComponent<LineRenderer>();
        lineRenderer.material = mat;
        lineRenderer.startWidth = LW;
        lineRenderer.endWidth = LW;
        lineRenderer.loop = false;
        lineRenderer.useWorldSpace = false;

        return lineRenderer;
    }

    static Tuple<AnimationCurve, Gradient> CurveAnimationGrad(float[] distances)
    {
        Gradient civGrad = new();
        List<GradientColorKey> civColors = new();
        GradientAlphaKey[] alphas = { new(1, 0), new(1, 1) };

        AnimationCurve widthCurve = new();
        for (int i = 0; i < distances.Length; i++)
        {
            widthCurve.AddKey(i, Mathf.Max(.1f - distances[i], 0));

            civColors.Add(new GradientColorKey(ColorUtils.CividisColor(Mathf.Min(1, distances[i] * 8f)), i));
        }

        civGrad.SetKeys(
            civColors.ToArray(),
            alphas
        );

        return new Tuple<AnimationCurve, Gradient>(widthCurve, civGrad);
    }
}