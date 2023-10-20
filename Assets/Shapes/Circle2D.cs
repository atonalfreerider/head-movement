using System;
using UnityEngine;

namespace Shapes
{
    public class Circle2D
    {
        readonly Vector2 center;
        readonly float r;

        public Circle2D(Vector2 center, float r)
        {
            this.center = center;
            this.r = r;
        }

        /// <summary>
        /// https://stackoverflow.com/questions/1073336/circle-line-segment-collision-detection-algorithm
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool LineIntersects(Line2D other)
        {
            Vector2 d = other.getEnd() - other.getStart();
            Vector2 f = other.getStart() - center;
            float a = Vector2.Dot(d, d);
            float b = 2 * Vector2.Dot(f, d);
            float c = Vector2.Dot(f, f) - r * r;

            float discriminant = b * b - 4 * a * c;

            if (discriminant >= 0)
            {
                // ray didn't totally miss sphere, so there is a solution to the equation.

                discriminant = Mathf.Sqrt(discriminant);

                // either solution may be on or off the ray so need to test both t1 is always the smaller value, because
                // BOTH discriminant and a are nonnegative.
                float t1 = (-b - discriminant) / (2 * a);
                float t2 = (-b + discriminant) / (2 * a);

                // 4x HIT cases:
                //          -o->             --|-->  |            |  --|->                       | -> |
                // Impale(t1 hit,t2 hit), Poke(t1 hit,t2>1), ExitWound(t1<0, t2 hit), CompletelyInside(t1<0, t2>1)

                // 2x MISS cases:
                //       ->  o                     o ->             
                // FallShort (t1>1,t2>1), Past (t1<0,t2<0)

                if (t1 is >= 0 and <= 1)
                {
                    // t1 is the intersection, and it's closer than t2 (since t1 uses -b - discriminant)
                    // Impale, Poke
                    return true;
                }

                // here t1 didn't intersect so we are either started inside the sphere or completely past it
                if (t2 is >= 0 and <= 1)
                {
                    // ExitWound
                    return true;
                }

                if (t1 < 0 && t2 > 1)
                {
                    // completely inside
                    return true;
                }
            }

            return false;
        }

        public bool CircleIntersects(Circle2D other)
        {
            float distance = Vector2.Distance(center, other.center);
            return distance < r + other.r;
        }
    }

    public class Sphere
    {
        readonly Vector3 c;
        readonly float r;
        public Sphere(Vector3 center, float radius)
        {
            c = center;
            r = radius;
        }

        /// <summary>
        /// https://stackoverflow.com/questions/5883169/intersection-between-a-line-and-a-sphere
        /// 
        ///                             ooo OOO OOO ooo
        ///                         oOO                 OOo
        ///                     oOO   \                     OOo
        ///                  oOO       \                       OOo
        ///                oOO          \                        OOo
        ///              oOO             \                         OOo
        ///             oOO               r                         OOo
        ///            oOO                 \                         OOo
        ///           oOO                   \                         OOo
        ///           oOO                    \                        OOo
        ///           oOO                    (c)                      OOo
        ///           oOO               *     |                       OOo
        ///           oOO        *            |                       OOo
        ///            oOO *                  |                      OOo
        ///         *   oOO                   |                     OOo
        /// (ray)--->    (s1)-------------------------------------(s2)  
        ///                oOO                                   OOo
        ///                  oO                                OOo
        ///                     oOO                         OOo
        ///                         oOO                 OOo
        ///                             ooo OOO OOO ooo
        ///        
        ///                           after -Byron DeLaBarre-
        ///  
        /// </summary>
        public Vector3[] FindRaySphereIntersections(Ray ray)
        {
            Vector3 linePoint0 = ray.origin;
            Vector3 linePoint1 = ray.GetPoint(10);

            // http://www.codeproject.com/Articles/19799/Simple-Ray-Tracing-in-C-Part-II-Triangles-Intersec

            float px = linePoint0.x;
            float py = linePoint0.y;
            float pz = linePoint0.z;

            float vx = linePoint1.x - px;
            float vy = linePoint1.y - py;
            float vz = linePoint1.z - pz;

            float A = vx * vx + vy * vy + vz * vz;
            float B = 2 * (px * vx + py * vy + pz * vz - vx * c.x - vy * c.y - vz * c.z);
            float C = px * px - 2 * px * c.x + c.x * c.x + py * py - 2 * py * c.y + c.y * c.y +
                      pz * pz - 2 * pz * c.z + c.z * c.z - r * r;

            // discriminant
            float D = B * B - 4 * A * C;

            if (D < 0)
            {
                // miss
                return Array.Empty<Vector3>();
            }

            float t1 = (-B - Mathf.Sqrt(D)) / (2 * A);

            Vector3 solution1 = new(
                linePoint0.x * (1 - t1) + t1 * linePoint1.x,
                linePoint0.y * (1 - t1) + t1 * linePoint1.y,
                linePoint0.z * (1 - t1) + t1 * linePoint1.z);
            if (Math.Abs(D) < float.Epsilon)
            {
                // ray origin is on surface
                return new[] {solution1};
            }

            float t2 = (-B + Mathf.Sqrt(D)) / (2 * A);
            Vector3 solution2 = new(
                linePoint0.x * (1 - t2) + t2 * linePoint1.x,
                linePoint0.y * (1 - t2) + t2 * linePoint1.y,
                linePoint0.z * (1 - t2) + t2 * linePoint1.z);

            // prefer a solution that's on the line segment itself
            return Mathf.Abs(t1 - 0.5f) > Mathf.Abs(t2 - 0.5f)
                ? new[] {solution1, solution2}
                : new[] {solution2, solution1};
        }
    }
}