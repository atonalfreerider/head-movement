using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The viewer's physics / rhythm choice, a PERMANENT setting (owner 2026-11-15: a toggle on the left-wrist menu). Stored in PlayerPrefs
    /// ("hm_vr_skeleton": "physics" | "rhythm", default rhythm) like the display and captions choices, applied to every lesson when it
    /// starts and the moment it is toggled. It also holds the film director to it (FilmDirector.PhysicsOverride): no segment of a lesson
    /// switches the mode by itself.
    /// </summary>
    public static class VrSkeletonPref
    {
        public const string PrefKey = "hm_vr_skeleton";

        static bool? physics;
        public static int Toggles { get; private set; }

        public static bool Physics
        {
            get
            {
                if (!physics.HasValue)
                {
                    string v = "";
                    try
                    {
                        v = PlayerPrefs.GetString(PrefKey, "rhythm");
                    }
                    catch (System.Exception)
                    {
                        // no prefs store (batch mode): rhythm
                    }

                    physics = v == "physics";
                }

                return physics.Value;
            }
        }

        public static void Set(bool on)
        {
            physics = on;
            try
            {
                PlayerPrefs.SetString(PrefKey, on ? "physics" : "rhythm");
                PlayerPrefs.Save();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("VrSkeletonPref: could not save the choice: " + e.Message);
            }

            Apply();
        }

        public static void Toggle()
        {
            Toggles++;
            Set(!Physics);
        }

        /// <summary>hold the director to the choice and show it now</summary>
        public static void Apply()
        {
            FilmDirector.PhysicsOverride = Physics;
            HeadMovement hm = HeadMovement.Instance;
            if (hm != null) hm.SetSkeletonMode(Physics ? SkeletonStyle.Mode.Physics : SkeletonStyle.Mode.Rhythm);
        }

        /// <summary>leaving VR: the desktop film follows its own segments again</summary>
        public static void Release() => FilmDirector.PhysicsOverride = null;
    }
}
