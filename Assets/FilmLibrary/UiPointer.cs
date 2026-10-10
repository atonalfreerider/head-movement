using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The viewer's 2D UI pointer: the mouse (or the primary touch) read straight from the Input System once per frame, in screen
/// pixels from the bottom-left corner (the canvases of the film UI are screen-space overlays at scale 1, so a pointer position
/// IS a UI position). There is no EventSystem on purpose: the project has none, and one would also feed the arrow keys and the
/// space bar to "selected" buttons. A later VR panel drives the same actions (FilmLibrary.Play / Back, FilmPlaybackBar) from
/// its own pointer; nothing here is XR specific.
/// </summary>
public static class UiPointer
{
    public static Vector2 Pos { get; private set; }
    public static bool Present { get; private set; }
    public static bool Down { get; private set; }
    public static bool Up { get; private set; }
    public static bool Held { get; private set; }
    public static bool Moved { get; private set; }

    static int frame = -1;
    static Vector2 last;
    static bool havePos;
    static bool prevHeld;

    // Edges are taken from the held state between two polls as well as from the Input System's own per-update flags: a press that an
    // injected event (the editor tools' simulate_pointer / simulate_key, a remote desktop) sets and clears between two frames is still seen
    // when it spans a frame, and a real press that spans none is caught by the flag.
    static readonly Dictionary<Key, (bool held, int frame, bool edge)> keys = new();

    /// <summary>read the devices once per rendered frame (calling it again in the same frame does nothing)</summary>
    public static void Poll()
    {
        if (frame == Time.frameCount) return;
        frame = Time.frameCount;
        Down = Up = Held = Moved = Present = false;
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            Pos = mouse.position.ReadValue();
            Present = true;
            Held = mouse.leftButton.isPressed;
            Down = mouse.leftButton.wasPressedThisFrame || (Held && !prevHeld);
            Up = mouse.leftButton.wasReleasedThisFrame || (!Held && prevHeld);
        }

        Touchscreen touch = Touchscreen.current;
        if (touch != null)
        {
            var t = touch.primaryTouch;
            if (t.press.isPressed || t.press.wasReleasedThisFrame)
            {
                Pos = t.position.ReadValue();
                Present = true;
                Held = t.press.isPressed;
                Down = t.press.wasPressedThisFrame || (Held && !prevHeld);
                Up = t.press.wasReleasedThisFrame || (!Held && prevHeld);
            }
        }

        prevHeld = Held;

        // the first reading is a position, not a movement (a stale pointer position must not select a card or wake the bar)
        Moved = havePos && (Pos - last).sqrMagnitude > 0.25f;
        havePos = true;
        last = Pos;
    }

    public static bool Pressed(Key key)
    {
        Keyboard k = Keyboard.current;
        if (k == null) return false;
        keys.TryGetValue(key, out var st);
        if (st.frame != Time.frameCount)
        {
            bool now = k[key].isPressed;
            st = (now, Time.frameCount, k[key].wasPressedThisFrame || (now && !st.held));
            keys[key] = st;
        }

        return st.edge;
    }

    public static bool IsDown(Key key)
    {
        Keyboard k = Keyboard.current;
        return k != null && k[key].isPressed;
    }

    /// <summary>true on the press and then, while the key stays down, every <paramref name="interval"/> seconds after
    /// <paramref name="delay"/> (key repeat for the seek keys); <paramref name="heldSince"/> is the caller's own timer</summary>
    public static bool Repeat(Key key, ref float heldSince, float delay = 0.4f, float interval = 0.12f)
    {
        Keyboard k = Keyboard.current;
        if (Pressed(key))
        {
            heldSince = Time.unscaledTime;
            return true;
        }

        if (k == null || !k[key].isPressed)
        {
            heldSince = -1f;
            return false;
        }

        if (heldSince < 0f) heldSince = Time.unscaledTime;
        float held = Time.unscaledTime - heldSince;
        if (held < delay) return false;
        float prev = held - Time.unscaledDeltaTime;
        return Mathf.FloorToInt((held - delay) / interval) != Mathf.FloorToInt((prev - delay) / interval);
    }
}
