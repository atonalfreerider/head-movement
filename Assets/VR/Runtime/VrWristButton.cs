using System;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The persistent small "menu" button on the back of the non-dominant wrist (like a watch): poke it or point and pinch with the other
    /// hand to call the lesson menu back from anywhere in a lesson. It shows while the hand is tracked and the back of the hand is turned
    /// toward the user (when the palm is up, the hand panel shows instead).
    /// </summary>
    [DefaultExecutionOrder(155)]
    [AddComponentMenu("")]
    public sealed class VrWristButton : MonoBehaviour
    {
        public static VrWristButton Instance { get; private set; }

        public Action OnPressed = () => { };
        public Func<bool> Available = () => true;
        public float Alpha { get; private set; }
        public VrPanel Panel => panel;

        VrPanel panel;
        VrWidget button;
        Vector3 vel;
        bool wasShown;
        const float SizeMm = 62f;

        void Awake()
        {
            Instance = this;
            panel = VrPanel.Create("VR wrist menu", new Vector2(SizeMm, SizeMm), transform, false);
            button = panel.AddButton("menu", "", VrPanel.R(0f, 0f, SizeMm, SizeMm), () => OnPressed(), 20f);
            button.Back.sprite = FilmUi.Disc;
            button.Back.type = UnityEngine.UI.Image.Type.Simple;
            button.Normal = new Color(0.07f, 0.082f, 0.115f, 0.9f);
            button.Hot = new Color(0.25f, 0.28f, 0.36f, 1f);
            // three bars: the menu icon
            for (int i = -1; i <= 1; i++)
            {
                UnityEngine.UI.Image bar = VrPanel.NewImage(button.Back.transform, "bar", null, Color.white);
                bar.rectTransform.sizeDelta = new Vector2(30f, 5f);
                bar.rectTransform.anchoredPosition = new Vector2(0f, i * 11f);
                button.Glyphs.Add(bar);
            }

            panel.Alpha = 0f;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            VrInput input = VrInput.Instance;
            bool want = false;
            VrHand off = input != null && input.Source != null ? input.OffHand : null;
            Vector3 pos = default;
            if (off != null && off.Tracked && Available())
            {
                Vector3 normal = off.State.PalmNormal;
                Vector3 toHead = (input.Frame.HeadPosition - off.State.Wrist).normalized;
                // visible when the back of the hand is toward the user's eyes
                float back = Vector3.Dot(-normal, toHead);
                want = back > (wasShown ? 0.0f : 0.15f) && !off.Palm.Facing;
                // on the back of the wrist, a little up the forearm
                pos = off.State.Wrist - off.State.Forward * 0.045f - normal * 0.034f;
            }

            float dt = Time.unscaledDeltaTime;
            Alpha = Mathf.MoveTowards(Alpha, want ? 1f : 0f, dt * 8f);
            panel.Alpha = Alpha;
            panel.AttachedTo = off?.Side;
            if (Alpha <= 0.002f)
            {
                wasShown = false;
                return;
            }

            if (want)
            {
                Vector3 p = wasShown ? Vector3.SmoothDamp(panel.transform.position, pos, ref vel, 0.05f, 3f, dt) : pos;
                panel.FaceFrom(p, input.Frame.HeadPosition, 1f);
                wasShown = true;
            }
        }
    }
}
