using System;

/// <summary>
/// Display mode of the headset (set by the VR mode, read by the scene layers): in PASSTHROUGH the real room is the background, so the
/// viewer draws only the dancers and the graphics on top: no room mesh, no floor fill (a black translucent plane would darken the real
/// floor), the floor grid only as faint short marks near the dancers. In IMMERSIVE (and on the desktop) nothing changes.
/// </summary>
public static class MixedReality
{
    public static bool Passthrough { get; private set; }

    /// <summary>raised when the mode changes; layers re-apply their visibility</summary>
    public static event Action Changed;

    public static void Set(bool passthrough)
    {
        if (Passthrough == passthrough) return;
        Passthrough = passthrough;
        Changed?.Invoke();
    }
}
