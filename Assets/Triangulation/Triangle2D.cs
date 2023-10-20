using System;
using Shapes;
using UnityEngine;

namespace Triangulation
{
    public class Triangle2D
    {
        public readonly Vector2 a;
        public readonly Vector2 b;
        public readonly Vector2 c;

        /// <summary>
        /// Constructor of the 2D triangle class used to create a new triangle instance from three 2D vectors describing
        /// the triangle's vertices.
        /// </summary>
        /// <param name="a">The first vertex of the triangle</param>
        /// <param name="b">The second vertex of the triangle</param>
        /// <param name="c">The third vertex of the triangle</param>
        public Triangle2D(Vector2 a, Vector2 b, Vector2 c)
        {
            this.a = a;
            this.b = b;
            this.c = c;
        }

        /// <summary>
        /// Tests if a 2D point lies inside this 2D triangle. See Real-Time Collision Detection, chap. 5, p. 206. 
        /// </summary>
        /// <param name="point">point The point to be tested</param>
        /// <returns>Returns true iff the point lies inside this 2D triangle</returns>
        public bool Contains(Vector2 point)
        {
            float pab = CrossProduct(point - a, b - a);
            float pbc = CrossProduct(point - b, c - b);

            if (!HasSameSign(pab, pbc))
            {
                return false;
            }

            float pca = CrossProduct(point - c, a - c);

            return HasSameSign(pab, pca);
        }

        /// <summary>
        /// Tests if a given point lies in the circumcircle of this triangle. Let the triangle ABC appear in
        /// counterclockwise (CCW) order. Then when det &gt; 0, the point lies inside the circumcircle through the three
        /// points a, b and c. If instead det &lt; 0, the point lies outside the circumcircle. When det = 0, the four
        /// points are cocircular. If the triangle is oriented clockwise (CW) the result is reversed. See Real-Time
        /// Collision Detection, chap. 3, p. 34.
        /// </summary>
        /// <param name="point">The point to be tested</param>
        /// <returns>Returns true iff the point lies inside the circumcircle through the three points a, b, and c of the
        /// triangle</returns>
        public bool IsPointInCircumcircle(Vector2 point)
        {
            float a11 = a.x - point.x;
            float a21 = b.x - point.x;
            float a31 = c.x - point.x;

            float a12 = a.y - point.y;
            float a22 = b.y - point.y;
            float a32 = c.y - point.y;

            float a13 = (a.x - point.x) * (a.x - point.x) +
                        (a.y - point.y) * (a.y - point.y);
            float a23 = (b.x - point.x) * (b.x - point.x) +
                        (b.y - point.y) * (b.y - point.y);
            float a33 = (c.x - point.x) * (c.x - point.x) +
                        (c.y - point.y) * (c.y - point.y);

            float det = a11 * a22 * a33 + 
                        a12 * a23 * a31 + 
                        a13 * a21 * a32 - 
                        a13 * a22 * a31 - 
                        a12 * a21 * a33 -
                        a11 * a23 * a32;

            if (IsOrientedCCW())
            {
                return det > 0;
            }

            return det < 0;
        }

        /// <summary>
        /// Test if this triangle is oriented counterclockwise (CCW). Let A, B and C be three 2D points. If det &gt; 0,
        /// C lies to the left of the directed line AB. Equivalently the triangle ABC is oriented counterclockwise. When
        /// det &lt; 0, C lies to the right of the directed line AB, and the triangle ABC is oriented clockwise. When
        /// det = 0, the three points are colinear. See Real-Time Collision Detection, chap. 3, p. 32
        /// </summary>
        /// <returns>Returns true iff the triangle ABC is oriented counterclockwise (CCW)</returns>
        bool IsOrientedCCW()
        {
            float a11 = a.x - c.x;
            float a21 = b.x - c.x;

            float a12 = a.y - c.y;
            float a22 = b.y - c.y;

            float det = a11 * a22 - a12 * a21;

            return det > 0;
        }

        /// <summary>
        /// Returns true if this triangle contains the given edge.
        /// </summary>
        /// <param name="edge">The edge to be tested</param>
        /// <returns>Returns true if this triangle contains the edge</returns>
        public bool IsNeighbour(Line2D edge)
        {
            return (a == edge.getStart() || b == edge.getStart() || c == edge.getStart()) &&
                   (a == edge.getEnd() || b == edge.getEnd() || c == edge.getEnd());
        }

        /// <summary>
        /// Returns the vertex of this triangle that is not part of the given edge.
        /// </summary>
        /// <param name="edge">The edge</param>
        /// <returns>The vertex of this triangle that is not part of the edge</returns>
        public Vector2 GetNoneEdgeVertex(Line2D edge)
        {
            if (a != edge.getStart() && a != edge.getEnd())
            {
                return a;
            }

            if (b != edge.getStart() && b != edge.getEnd())
            {
                return b;
            }

            if (c != edge.getStart() && c != edge.getEnd())
            {
                return c;
            }

            return Vector2.zero;
        }

        /// <summary>
        /// Returns true if the given vertex is one of the vertices describing this triangle.
        /// </summary>
        /// <param name="vertex">The vertex to be tested</param>
        /// <returns>Returns true if the Vertex is one of the vertices describing this triangle</returns>
        public bool HasVertex(Vector2 vertex)
        {
            return a == vertex || b == vertex || c == vertex;
        }

        /// <summary>
        /// Returns an EdgeDistancePack containing the edge and its distance nearest to the specified point.
        /// </summary>
        /// <param name="point">The point the nearest edge is queried for</param>
        /// <returns>The edge of this triangle that is nearest to the specified point</returns>
        public EdgeDistancePack FindNearestEdge(Vector2 point)
        {
            float distance0 = (ComputeClosestPoint(new Line2D(a, b), point) - point).magnitude;
            float distance1 = (ComputeClosestPoint(new Line2D(b, c), point) - point).magnitude;
            float distance2 = (ComputeClosestPoint(new Line2D(c, a), point) - point).magnitude;

            if (distance0 < distance1 && distance0 < distance2)
            {
                return new EdgeDistancePack(new Line2D(a, b), distance0);
            }

            if (distance1 < distance0 && distance1 < distance2)
            {
                return new EdgeDistancePack(new Line2D(b, c), distance1);
            }

            return new EdgeDistancePack(new Line2D(c, a), distance2);
        }

        /// <summary>
        /// Computes the closest point on the given edge to the specified point.
        /// </summary>
        /// <param name="edge">The edge on which we search the closest point to the specified point</param>
        /// <param name="point">The point to which we search the closest point on the edge</param>
        /// <returns>The closest point on the given edge to the specified point</returns>
        static Vector2 ComputeClosestPoint(Line2D edge, Vector2 point)
        {
            Vector2 ab = edge.getEnd() - edge.getStart();
            float t = Vector2.Dot(point - edge.getStart(),ab) / Vector2.Dot(ab,ab);

            if (t < 0)
            {
                t = 0;
            }
            else if (t > 1)
            {
                t = 1;
            }

            Vector2 r = ab * t;
            return edge.getStart() + r;
        }

        /// <summary>
        /// Tests if the two arguments have the same sign.
        /// </summary>
        /// <param name="a">The first floating point argument</param>
        /// <param name="b">The second floating point argument</param>
        /// <returns>Returns true iff both arguments have the same sign</returns>
        static bool HasSameSign(float a, float b)
        {
            return Math.Sign(a) == Math.Sign(b);
        }

        public bool Intersects(Triangle2D other)
        {
            /* if any edge in this triangle separates a point from the other three points in the other triangle, then
             they do not intersect
    
             https://stackoverflow.com/questions/2778240/detection-of-triangle-collision-in-2d-space
    
                 D---E
                  \  |
             A---B \ |
             |  /   \|
             | /     F
             |/
             C
    
            In this example, the line DF separates point E from points A,B,C - proving that the triangles don't
             intersect
    
            */

            return !EdgeSeparates(a, b, c, other) &&
                   !EdgeSeparates(b, c, a, other) && 
                   !EdgeSeparates(c, a, b, other);
        }

        public bool DistanceCloserThan(float d, Triangle2D other)
        {
            return ClosestDistance(other) < d;
        }

        float ClosestDistance(Triangle2D other)
        {
            return Mathf.Min(
                Vector2.Distance(a, other.a),
                Vector2.Distance(a, other.b),
                Vector2.Distance(a, other.c),
                Vector2.Distance(b, other.a),
                Vector2.Distance(b, other.b),
                Vector2.Distance(b, other.c),
                Vector2.Distance(c, other.a),
                Vector2.Distance(c, other.b),
                Vector2.Distance(c, other.c)
            );
        }

        static bool EdgeSeparates(Vector2 startPoint, Vector2 endPoint, Vector2 thirdPoint, Triangle2D other)
        {
            Vector2 separatingEdge = endPoint - startPoint;

            // calculate the sign of the cross product of the vector to the third point in this triangle
            Vector2 comparisonVector = thirdPoint - startPoint;
            int compareSign = -Math.Sign(CrossProduct(comparisonVector, separatingEdge));

            // calculate the vectors from this triangle's starting point to the three points in the other triangle
            Vector2 startA = other.a - startPoint;
            Vector2 startB = other.b - startPoint;
            Vector2 startC = other.c - startPoint;

            // compare the sign of the cross products to the above cross product if all are of the opposite sign, the
            // triangles have been proven to not intersect
            return compareSign == Math.Sign(CrossProduct(startA,separatingEdge)) &&
                   compareSign == Math.Sign(CrossProduct(startB,separatingEdge)) &&
                   compareSign == Math.Sign(CrossProduct(startC,separatingEdge));
        }

        static float CrossProduct(Vector2 vectorA, Vector2 vectorB)
        {
            return vectorA.y * vectorB.x - vectorA.x * vectorB.y;
        }
    }
}