using System;
using System.Collections.Generic;
using Shapes;
using UnityEngine;

public class FootScorpion : MonoBehaviour
{
    Polygon groundTri;

    readonly List<Polygon> pastTrail = new();
    readonly List<Polygon> futureTrail = new();

    const int TrailWindow = 15;

    void Awake()
    {
        groundTri = Instantiate(PolygonFactory.Instance.tri);
        groundTri.gameObject.SetActive(true);
        groundTri.transform.SetParent(transform, false);
        groundTri.transform.localScale = new Vector3(.045f, .01f, .1f);
        groundTri.SetColor(Cividis.CividisColor(0));

        for (int i = 0; i < TrailWindow; i++)
        {
            Polygon pastTrailer = Instantiate(PolygonFactory.Instance.icosahedron0);
            pastTrailer.gameObject.SetActive(true);
            pastTrailer.transform.SetParent(transform, false);
            pastTrailer.transform.localScale = Vector3.one * .02f;
            pastTrailer.SetColor(Cividis.CividisColor(.7f));
            pastTrail.Add(pastTrailer);

            Polygon futureTrailer = Instantiate(PolygonFactory.Instance.icosahedron0);
            futureTrailer.gameObject.SetActive(true);
            futureTrailer.transform.SetParent(transform, false);
            futureTrailer.transform.localScale = Vector3.one * .007f;
            futureTrailer.SetColor(Cividis.CividisColor(.85f));
            futureTrail.Add(futureTrailer);
        }
    }

    public void SetGroundTriState(bool show)
    {
        groundTri.gameObject.SetActive(show);
    }

    public void SyncToAnkleAndKnee(Vector3 ankle, Vector3 knee, Vector3 hip)
    {
        groundTri.transform.position = new Vector3(ankle.x, 0, ankle.z);
        // point the triangle in the forward direction of the knee relative to the ground
        groundTri.transform.rotation = Quaternion.LookRotation(GetNormalizedDirection(ankle, knee, hip), Vector3.up);
    }

    public void SetPastAndFuture(List<Vector3> futureAnklePositions, List<Vector3> pastAnklePositions,
        Vector3 currentAnklePosition)
    {
        for (int i = 0; i < TrailWindow; i++)
        {
            if (i >= futureAnklePositions.Count)
            {
                futureTrail[i].gameObject.SetActive(false);
            }
            else
            {
                futureTrail[i].gameObject.SetActive(false); // keep the future switched off for now
                futureTrail[i].transform.position = futureAnklePositions[i];
            }
        }

        for (int i = 0; i < TrailWindow; i++)
        {
            if (i >= pastAnklePositions.Count)
            {
                pastTrail[i].gameObject.SetActive(false);
            }
            else
            {
                float distanceAmplifier;
                if (i == 0)
                {
                    distanceAmplifier = Vector3.Distance(currentAnklePosition, pastAnklePositions[i]);
                }
                else
                {
                    distanceAmplifier = Vector3.Distance(pastAnklePositions[i - 1], pastAnklePositions[i]);
                    distanceAmplifier /= (i * .2f + 1); // shrink even more with time
                }

                Polygon pastTrailer = pastTrail[i];
                pastTrailer.gameObject.SetActive(true);
                pastTrailer.transform.localScale = Vector3.one * .2f * distanceAmplifier;
                pastTrailer.SetColor(Cividis.CividisColor(1 - Math.Min(1f, distanceAmplifier * 10)));
                pastTrailer.transform.position = pastAnklePositions[i];
            }
        }
    }

    static Vector3 GetNormalizedDirection(Vector3 vec1, Vector3 vec2, Vector3 vec3)
    {
        // Find the direction vector from vec1 to vec3
        Vector3 direction = vec3 - vec1;

        // Find a point on the line between vec1 and vec3 that is coplanar with vec2
        // The coplanar condition requires that the y-coordinate of the point matches vec2's y-coordinate
        float t = (vec2.y - vec1.y) / direction.y;
        Vector3 coplanarPoint = vec1 + t * direction;

        // Calculate the vector from this coplanar point to vec2
        Vector3 resultVector = vec2 - coplanarPoint;

        // Normalize and return this vector
        return resultVector.normalized;
    }
}