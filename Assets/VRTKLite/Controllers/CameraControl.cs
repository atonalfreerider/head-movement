using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace VRTKLite.Controllers
{
    /// <summary>what the rig knows about the couple this frame (XZ only; the camera never reads a dancer's height)</summary>
    public struct CameraFollow
    {
        /// <summary>XZ midpoint of the two pelvises at the shown frame</summary>
        public Vector2 Raw;
        /// <summary>damped look anchor: dead zone + critically damped spring (lags Raw by up to ~0.3 m)</summary>
        public Vector2 LookAnchor;
        /// <summary>damped eye anchor: a slower spring, so the camera pans first and then trucks</summary>
        public Vector2 EyeAnchor;
        public float DanceTime;
        public bool Playing, Cut, Valid;
    }

    /// <summary>a camera director (the view-state tour): a world pose per frame, or false for "no opinion"</summary>
    public interface ICameraDirector
    {
        /// <summary>the pose of the CURRENT shot only (no blending: the rig blends from what is on screen)</summary>
        /// <param name="fov">vertical field of view in degrees; NaN = the camera's base FOV</param>
        bool TryGetCameraPose(in CameraFollow follow, float dt, out Vector3 eye, out Vector3 look, out float fov);

        /// <summary>name of the state that drives the camera (hm_state), "none" when inactive</summary>
        string DirectorState { get; }

        /// <summary>changes whenever the director switches shot (view state, capture load); the rig then blends from
        /// the pose on screen over ShotBlendSeconds</summary>
        int Shot { get; }

        /// <summary>blend length into the current shot in seconds; 0 = cut</summary>
        float ShotBlendSeconds { get; }
    }

    /// <summary>
    /// Cylindrical blend between two camera poses (Cinemachine's CylindricalPosition hint): the look point lerps; the
    /// eye's horizontal distance and absolute height lerp; its azimuth around the look point turns the shortest way
    /// FIXED AT THE FIRST FRAME and then tracks both (moving) endpoints continuously, unwrapped, so the turn never
    /// reverses when their azimuth difference crosses 180 degrees mid-blend. The source is given in cylindrical
    /// coordinates (azimuth, horizontal distance, eye height around its look point), so its azimuth stays defined and
    /// continuous even straight overhead. Reset() at every blend start.
    /// The TARGET's azimuth is ill-conditioned when its eye is nearly above its look point (it spins as the eye passes
    /// over, e.g. the graph chase camera turning round mid-blend, or sits at an arbitrary angle, the Overhead state):
    /// below CylindricalAbove the eye path crossfades to a straight (Cartesian) lerp, fully straight below
    /// CartesianBelow, and once lowered the weight stays down for the rest of the blend (no swing back onto a
    /// re-wound azimuth). Both paths start at the source and end at the target, so the blend stays continuous.
    /// </summary>
    public struct CylindricalBlend
    {
        /// <summary>target eye's horizontal distance from its look point (m): straight path below, cylindrical above</summary>
        public const float CartesianBelow = 0.5f, CylindricalAbove = 1.5f;

        bool started;
        float delta; // unwrapped azimuth difference target - source (rad)
        float weight; // cylindrical share, non-increasing during a blend

        public float Delta => delta;

        /// <summary>the cylindrical share of the eye path now (1 = cylindrical, 0 = straight lerp)</summary>
        public float CylindricalWeight => started ? weight : 1f;

        public void Reset()
        {
            started = false;
            delta = 0f;
            weight = 1f;
        }

        /// <param name="a0">source azimuth (rad) of the eye around look0; h0 its horizontal distance; y0 its height</param>
        public void Evaluate(Vector3 look0, float a0, float h0, float y0, Vector3 eye1, Vector3 look1, float s,
            out Vector3 eye, out Vector3 look)
        {
            look = Vector3.Lerp(look0, look1, s);
            Vector2 d1 = new(eye1.x - look1.x, eye1.z - look1.z);
            float h1 = d1.magnitude;
            float a1 = h1 >= 1e-4f ? Mathf.Atan2(d1.y, d1.x) : a0 + delta; // a target straight overhead: keep the turn
            float shortest = WrapPi(a1 - a0);
            // first frame: the shortest way round; then the value of the same angle closest to last frame's
            delta = started ? delta + WrapPi(shortest - delta) : shortest;
            float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CartesianBelow, CylindricalAbove, h1));
            weight = started ? Mathf.Min(weight, w) : w;
            started = true;
            float a = a0 + delta * s;
            float hs = Mathf.Max(0f, h0);
            float h = Mathf.Lerp(hs, h1, s);
            float y = Mathf.Lerp(y0, eye1.y, s);
            eye = new Vector3(look.x + h * Mathf.Cos(a), y, look.z + h * Mathf.Sin(a));
            if (weight < 1f)
            {
                Vector3 eye0 = new(look0.x + hs * Mathf.Cos(a0), y0, look0.z + hs * Mathf.Sin(a0));
                eye = Vector3.Lerp(Vector3.Lerp(eye0, eye1, s), eye, weight);
            }
        }

        public static void ToCylindrical(Vector3 eye, Vector3 look, out float azimuth, out float distance)
        {
            Vector2 d = new(eye.x - look.x, eye.z - look.z);
            distance = d.magnitude;
            azimuth = Mathf.Atan2(d.y, d.x);
        }

        public static float WrapPi(float a)
        {
            a %= 2f * Mathf.PI;
            if (a > Mathf.PI) a -= 2f * Mathf.PI;
            else if (a < -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }
    }

    /// <summary>
    /// The desktop camera rig (VIEWER_SPEC 5.2) and the only writer of the desktop camera's transform and FOV.
    /// - Follow: the XZ midpoint of the two pelvises feeds a dead zone (0.15 m, slow 2.5 s recentre) and two
    ///   critically damped springs (look 0.6 s, eye 0.9 s), so steps and bounces do not shake the view. The rig never
    ///   reads a dancer's height: the camera does not bob with the centre of gravity. Seeks, loops and capture loads
    ///   are cuts (snap). Paused, the view coasts to a stop where it is (no glide with nothing playing); stepping or
    ///   scrubbing while paused re-centres on the couple (deterministic stills).
    /// - Free (user): cylindrical around the anchors with ABSOLUTE eye height H and look height L, which only the
    ///   keyboard or hm_orbit change. A/D orbit, W/S dolly (horizontal), E/Q crane up/down, Z/X tilt the look point
    ///   down/up, Shift x3.
    /// - Director: an ICameraDirector (DanceTour) returns the pose of its current shot; its height depends on the state
    ///   only. Every shot change and every hand-over to the director blends from a SNAPSHOT OF THE POSE ON SCREEN (with
    ///   its motion - look velocity, orbit rate, dolly and crane rates - decaying over MomentumSeconds, so the camera
    ///   does not stop dead) to the live director pose: cylindrical, turn direction fixed at the blend start. So a switch never pops, an interrupted blend continues from where the camera is, and the
    ///   outgoing state's own motion cannot move the start. A cut (seek, loop wrap) moves a running blend's start
    ///   with the couple's jump and the blend carries on (re-taking the shortest turn on that cut frame).
    ///   Any camera key takes over (the eye stays where it is, the look point swings to the anchor). Disabled = parked
    ///   (hm_hair frame): nothing writes the transform.
    /// Runs in LateUpdate at order -20: after HeadMovement.Update posed the dancers, before DanceTour (-10) and the
    /// view-facing overlays (0). The per-frame trace (StartTrace / StopTrace, hm_camtrace) records what is rendered.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class CameraControl : MonoBehaviour
    {
        public enum Owner
        {
            Free,
            Director
        }

        public float Speed = 0.3f; // scene value 1: rates are 1 rad/s orbit, 1 m/s dolly

        [Tooltip("output only: the point the camera looks at now (writes are ignored)")]
        public Vector3 Center = Vector3.zero;

        [Header("Follow (XZ only)")]
        public float DeadZone = 0.15f;
        public float RecenterSeconds = 2.5f;
        public float LookSmoothTime = 0.6f;
        public float EyeSmoothTime = 0.9f;
        public float MaxFollowSpeed = 2.5f;
        public float PausedSmoothTime = 0.25f;
        [Tooltip("paused (not scrubbing): the follow's remaining velocity decays with this time constant")]
        public float PausedCoastSeconds = 0.15f;
        public float CutTimeJump = 0.5f;
        public float CutRawJump = 1.0f;

        [Header("Free-fly (first load; kept across captures)")]
        public float DefaultAzimuthDeg = 90f;
        public float DefaultDistance = 2.9f;
        public float DefaultEyeHeight = 1.6f;
        public float DefaultLookHeight = 0.95f;

        [Header("Blends and input")]
        public float BlendSeconds = 1f;
        [Tooltip("a blend starts from the pose on screen moving on with its velocity, decaying with this time constant")]
        public float MomentumSeconds = 0.25f;
        public float MaxMomentumSpeed = 4f;
        [Tooltip("rad/s; the orbit rate carried into a blend (only when the eye is >= 0.3 m off the look point's vertical)")]
        public float MaxMomentumTurn = 2f;
        public float TakeoverLookSeconds = 0.6f;
        public float InputRampSeconds = 0.12f;

        public delegate void MovementUpdate();

        public MovementUpdate MovementUpdater;

        const float MinDistance = 0.02f, MaxDistance = 10f, MinEyeHeight = 0.05f, MaxEyeHeight = 10f, MaxLookHeight = 2.2f;

        // free-fly parameters (cylindrical): azimuth, horizontal distance, absolute eye / look heights
        float phi, dist, eyeH, lookH;
        bool initialised;

        // follow
        Vector2 raw, deadZoneAnchor, lookA, eyeA, velLook, velEye, cutShift;
        bool hasRaw, playing, cutPending = true, cutThisFrame, scrubbing;
        float danceTime = float.NaN, pausedAt = float.NaN;
        int cuts, lastCutFrame = -1;

        // ownership and blends
        Owner mode = Owner.Free;
        ICameraDirector director;
        int shotSeen = int.MinValue;
        bool blending;
        float blendStart, blendLength = 1f;
        // the blend's start: the look point and the eye in cylindrical coordinates around it, with their rates
        Vector3 srcLook, srcLookVel;
        float srcAz, srcDist, srcY, srcAzRate, srcDistRate, srcYRate, srcFov;
        CylindricalBlend cylinder;
        int blends;
        bool swingLook;
        float swingStart;
        Vector3 swingFromLook;
        float swingFromFov;

        // key velocities (ramped)
        float vPhi, vDist, vHeight, vLook;

        // the pose written last and its rates (a blend's momentum): look velocity, eye azimuth / distance / height rates
        Camera cam;
        float baseFov = -1f;
        Vector3 eye, look, lookVel;
        float azRate, distRate, yRate;
        float fov = 60f;
        bool poseValid;

        public Owner Mode => mode;
        public bool Parked => !enabled;
        public bool Blending => blending || swingLook;
        /// <summary>number of follow cuts (snaps) so far</summary>
        public int Cuts => cuts;
        /// <summary>Time.frameCount of the last follow cut</summary>
        public int LastCutFrame => lastCutFrame;

        public ICameraDirector Director
        {
            get => director;
            set
            {
                if (ReferenceEquals(director, value)) return;
                director = value;
                if (director == null && mode == Owner.Director) TakeControl();
            }
        }

        /// <summary>azimuth (rad, around +Y from +X toward +Z) of the eye around the look point</summary>
        public float Azimuth => Mathf.Atan2(eye.z - look.z, eye.x - look.x);

        /// <summary>polar angle (rad from +Y) of the eye seen from the look point</summary>
        public float Polar
        {
            get
            {
                float r = Radius;
                return r > 1e-5f ? Mathf.Acos(Mathf.Clamp((eye.y - look.y) / r, -1f, 1f)) : Mathf.PI / 2;
            }
        }

        public float Radius => (eye - look).magnitude;
        /// <summary>the camera's field of view outside director states that narrow it (overhead)</summary>
        public float BaseFov => baseFov > 0 ? baseFov : 60f;
        public float EyeHeight => eyeH;
        public float LookHeight => lookH;
        public float Distance => dist;
        public Vector3 Eye => eye;
        public Vector3 Look => look;
        public CameraFollow Follow => new()
        {
            Raw = raw, LookAnchor = lookA, EyeAnchor = eyeA, DanceTime = danceTime, Playing = playing, Cut = cutThisFrame,
            Valid = hasRaw
        };

        void Awake()
        {
            InitDefaults();
            cam = GetComponentInChildren<Camera>();
        }

        void InitDefaults()
        {
            if (initialised) return;
            initialised = true;
            phi = DefaultAzimuthDeg * Mathf.Deg2Rad;
            dist = DefaultDistance;
            eyeH = DefaultEyeHeight;
            lookH = DefaultLookHeight;
        }

        void Start()
        {
            if (cam == null) cam = GetComponentInChildren<Camera>();
            if (cam != null && baseFov < 0) baseFov = cam.fieldOfView;
            ApplyPose(Time.unscaledDeltaTime, false);
        }

        void OnEnable()
        {
            // returning from a park (hm_hair / hm_shoes frame): snap to the couple; the pose written before the park is
            // not what was on screen, so nothing blends from it (the return is a cut)
            cutPending = true;
            poseValid = false;
        }

        void OnDisable()
        {
            if (tracing) Application.onBeforeRender -= RecordTrace;
            tracing = false;
        }

        // ------------------------------------------------------------------ inputs

        /// <summary>the couple's XZ (pelvis midpoint) at the shown frame; called by HeadMovement.Update every frame</summary>
        public void SetFollow(Vector2 rawXZ, float time, bool isPlaying)
        {
            if (!float.IsFinite(rawXZ.x) || !float.IsFinite(rawXZ.y)) return; // keep the last valid point
            if (!hasRaw) cutPending = true;
            else
            {
                if (float.IsFinite(time) && float.IsFinite(danceTime) && Mathf.Abs(time - danceTime) > CutTimeJump) cutPending = true;
                if ((rawXZ - raw).magnitude > CutRawJump) cutPending = true;
            }

            // paused: a change of the shown frame after the pause (step, scrub) re-centres; the pause itself does not
            if (isPlaying) scrubbing = false;
            else if (playing || !hasRaw || !float.IsFinite(pausedAt)) pausedAt = time;
            else if (float.IsFinite(time) && Mathf.Abs(time - pausedAt) > 1e-4f)
            {
                scrubbing = true;
                pausedAt = time;
            }

            raw = rawXZ;
            danceTime = time;
            playing = isPlaying;
            hasRaw = true;
        }

        /// <summary>snap every anchor to the couple now (capture load, hm_orbit, return from park)</summary>
        public void SnapFollow()
        {
            cutPending = false;
            cutThisFrame = true;
            lastCutFrame = Time.frameCount;
            if (!hasRaw) return;
            cutShift = raw - lookA; // how far the follow jumps (a running blend's start moves with it)
            deadZoneAnchor = lookA = eyeA = raw;
            velLook = velEye = Vector2.zero;
            cuts++;
        }

        /// <summary>hand the camera to the director (explicit tour commands, O key); blends from the pose on screen</summary>
        public void UseDirector(bool blend = true)
        {
            if (director == null || mode == Owner.Director) return;
            mode = Owner.Director;
            swingLook = false;
            shotSeen = director.Shot;
            if (blend && poseValid) StartBlend(BlendSeconds);
            else blending = false;
        }

        /// <summary>the user takes the camera (keys, hm_orbit, tour stop): free-fly seeded from the current pose, the
        /// eye stays where it is and the look point swings to the anchor over TakeoverLookSeconds</summary>
        public void TakeControl()
        {
            if (mode == Owner.Free) return;
            mode = Owner.Free;
            blending = false;
            if (!poseValid) return;
            SeedFree(eye, look);
            swingLook = true;
            swingStart = Time.unscaledTime;
            swingFromLook = look;
            swingFromFov = fov;
        }

        /// <summary>freeze the pose on screen (and its velocity) as the start of a blend to the director's live pose</summary>
        void StartBlend(float seconds)
        {
            blending = poseValid && seconds > 0f;
            if (!blending) return;
            // the snapshot is last frame's pose: start the clock there, so this frame already moves on (no 1-frame stall)
            blendStart = Time.unscaledTime - Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
            blendLength = seconds;
            srcLook = look;
            CylindricalBlend.ToCylindrical(eye, look, out srcAz, out srcDist);
            srcY = eye.y;
            srcFov = fov;
            srcLookVel = Vector3.ClampMagnitude(lookVel, MaxMomentumSpeed);
            srcAzRate = Mathf.Clamp(azRate, -MaxMomentumTurn, MaxMomentumTurn);
            srcDistRate = Mathf.Clamp(distRate, -MaxMomentumSpeed, MaxMomentumSpeed);
            srcYRate = Mathf.Clamp(yRate, -MaxMomentumSpeed, MaxMomentumSpeed);
            cylinder.Reset();
            blends++;
        }

        /// <summary>
        /// Jump to a spherical orbit around the look point (lookA at height L): azimuth (rad), polar angle from +Y
        /// (rad), radius (m). Switches to free-fly and snaps the follow (deterministic screenshots).
        /// </summary>
        public void SetOrbit(float azimuth, float polar, float radius)
        {
            InitDefaults();
            polar = Mathf.Clamp(polar, 0.01f, Mathf.PI - 0.01f);
            radius = Mathf.Clamp(radius, 0.3f, MaxDistance);
            if (!float.IsFinite(azimuth)) azimuth = phi;
            phi = NormalizeAngle(azimuth);
            dist = Mathf.Clamp(radius * Mathf.Sin(polar), MinDistance, MaxDistance);
            eyeH = Mathf.Clamp(lookH + radius * Mathf.Cos(polar), MinEyeHeight, MaxEyeHeight);
            GoFreeAndSnap();
        }

        /// <summary>set any free-fly parameter (NaN keeps it): azimuth (rad), horizontal distance (m), eye height and
        /// look height (world m). Switches to free-fly and snaps the follow.</summary>
        public void SetFree(float azimuthRad = float.NaN, float distance = float.NaN, float eyeHeight = float.NaN, float lookHeight = float.NaN)
        {
            InitDefaults();
            if (float.IsFinite(azimuthRad)) phi = NormalizeAngle(azimuthRad);
            if (float.IsFinite(distance)) dist = Mathf.Clamp(distance, MinDistance, MaxDistance);
            if (float.IsFinite(eyeHeight)) eyeH = Mathf.Clamp(eyeHeight, MinEyeHeight, MaxEyeHeight);
            if (float.IsFinite(lookHeight)) lookH = Mathf.Clamp(lookHeight, 0f, MaxLookHeight);
            GoFreeAndSnap();
        }

        void GoFreeAndSnap()
        {
            mode = Owner.Free;
            blending = swingLook = false;
            vPhi = vDist = vHeight = vLook = 0f;
            lookVel = Vector3.zero;
            azRate = distRate = yRate = 0f;
            SnapFollow();
            if (enabled) ApplyPose(0f, false);
        }

        // ------------------------------------------------------------------ per frame

        void LateUpdate()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            ApplyPose(dt, true);
            cutThisFrame = false;
        }

        void ApplyPose(float dt, bool readKeys)
        {
            InitDefaults();
            if (cam == null) cam = GetComponentInChildren<Camera>();
            if (cam != null && baseFov < 0) baseFov = cam.fieldOfView;
            float fov0 = baseFov > 0 ? baseFov : 60f;

            if (cutPending) SnapFollow();
            UpdateFollow(dt);

            bool moving = false;
            if (readKeys) moving = ReadKeys(dt);

            FreePose(out Vector3 freeEye, out Vector3 freeLook);
            Vector3 e = freeEye, l = freeLook;
            float f = fov0;
            if (mode == Owner.Director)
            {
                if (director != null && director.TryGetCameraPose(Follow, dt, out Vector3 de, out Vector3 dl, out float dfov) &&
                    IsFinite(de) && IsFinite(dl))
                {
                    float df = float.IsFinite(dfov) ? dfov : fov0;
                    int shot = director.Shot;
                    if (shot != shotSeen)
                    {
                        // a new shot (view state): blend from what is on screen, whatever was happening
                        shotSeen = shot;
                        StartBlend(director.ShotBlendSeconds);
                    }

                    if (cutThisFrame && blending)
                    {
                        // seek / loop wrap: the follow jumped with the couple; so does the blend's start, and the blend
                        // carries on (the cut is exactly the couple's jump, not a jump to the end of the blend). The
                        // director's pose may have jumped too (graph chase): take the shortest turn again from here,
                        // on the cut frame, instead of unwrapping the jump into a spin.
                        srcLook += new Vector3(cutShift.x, 0f, cutShift.y);
                        cylinder.Reset();
                    }
                    if (blending)
                    {
                        float t = Time.unscaledTime - blendStart;
                        float k = blendLength > 0 ? Mathf.Clamp01(t / blendLength) : 1f;
                        float s = Mathf.SmoothStep(0f, 1f, k);
                        float drift = MomentumSeconds > 0 ? MomentumSeconds * (1f - Mathf.Exp(-t / MomentumSeconds)) : 0f;
                        cylinder.Evaluate(srcLook + srcLookVel * drift, srcAz + srcAzRate * drift,
                            Mathf.Max(0f, srcDist + srcDistRate * drift), srcY + srcYRate * drift, de, dl, s, out e, out l);
                        f = Mathf.Lerp(srcFov, df, s);
                        if (k >= 1f) blending = false;
                    }
                    else
                    {
                        e = de;
                        l = dl;
                        f = df;
                    }
                }
                else if (director != null && director.DirectorState != "none" && poseValid)
                {
                    // a state is active but cannot pose yet (a capture is loading): hold the last pose, keep the owner
                    e = eye;
                    l = look;
                    f = fov;
                }
                else
                {
                    // the director has no opinion (tour stopped): free-fly from where we are
                    TakeControl();
                    FreePose(out e, out l);
                }
            }

            if (mode == Owner.Free && swingLook)
            {
                if (cutThisFrame) swingFromLook += new Vector3(cutShift.x, 0f, cutShift.y);
                float k = TakeoverLookSeconds > 0 ? Mathf.Clamp01((Time.unscaledTime - swingStart) / TakeoverLookSeconds) : 1f;
                float s = Mathf.SmoothStep(0f, 1f, k);
                l = Vector3.Lerp(swingFromLook, l, s);
                f = Mathf.Lerp(swingFromFov, fov0, s);
                if (k >= 1f) swingLook = false;
            }

            // the on-screen rates (lightly filtered), the momentum of the next blend's start; zero across cuts
            if (poseValid && dt > 1e-4f && !cutThisFrame)
            {
                float a = 1f - Mathf.Exp(-dt / 0.05f);
                CylindricalBlend.ToCylindrical(eye, look, out float az0, out float d0);
                CylindricalBlend.ToCylindrical(e, l, out float az1, out float d1);
                // the azimuth is only meaningful away from the look point's vertical (overhead it spins)
                float turn = d0 > 0.3f && d1 > 0.3f ? CylindricalBlend.WrapPi(az1 - az0) / dt : 0f;
                lookVel = Vector3.Lerp(lookVel, (l - look) / dt, a);
                azRate = Mathf.Lerp(azRate, turn, a);
                distRate = Mathf.Lerp(distRate, (d1 - d0) / dt, a);
                yRate = Mathf.Lerp(yRate, (e.y - eye.y) / dt, a);
            }
            else if (cutThisFrame)
            {
                lookVel = Vector3.zero;
                azRate = distRate = yRate = 0f;
            }

            eye = e;
            look = l;
            fov = f;
            poseValid = true;
            Center = look;
            Vector3 forward = look - eye;
            if (forward.sqrMagnitude < 1e-8f) forward = transform.forward;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.9999f ? Vector3.forward : Vector3.up;
            transform.SetPositionAndRotation(eye, Quaternion.LookRotation(forward, up));
            if (cam != null && Mathf.Abs(cam.fieldOfView - fov) > 0.01f) cam.fieldOfView = fov;
            if (moving) MovementUpdater?.Invoke();
        }

        void UpdateFollow(float dt)
        {
            if (!hasRaw || dt <= 0f) return;
            Vector2 goal;
            float tLook, tEye;
            if (playing)
            {
                // camera window: the anchor drifts slowly to the couple and is dragged along when they leave the window
                deadZoneAnchor += (raw - deadZoneAnchor) * (1f - Mathf.Exp(-dt / Mathf.Max(0.01f, RecenterSeconds)));
                Vector2 d = raw - deadZoneAnchor;
                float m = d.magnitude;
                if (m > DeadZone) deadZoneAnchor = raw - d * (DeadZone / m);
                goal = deadZoneAnchor;
                tLook = LookSmoothTime;
                tEye = EyeSmoothTime;
            }
            else if (scrubbing)
            {
                // paused and stepping / scrubbing: centre exactly on the shown frame's couple (deterministic stills)
                deadZoneAnchor = raw;
                goal = raw;
                tLook = tEye = PausedSmoothTime;
            }
            else
            {
                // paused (user pause, end of take): nothing plays, so the view coasts to a stop where it is
                float decay = PausedCoastSeconds > 0 ? Mathf.Exp(-dt / PausedCoastSeconds) : 0f;
                lookA += velLook * (PausedCoastSeconds * (1f - decay));
                eyeA += velEye * (PausedCoastSeconds * (1f - decay));
                velLook *= decay;
                velEye *= decay;
                if (velLook.sqrMagnitude < 1e-8f) velLook = Vector2.zero;
                if (velEye.sqrMagnitude < 1e-8f) velEye = Vector2.zero;
                return;
            }

            lookA = Vector2.SmoothDamp(lookA, goal, ref velLook, tLook, MaxFollowSpeed, dt);
            eyeA = Vector2.SmoothDamp(eyeA, goal, ref velEye, tEye, MaxFollowSpeed, dt);
        }

        /// <summary>W/S dolly, A/D orbit, E/Q crane, Z/X tilt, Shift x3; any of them takes the camera from the director</summary>
        bool ReadKeys(float dt)
        {
            Keyboard k = Keyboard.current;
            float wPhi = 0, wDist = 0, wHeight = 0, wLook = 0, boost = 1;
            if (k != null)
            {
                wPhi = (k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0);
                wDist = (k.sKey.isPressed ? 1 : 0) - (k.wKey.isPressed ? 1 : 0);
                wHeight = (k.eKey.isPressed ? 1 : 0) - (k.qKey.isPressed ? 1 : 0);
                wLook = (k.xKey.isPressed ? 1 : 0) - (k.zKey.isPressed ? 1 : 0);
                if (k.leftShiftKey.isPressed || k.rightShiftKey.isPressed) boost = 3f;
            }

            bool pressed = wPhi != 0 || wDist != 0 || wHeight != 0 || wLook != 0;
            if (pressed && mode == Owner.Director) TakeControl();
            float a = InputRampSeconds > 0 ? 1f - Mathf.Exp(-dt / InputRampSeconds) : 1f;
            float rate = Speed * boost;
            vPhi = Mathf.Lerp(vPhi, wPhi * rate, a);
            vDist = Mathf.Lerp(vDist, wDist * rate, a);
            vHeight = Mathf.Lerp(vHeight, wHeight * 0.8f * rate, a);
            vLook = Mathf.Lerp(vLook, wLook * 0.5f * rate, a);
            if (!pressed && Mathf.Abs(vPhi) + Mathf.Abs(vDist) + Mathf.Abs(vHeight) + Mathf.Abs(vLook) < 1e-4f)
            {
                vPhi = vDist = vHeight = vLook = 0f;
                return false;
            }

            if (mode != Owner.Free) return pressed;
            phi = NormalizeAngle(phi + vPhi * dt);
            dist = Mathf.Clamp(dist + vDist * dt, MinDistance, MaxDistance);
            eyeH = Mathf.Clamp(eyeH + vHeight * dt, MinEyeHeight, MaxEyeHeight);
            lookH = Mathf.Clamp(lookH + vLook * dt, 0f, MaxLookHeight);
            return true;
        }

        /// <summary>the eye orbits the (slower) eye anchor; close up it moves to the look anchor so a 0.3 m lag cannot
        /// swing a close-up camera around</summary>
        Vector2 EyeBase(float d) => Vector2.Lerp(lookA, eyeA, Mathf.InverseLerp(0.5f, 1.5f, d));

        void FreePose(out Vector3 freeEye, out Vector3 freeLook)
        {
            Vector2 b = EyeBase(dist);
            freeEye = new Vector3(b.x + dist * Mathf.Cos(phi), eyeH, b.y + dist * Mathf.Sin(phi));
            freeLook = new Vector3(lookA.x, lookH, lookA.y);
        }

        /// <summary>free-fly parameters that reproduce an eye position exactly (InheritPosition)</summary>
        void SeedFree(Vector3 e, Vector3 l)
        {
            eyeH = Mathf.Clamp(e.y, MinEyeHeight, MaxEyeHeight);
            lookH = Mathf.Clamp(l.y, 0f, MaxLookHeight);
            float d = dist;
            for (int i = 0; i < 3; i++)
            {
                Vector2 off = new Vector2(e.x, e.z) - EyeBase(d);
                d = Mathf.Max(MinDistance, off.magnitude);
                if (off.sqrMagnitude > 1e-10f) phi = Mathf.Atan2(off.y, off.x);
            }

            dist = Mathf.Clamp(d, MinDistance, MaxDistance);
            vPhi = vDist = vHeight = vLook = 0f;
        }

        // ------------------------------------------------------------------ per-frame trace (playtests)

        public struct TraceSample
        {
            public int Frame;
            public float Time, Dt;
            public Vector3 Eye, Look;
            public float Fov;
            /// <summary>a follow cut (seek, loop wrap, load, hm_orbit) happened this frame</summary>
            public bool Cut;
            public bool Blending, Director, Parked;
            public int Shot;
            public string State;
        }

        const int MaxTrace = 60000;
        readonly List<TraceSample> trace = new();
        bool tracing;

        public bool Tracing => tracing;
        public int TraceCount => trace.Count;

        /// <summary>record the rendered camera every frame (Application.onBeforeRender) until StopTrace</summary>
        public void StartTrace()
        {
            trace.Clear();
            if (!tracing) Application.onBeforeRender += RecordTrace;
            tracing = true;
        }

        public List<TraceSample> StopTrace()
        {
            if (tracing) Application.onBeforeRender -= RecordTrace;
            tracing = false;
            return new List<TraceSample>(trace);
        }

        void RecordTrace()
        {
            if (trace.Count >= MaxTrace || this == null) return;
            Camera c = cam != null ? cam : GetComponentInChildren<Camera>();
            Transform t = c != null ? c.transform : transform;
            trace.Add(new TraceSample
            {
                Frame = Time.frameCount, Time = Time.unscaledTime, Dt = Time.unscaledDeltaTime, Eye = t.position, Look = Center,
                Fov = c != null ? c.fieldOfView : fov, Cut = lastCutFrame == Time.frameCount, Blending = blending || swingLook,
                Director = mode == Owner.Director, Parked = !enabled, Shot = director != null ? director.Shot : 0,
                State = director != null ? director.DirectorState : "none"
            });
        }

        // ------------------------------------------------------------------ helpers

        static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        /// <summary>Normalizes an angle to the range [-PI, PI].</summary>
        static float NormalizeAngle(float angle)
        {
            angle %= 2 * Mathf.PI;
            if (angle > Mathf.PI) angle -= 2 * Mathf.PI;
            if (angle < -Mathf.PI) angle += 2 * Mathf.PI;
            return angle;
        }

        static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        static float[] V(Vector2 v) => new[] { v.x, v.y };

        /// <summary>hm_state "camera" block (CLI only; allocates)</summary>
        public Dictionary<string, object> State()
        {
            float r = Radius;
            return new Dictionary<string, object>
            {
                ["mode"] = !enabled ? "parked" : mode == Owner.Director ? "director" : "free",
                ["director"] = director != null ? director.DirectorState : "none",
                ["shot"] = director != null ? director.Shot : 0,
                ["blend"] = blending ? Mathf.Clamp01((Time.unscaledTime - blendStart) / Mathf.Max(1e-3f, blendLength))
                    : swingLook ? Mathf.Clamp01((Time.unscaledTime - swingStart) / Mathf.Max(1e-3f, TakeoverLookSeconds)) : 1f,
                ["blends"] = blends,
                ["eye"] = V(eye), ["look"] = V(look), ["eyeHeight"] = eyeH, ["lookHeight"] = lookH,
                ["azimuth"] = Azimuth * Mathf.Rad2Deg, ["elevation"] = 90f - Polar * Mathf.Rad2Deg, ["radius"] = r,
                ["distance"] = dist, ["freeAzimuth"] = phi * Mathf.Rad2Deg, ["fov"] = fov,
                ["raw"] = V(raw), ["lookAnchor"] = V(lookA), ["eyeAnchor"] = V(eyeA), ["lag"] = (lookA - raw).magnitude,
                ["settling"] = Blending || velLook.magnitude + velEye.magnitude > 1e-3f, ["cuts"] = cuts,
                ["following"] = hasRaw, ["playing"] = playing, ["scrubbing"] = scrubbing, ["tracing"] = tracing
            };
        }
    }
}
