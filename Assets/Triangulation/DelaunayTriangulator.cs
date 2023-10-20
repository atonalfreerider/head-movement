using System;
using System.Collections.Generic;
using System.Linq;
using Shapes;
using UnityEngine;

namespace Triangulation
{
    /// <summary>
    /// The C# implementation of a Java implementation of an incremental 2D Delaunay triangulation algorithm written by
    /// Johannes Diemke: https://github.com/jdiemke/delaunay-triangulator
    /// </summary>
    public class DelaunayTriangulator
    {
        readonly List<Vector2> pointSet;
        TriangleSoup triangleSoup;

        /// <summary>
        /// Constructor of the SimpleDelaunayTriangulator class used to create a new triangulator instance.
        /// </summary>
        /// <param name="pointSet">The point set to be triangulated</param>
        public DelaunayTriangulator(List<Vector2> pointSet)
        {
            this.pointSet = pointSet;
            triangleSoup = new TriangleSoup();
        }

        /// <summary>
        /// This method generates a Delaunay triangulation from the specified point set.
        /// </summary>
        public bool Triangulate()
        {
            triangleSoup = new TriangleSoup();

            if (pointSet == null || pointSet.Count < 3)
            {
                return true;
            }

            // In order for the in circumcircle test to not consider the vertices of the super triangle we have to start
            // out with a big triangle containing the whole point set. We have to scale the super triangle to be very
            // large. Otherwise the triangulation is not convex.

            float maxOfAnyCoordinate = GetPointSet()
                .Select(vector => Math.Max(vector.x, vector.y))
                .Concat(new[] {0f}).Max();

            maxOfAnyCoordinate *= 16;

            Vector2 p1 = new(0, 3 * maxOfAnyCoordinate);
            Vector2 p2 = new(3 * maxOfAnyCoordinate, 0);
            Vector2 p3 = new(-3 * maxOfAnyCoordinate, -3 * maxOfAnyCoordinate);

            Triangle2D superTriangle = new(p1, p2, p3);

            triangleSoup.Add(superTriangle);

            foreach (Vector2 point in pointSet)
            {
                Triangle2D triangle = triangleSoup.FindContainingTriangle(point);

                if (triangle == null)
                {
                    // If no containing triangle exists, then the vertex is not inside a triangle (this can also happen
                    // due to numerical errors) and lies on an edge. In order to find this edge we search all edges of
                    // the triangle soup and select the one which is nearest to the point we try to add. This edge is
                    // removed and four new edges are added.

                    Line2D edge = triangleSoup.FindNearestEdge(point);

                    Triangle2D first = triangleSoup.FindOneTriangleSharing(edge);
                    Triangle2D second = triangleSoup.FindNeighbour(first, edge);

                    if (second == null)
                    {
                        // This indicates the point is not only on the edge of a triangle, but on the edge of the entire
                        // convex hull.
                        //
                        // One option might be to break the first triangle up into two pieces using the new point. Until
                        // that's implemented, just stop the triangulation completely.
                        //
                        // It's a hack to use `NotEnoughPointsException` here, but it's the simplest way to not modify
                        // this library too much from the original source.
                        
                        // return false to signify a failed analysis -> this prevents infinite loops
                        return false;
                    }

                    Vector2 firstNoneEdgeVertex = first.GetNoneEdgeVertex(edge);
                    Vector2 secondNoneEdgeVertex = second.GetNoneEdgeVertex(edge);

                    triangleSoup.Remove(first);
                    triangleSoup.Remove(second);

                    Triangle2D triangle1 = new(edge.getStart(), firstNoneEdgeVertex, point);
                    Triangle2D triangle2 = new(edge.getEnd(), firstNoneEdgeVertex, point);
                    Triangle2D triangle3 = new(edge.getStart(), secondNoneEdgeVertex, point);
                    Triangle2D triangle4 = new(edge.getEnd(), secondNoneEdgeVertex, point);

                    triangleSoup.Add(triangle1);
                    triangleSoup.Add(triangle2);
                    triangleSoup.Add(triangle3);
                    triangleSoup.Add(triangle4);

                    LegalizeEdge(triangle1, new Line2D(edge.getStart(), firstNoneEdgeVertex), point);
                    LegalizeEdge(triangle2, new Line2D(edge.getEnd(), firstNoneEdgeVertex), point);
                    LegalizeEdge(triangle3, new Line2D(edge.getStart(), secondNoneEdgeVertex), point);
                    LegalizeEdge(triangle4, new Line2D(edge.getEnd(), secondNoneEdgeVertex), point);
                }
                else
                {
                    // The vertex is inside a triangle.
                    Vector2 a = triangle.a;
                    Vector2 b = triangle.b;
                    Vector2 c = triangle.c;

                    triangleSoup.Remove(triangle);

                    Triangle2D first = new(a, b, point);
                    Triangle2D second = new(b, c, point);
                    Triangle2D third = new(c, a, point);

                    triangleSoup.Add(first);
                    triangleSoup.Add(second);
                    triangleSoup.Add(third);

                    LegalizeEdge(first, new Line2D(a, b), point);
                    LegalizeEdge(second, new Line2D(b, c), point);
                    LegalizeEdge(third, new Line2D(c, a), point);
                }
            }

            // Remove all triangles that contain vertices of the super triangle.
            triangleSoup.RemoveTrianglesUsing(superTriangle.a);
            triangleSoup.RemoveTrianglesUsing(superTriangle.b);
            triangleSoup.RemoveTrianglesUsing(superTriangle.c);

            return true;
        }

        /// <summary>
        /// This method legalizes edges by recursively flipping all illegal edges.
        /// </summary>
        /// <param name="triangle">The triangle</param>
        /// <param name="edge">The edge to be legalized</param>
        /// <param name="newVertex">The new vertex</param>
        void LegalizeEdge(Triangle2D triangle, Line2D edge, Vector2 newVertex)
        {
            Triangle2D neighbourTriangle = triangleSoup.FindNeighbour(triangle, edge);

            // If the triangle has a neighbor, then legalize the edge
            if (neighbourTriangle != null && neighbourTriangle.IsPointInCircumcircle(newVertex))
            {
                triangleSoup.Remove(triangle);
                triangleSoup.Remove(neighbourTriangle);

                Vector2 noneEdgeVertex = neighbourTriangle.GetNoneEdgeVertex(edge);

                Triangle2D firstTriangle = new(noneEdgeVertex, edge.getStart(), newVertex);
                Triangle2D secondTriangle = new(noneEdgeVertex, edge.getEnd(), newVertex);

                triangleSoup.Add(firstTriangle);
                triangleSoup.Add(secondTriangle);

                LegalizeEdge(firstTriangle, new Line2D(noneEdgeVertex, edge.getStart()), newVertex);
                LegalizeEdge(secondTriangle, new Line2D(noneEdgeVertex, edge.getEnd()), newVertex);
            }
        }

        /// <summary>
        /// Returns the point set in form of a vector of 2D vectors.
        /// </summary>
        /// <returns>Returns the points set.</returns>
        IEnumerable<Vector2> GetPointSet()
        {
            return pointSet;
        }

        /// <summary>
        /// Returns the triangles of the triangulation in form of a vector of 2D triangles.
        /// </summary>
        /// <returns>Returns the triangles of the triangulation.</returns>
        public List<Triangle2D> GetTriangles()
        {
            return triangleSoup.GetTriangles();
        }
    }
}