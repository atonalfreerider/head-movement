using UnityEngine;

namespace Shapes
{
    public class Circle : Polygon
    {
        // the amount of straight lines per 1 meter of an arc; 
        public int sidePer1M = 16;

        // the current radius and percent completion of a circle or ring;
        public float curPrct;

        public float curR;

        public void Draw(float radius)
        {
            // Draws a full circle (100%) with no height (not a cylinder)
            DrawCirc(radius, 1, 0);
        }

        public void DrawCirc(float R, float prct, float h, float offset = 0f)
        {
            curPrct = prct;
            curR = R;

            // calculate the number of sides to this circle;
            // there is one less side because a full circle fills the last side;
            int side = Mathf.RoundToInt(Mathf.Pow(R + 1f, .8f) * sidePer1M * prct);
            if (side <= 0)
                return;

            // if an incomplete circle, draw one more side;
            if (prct < 1f)
                side += 1;

            // generate vertices and indices;
            Vector3[] skinList = new Vector3[side + 1];
            int[] indList = new int[side * 3];

            skinList[0] = Vector3.zero;
            float alpha = 360f / (side - (prct < 1f ? 1 : 0)); // a positive alpha is drawn clockwise;

            int count = 1;
            int[] insInd;
            for (int ii = side - 1; ii >= 0; ii--)
            {
                skinList[count] = VectArc(R, ii * alpha, prct, offset, 0, 0, 0);
                insInd = new[] { 0, count, count + 1 };
                insInd.CopyTo(indList, (count - 1) * 3);
                count++;
            }

            indList[^1] = 1;

            if (h < float.Epsilon)
            {
                // a circle with 0 depth - only draw two sides;
                Draw3DPoly(skinList, MirrorIndices(indList, 0));
            }
            else
            {
                Extrude(skinList, indList, h, true, true, 0f);
            }
        }

        public void DrawRing(float R1, float R2, float prct, float h, float spiralH = 0, bool diminishTail = false)
        {
            curR = (R1 + R2) * .5f;
            prct = Mathf.Abs(prct);
            curPrct = prct;
            float spirBit = 0;
            float diminishingR = 0;

            // calculate the number of sides to this ring;
            // there is one less side because a full ring fills the last side;
            int side = Mathf.RoundToInt(Mathf.Pow(R1 + 1f, .8f) * sidePer1M * prct);
            if (side <= 0)
                return;

            // if an incomplete ring, draw one more side;
            if (prct < 1f)
            {
                side += 1;
            }

            // generate vertices and indices;
            Vector3[] skinList = new Vector3[side * 2];
            int[] indList = new int[side * 6];
            int[] insInd;
            float alpha = 360f / (side - (prct < 1f ? 1 : 0)); // a positive alpha is drawn clockwise;

            for (int ii = 0; ii < side; ii++)
            {
                spirBit = -(ii) * spiralH / side;
                if (diminishTail)
                {
                    diminishingR = Mathf.Lerp(0, R1 - R2, (float)ii / side);
                }

                skinList[ii] = VectArc(R1 - diminishingR, ii * alpha, prct, 0, 0, spirBit, 0);
                skinList[side * 2 - ii - 1] = VectArc(R2, ii * alpha, prct, 0, 0, spirBit, 0);

                insInd = new[] { ii, side * 2 - ii - 2, side * 2 - ii - 1 };
                insInd.CopyTo(indList, ii * 6);

                insInd = new[] { ii, ii + 1, side * 2 - ii - 2 };
                insInd.CopyTo(indList, ii * 6 + 3);
            }

            if (prct >= 1f - float.Epsilon)
            {
                // if a complete ring - connect the last segment of the ring to the beginning segment;
                indList[^1] = side * 2 - 1;
                indList[^2] = 0;

                indList[^5] = side * 2 - 1;
            }
            else if (indList.Length > 6)
            {
                // remove the last segment of the incomplete ring;
                int[] truncList = new int[indList.Length - 3];
                for (int zz = 0; zz < truncList.Length; zz++)
                    truncList[zz] = indList[zz];

                indList = truncList;
            }

            if (h < float.Epsilon)
            {
                // a ring with 0 depth - only draw two sides;
                Draw3DPoly(skinList, MirrorIndices(indList, 0));
            }
            else
            {
                Extrude(skinList, indList, h, false, true, 0f);
            }
        }

        static Vector3 VectArc(float R, float alpha, float prct, float offSet, float cX, float cY, float cZ)
        {
            return new Vector3(
                R * Mathf.Sin(
                    alpha * prct * Mathf.PI / 180f +
                    offSet * 2f * Mathf.PI) +
                cX,
                cY,
                R * Mathf.Cos(
                    alpha * prct * Mathf.PI / 180f +
                    offSet * 2f * Mathf.PI) +
                cZ
            );
        }

        public static class NewCylinder
        {
            public static Circle cylinder;
            public static Circle circle;

            public static void Init(PolygonFactory polygonFactory)
            {
                Color shapeColor = Color.white;

                cylinder = PolygonFactory.NewCirclePoly(polygonFactory.mainMat);
                cylinder.sidePer1M = 8;
                cylinder.DrawRing(1, .85f, 1, .3f);
                cylinder.name = "cylinder";
                cylinder.SetColor(shapeColor);
                cylinder.transform.SetParent(polygonFactory.transform, false);
                cylinder.gameObject.SetActive(false);

                circle = PolygonFactory.NewCirclePoly(polygonFactory.mainMat);
                circle.name = "RootDot";
                circle.DrawCirc(.035f * .5f, 1, 0);
                circle.SetColor(shapeColor);
                circle.transform.SetParent(polygonFactory.transform, false);
                circle.gameObject.SetActive(false);
            }
        }
    }
}