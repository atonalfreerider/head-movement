using System.Linq;
using UnityEngine;

namespace Shapes.Lines
{
    public class Line : Polygon
    {
        public void DrawLine(Vector3[] points, float LW, bool closed, int n)
        {
            int skinLen = (points.Length - 1) * n * 2;
            int indLen = (points.Length - 1) * 3 * n + (points.Length - 2) * 3;
            if (closed)
            {
                skinLen += 2 * n;
                indLen += 6 * n;
            }

            Vector3[] skinList = new Vector3[skinLen];
            int[] indList = new int[indLen];

            int startInd = closed ? 0 : 1;
            Vector3 fwdPt = GetPrev(startInd, points);
            int prevIndices = 0;
            int modder = 0;
            for (int ii = startInd; ii < points.Length; ii++)
            {
                Vector3 prevPt = GetPrev(ii, points);
                Vector3 currPt = points[ii];
                Vector3 nextPt = GetNext(ii, points);

                // determine offset based on angle at joint;
                Vector3 vec1 = currPt - prevPt;
                Vector3 vec2 = nextPt - currPt;
                float alpha = Vector3.Angle(vec1, vec2) * .5f;
                float x = LW * .5f * Mathf.Atan(alpha * Mathf.PI / 180f);
                float d = Vector3.Distance(prevPt, currPt);
                Vector3 backPt = Vector3.Lerp(prevPt, currPt, 1f - x / d);

                // add Line  
                LineArray(fwdPt, backPt, LW, n)
                    .CopyTo(skinList, (ii - startInd) * 4);

                // set forward for next iteration;
                fwdPt = Vector3.Lerp(
                    currPt, 
                    nextPt, 
                    x / Vector3.Distance(currPt, nextPt));

                // Tri 1
                int[] insInd = {0 + prevIndices, 2 + prevIndices, 3 + prevIndices};
                insInd.CopyTo(indList, (ii - startInd) * 9 + modder);

                // Tri 2
                insInd = new[] {0 + prevIndices, 3 + prevIndices, 1 + prevIndices};
                insInd.CopyTo(indList, (ii - startInd) * 9 + 3 + modder);

                // Joint
                if (prevIndices >= 4)
                {
                    insInd = alpha <= 0
                        ? new[] {0 + prevIndices, -2 + prevIndices, 1 + prevIndices}
                        : new[] {0 + prevIndices, 1 + prevIndices, -1 + prevIndices};
                    insInd.CopyTo(indList, (ii - startInd) * 9 + 6 + modder);
                }

                prevIndices += 4;
                modder = -3;
            }

            if (indList.Length >= 6 && skinList.Length >= 4)
            {
                Draw3DPoly(skinList, MirrorIndices(indList, 0));
            }
        }

        public static Vector3[] BezierCurve(Vector3[] points)
        {
            float linearD = 0;
            for (int ii = 0; ii < points.Length - 1; ii++)
            {
                linearD += Vector3.Distance(points[ii], points[ii + 1]);
            }

            int numPts = System.Convert.ToInt32(.3f * linearD / .01f);
            Vector3[] curvePts = new Vector3[numPts + 1];
            for (int ii = 0; ii <= numPts; ii++)
            {
                curvePts[ii] = BezierPt(points, ii / (float) numPts);
            }

            return curvePts;
        }

        static Vector3[] LineArray(Vector3 ptA, Vector3 ptB, float LW, int n)
        {
            Vector3[] points = new Vector3[n * 2];
            Vector3 addPt;
            Vector3 normal = ptB - ptA;
            Vector3.Normalize(normal);
            Vector3 cross = Vector3.up; //.Cross(ptA, ptB);

            float alpha = 360f / n;

            for (int ii = 0; ii < n; ii++)
            {
                addPt = Vector3.Normalize(Quaternion.AngleAxis(ii * alpha - 90, cross) * normal);
                addPt *= LW * .5f;
                addPt += ptA;
                points[ii] = addPt;
            }

            for (int ii = 0; ii < n; ii++)
            {
                addPt = Vector3.Normalize(Quaternion.AngleAxis(ii * alpha - 90, cross) * normal);
                addPt *= LW * .5f;
                addPt += ptB;
                points[ii + n] = addPt;
            }

            return points;
        }

        static Vector3 GetPrev(int ind, Vector3[] passV)
        {
            return ind == 0 ? passV.Last() : passV[ind - 1];
        }

        static Vector3 GetNext(int ind, Vector3[] passV)
        {
            return ind == passV.Length - 1 ? passV.First() : passV[ind + 1];
        }

        static Vector3 BezierPt(Vector3[] points, float t)
        {
            while (true)
            {
                if (points.Length == 1) return points.First();
                Vector3[] lerps = new Vector3[points.Length - 1];
                for (int ii = 0; ii < lerps.Length; ii++)
                {
                    lerps[ii] = Vector3.Lerp(points[ii], points[ii + 1], t);
                }

                points = lerps;
            }
        }
    }
}