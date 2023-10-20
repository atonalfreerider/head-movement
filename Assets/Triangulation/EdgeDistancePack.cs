using Shapes;

namespace Triangulation
{
    public class EdgeDistancePack
    {
        public readonly Line2D edge;
        public readonly float distance;

        /// <summary>
        /// Constructor of the edge distance pack class used to create a new edge distance pack instance from a 2D edge
        /// and a scalar value describing a distance.
        /// </summary>
        /// <param name="edge">The edge</param>
        /// <param name="distance">The distance of the edge to some point</param>
        public EdgeDistancePack(Line2D edge, float distance)
        {
            this.edge = edge;
            this.distance = distance;
        }
    }
}