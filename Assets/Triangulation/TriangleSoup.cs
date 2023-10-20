using System.Collections.Generic;
using System.Linq;
using Shapes;
using UnityEngine;

namespace Triangulation
{
    public class TriangleSoup
    {
        readonly List<Triangle2D> triangleSoup;

        /// <summary>
        /// Constructor of the triangle soup class used to create a new triangle soup instance.
        /// </summary>
        public TriangleSoup()
        {
            triangleSoup = new List<Triangle2D>();
        }

        /// <summary>
        /// Adds a triangle to this triangle soup.
        /// </summary>
        /// <param name="triangle">The triangle to be added to this triangle soup</param>
        public void Add(Triangle2D triangle)
        {
            triangleSoup.Add(triangle);
        }

        /// <summary>
        /// Removes a triangle from this triangle soup.
        /// </summary>
        /// <param name="triangle">The triangle to be removed from this triangle soup</param>
        public void Remove(Triangle2D triangle)
        {
            triangleSoup.Remove(triangle);
        }

        /// <summary>
        /// Returns the triangles from this triangle soup.
        /// </summary>
        /// <returns>The triangles from this triangle soup</returns>
        public List<Triangle2D> GetTriangles()
        {
            return triangleSoup;
        }

        /// <summary>
        /// Returns the triangle from this triangle soup that contains the specified point or null if no triangle from
        /// the triangle soup contains the point.
        /// </summary>
        /// <param name="point">The point</param>
        /// <returns>Returns the triangle from this triangle soup that contains the specified point or null</returns>
        public Triangle2D FindContainingTriangle(Vector2 point)
        {
            return triangleSoup.FirstOrDefault(triangle => triangle.Contains(point));
        }

        /// <summary>
        /// Returns the neighbor triangle of the specified triangle sharing the same edge as specified. If no neighbor
        /// sharing the same edge exists null is returned.
        /// </summary>
        /// <param name="triangle">The triangle</param>
        /// <param name="edge">The edge</param>
        /// <returns>The triangles neighbor triangle sharing the same edge or null if no triangle exists</returns>
        public Triangle2D FindNeighbour(Triangle2D triangle, Line2D edge)
        {
            return triangleSoup.FirstOrDefault(triangleFromSoup =>
                triangleFromSoup.IsNeighbour(edge) &&
                triangleFromSoup != triangle);
        }

        /// <summary>
        /// Returns one of the possible triangles sharing the specified edge. Based on the ordering of the triangles in
        /// this triangle soup the returned triangle may differ. To find the other triangle that shares this edge use
        /// the method.
        /// </summary>
        /// <param name="edge">The edge</param>
        /// <returns>Returns one triangle that shares the specified edge</returns>
        public Triangle2D FindOneTriangleSharing(Line2D edge)
        {
            return triangleSoup.FirstOrDefault(triangle => triangle.IsNeighbour(edge));
        }

        /// <summary>
        /// Returns the edge from the triangle soup nearest to the specified point.
        /// </summary>
        /// <param name="point">The point</param>
        /// <returns>The edge from the triangle soup nearest to the specified point</returns>
        public Line2D FindNearestEdge(Vector2 point)
        {
            List<EdgeDistancePack> edgeList = triangleSoup.Select(
                triangle => triangle.FindNearestEdge(point)).ToList();

            EdgeDistancePack[] edgeDistancePacks = edgeList.OrderBy(
                edge => edge.distance).ToArray();

            return edgeDistancePacks.First().edge;
        }

        /// <summary>
        /// Removes all triangles from this triangle soup that contain the specified vertex.
        /// </summary>
        /// <param name="vertex">The vertex</param>
        public void RemoveTrianglesUsing(Vector2 vertex)
        {
            List<Triangle2D> trianglesToBeRemoved = triangleSoup.Where(
                triangle => triangle.HasVertex(vertex)).ToList();

            foreach (Triangle2D triangleToBeRemoved in trianglesToBeRemoved)
            {
                triangleSoup.Remove(triangleToBeRemoved);
            }
        }
    }
}