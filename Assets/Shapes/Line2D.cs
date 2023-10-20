using UnityEngine;

namespace Shapes
{
    public class Line2D
    {
        readonly Vector2 start;
        readonly Vector2 end;

        public Line2D(Vector2 start, Vector2 end)
        {
            this.start = start;
            this.end = end;
        }

        public Vector2 getStart() => start;
        public Vector2 getEnd() => end;
         
        public bool Intersects(Line2D other)
        {
            // Find the four orientations needed for general and special cases 
            int o1 = orientation(start, end, other.start);
            int o2 = orientation(start, end, other.end);
            int o3 = orientation(other.start, other.end, start);
            int o4 = orientation(other.start, other.end, end);
             
            if (o1 != o2 && o3 != o4)
            {
                // General case for intersection
                return true;
            }
            
            return false;

            // Special Cases (not performed) 
            // p1, q1 and p2 are colinear and p2 lies on segment p1q1 
            /*
            if (o1 == 0 && onSegment(start, other.start, end)) return true;

            // p1, q1 and q2 are colinear and q2 lies on segment p1q1 
            if (o2 == 0 && onSegment(start, other.end, end)) return true;

            // p2, q2 and p1 are colinear and p1 lies on segment p2q2 
            if (o3 == 0 && onSegment(other.start, start, other.end)) return true;

            // p2, q2 and q1 are colinear and q1 lies on segment p2q2 
            if (o4 == 0 && onSegment(other.start, end, other.end)) return true;
            */
        }

        /// <summary>
        /// To find orientation of ordered triplet (p, q, r). 
        /// The function returns following values 
        /// 0 --> p, q and r are colinear 
        /// 1 --> Clockwise 
        /// 2 --> Counterclockwise
        /// </summary>
        /// <param name="p"></param>
        /// <param name="q"></param>
        /// <param name="r"></param>
        /// <returns></returns>
        static int orientation(Vector2 p, Vector2 q, Vector2 r)
        {
            // See https://www.geeksforgeeks.org/orientation-3-ordered-points/ 
            // for details of below formula. 
            float val = (q.y - p.y) * (r.x - q.x) -
                        (q.x - p.x) * (r.y - q.y);

            if (Mathf.Abs(val) <= float.Epsilon) return 0; // colinear 

            return val > 0 ? 1 : 2; // clock or counterclockwise 
        }

        /// <summary>
        /// Given three colinear points p, q, r, the function checks if point q lies on line segment 'pr' 
        /// </summary>
        /// <param name="p"></param>
        /// <param name="q"></param>
        /// <param name="r"></param>
        /// <returns></returns>
        static bool onSegment(Vector2 p, Vector2 q, Vector2 r)
        {
            if (q.x <= Mathf.Max(p.x, r.x) && q.x >= Mathf.Min(p.x, r.x) &&
                q.y <= Mathf.Max(p.y, r.y) && q.y >= Mathf.Min(p.y, r.y))
                return true;

            return false;
        }
    }
}