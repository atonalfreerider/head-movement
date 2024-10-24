using System;

namespace Util
{
    public static class Szudzik
    {
        public static ulong uintSzudzik2tupleCombine(uint x, uint y)
        {
            if (x != Math.Max(x, y))
                return (y * y) + x;
            return (x * x) + x + y;
        }

        public static uint[] uintSzudzik2tupleReverse(ulong z) //this number WILL be positive
        {
            uint zSpecial1 = (uint) Math.Floor(Math.Sqrt(z)); //this number WILL be positive (returns integer)
            ulong zSpecial2 = z - (zSpecial1 * zSpecial1); //this number WILL be positive (returns integer)

            return zSpecial2 < zSpecial1 
                ? new[] {(uint) zSpecial2, zSpecial1} 
                : new[] {zSpecial1, (uint) (zSpecial2 - zSpecial1)};
        }
    }
}