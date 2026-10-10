using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VRTKLite.Controllers;
using Object = UnityEngine.Object;

/// <summary>
/// Plays a film direction (dancecap work/&lt;take&gt;/film/direction*.json, film/build_direction.py) in the viewer:
/// the FILM clock (its own: Time.deltaTime per rendered frame, never the song or the wall clock; INTERFACE.md section 1)
/// maps to the DANCE time through FilmClock (speeds, smooth slow-motion ramps, freezes, replays as cuts), and drives
/// - HeadMovement through its external clock (sub-frame pose blends, song muted: the soundtrack is one pre-mixed file);
/// - the camera (FilmCamera: orbit, cameras_overview, fly_to_camera, camera_tour, feet_closeup, overhead_floorcraft,
///   graph_wide, physics_3q, geometry_orbit, rhythm, end_card) on the parked desktop rig;
/// - the view-state layers per segment (avatars, skeleton mode, floor craft, counterbalance, traces, neck, physics,
///   balance, the dance graph isolated with its miniature couple and path, move caption, graph inset), with the
///   continuous ones (avatar opacity, the full-size dance, graph fade) blended across segment boundaries;
/// - the source phones (FilmCameraSource: frustum glyphs, the full-frame video at a phone's POV via the camera-video
///   feature FilmCameraVideo when installed, INTERFACE.md section 5);
/// - the screen overlays (FilmOverlay: title card, narration captions with the yellow current word, readouts, the zouk
///   beat counter, replay flash) and the 3D call-outs (Annotation3D: arrows, floor rings / arcs / wedges, turn arcs,
///   pull arrows, centre-of-mass markers with labels).
/// The recorder (Assets/Film/Editor/FilmRecorder.cs) calls Prepare / Begin by reflection; hm_film_show and the
/// "Head Movement/Film Preview" menu play it on the desktop with the soundtrack. Stop() hands everything back.
/// </summary>
[DefaultExecutionOrder(-50)]
[AddComponentMenu("")]
public class FilmDirector : MonoBehaviour
{
    public static FilmDirector Instance { get; private set; }

    /// <summary>the last static failure (Prepare / Begin returned null)</summary>
    public static string LastStaticError { get; private set; }

    public const float LayerBlendSeconds = 0.8f;

    FilmDirection direction;
    FilmClock clock;
    FilmTargets targets;
    FilmCameraSource sources;
    FilmCamera filmCamera;
    FilmOverlay overlay;
    Annotation3D annotations;
    HeadMovement hm;
    string directionPath, aspect = "horizontal";
    bool running, justBegan, stopped, finished, sourcesPrepared, loadRequested, controlTaken;
    float filmTime;
    int appliedFrames;
    float createdReal;
    string lastError;

    // desktop preview: the director plays the pre-mixed soundtrack itself (a recording plays it from the recorder)
    bool playSoundtrack, soundtrackStarted;

    // camera rig (parked while the film runs) and the render camera's lens
    CameraControl rig;
    bool rigWasEnabled;
    Camera cam;
    float camFov = -1f;
    bool camPhysical;
    Vector2 camShift;
    float camNear = -1f;
    Vector3 camLocalPos;
    Quaternion camLocalRot = Quaternion.identity;
    Transform camParent;
    float nextLoadTry;
    object layersKey;

    // the floor-craft overlay's T look (restored on stop)
    bool tLookSaved;
    float savedTWidth, savedOldTWidth, savedTBrightness;
    FloorCraftOverlay tLookOwner;

    // layers we change, with the values to restore
    static readonly string[] HeadLayers = { "floor", "grid", "timing", "physics", "tension", "avatars", "hud" };
    readonly Dictionary<string, bool> savedHead = new();
    readonly Dictionary<LineRenderer, (float width, bool on)> savedWidth = new();
    readonly List<LineRenderer> lineBuffer = new();
    List<LineRenderer> contactLines;
    FloorPatterns.FootprintMode savedFootprints = FloorPatterns.FootprintMode.Recent;
    int appliedSegment = -1;
    SegLayers[] segLayers;
    float skeletonNow = 1f, avatarNow = -1f, graphNow = -1f, contactsNow = -1f;
    bool yawOverridden;
    bool videoShown;

    /// <summary>layer values of one segment (view-state defaults + the segment's "layers")</summary>
    public class SegLayers
    {
        public bool AvatarsFadeWithVideo; // layer "avatars_fade_with_video": the 3D dancers fade out as the phone's video fades in (the class recap's POV shots)
        public float Avatar = 0.35f, Skeleton = 1f, Graph, Glyphs, Contacts = 1f; // Contacts: the hand-contact lights (layer "hand_contacts")
        public bool Physics, Floor, Timing, Tension, Counterbalance, Traces, Neck, Axis, Record, AllPivots, Moves = true, Inset, BeatCounter;
        public bool Grid = true;                                        // the floor's cross grid + plane (layer "grid")
        public bool FcLabelsCurrent;                                    // layer "floorcraft_labels": "current" = only the running dial keeps its number
        public float TWidth = float.NaN, OldTWidth = float.NaN, TBrightness = float.NaN; // the leader's T on the floor (m / 0..1)
        public DanceGraphLayer.Mode GraphMode = DanceGraphLayer.Mode.Off;
        public FloorPatterns.FootprintMode Footprints = FloorPatterns.FootprintMode.Recent;
    }

    // ------------------------------------------------------------------ INTERFACE.md section 1

    public bool IsReady
    {
        get
        {
            if (stopped || direction == null || !Loaded) return false;
            if (!sourcesPrepared || !sources.Ready) return false;
            return appliedFrames >= 3;
        }
    }

    public bool IsFinished => finished || stopped;
    public float FilmTime => filmTime;
    public float FilmDuration => direction != null ? direction.FilmDuration : 0f;
    public float DanceTime { get; private set; } = float.NaN;
    public string SegmentId { get; private set; }
    public string LastError => lastError;

    // extras (CLI, overlays, report)
    public FilmDirection Direction => direction;
    public FilmClock Clock => clock;
    public FilmTargets Targets => targets;
    public FilmCamera FilmCam => filmCamera;
    public FilmCameraSource Sources => sources;
    public Annotation3D Annotations => annotations;
    public FilmOverlay Overlay => overlay;
    public string Aspect => aspect;
    public bool Vertical => aspect == "vertical";
    public FilmAspect AspectInfo => direction?.Aspect(aspect) ?? new FilmAspect();
    public bool Running => running;
    public bool Paused { get; private set; }
    public float Speed { get; private set; } = 1f;
    public int SegmentIndex { get; private set; } = -1;
    public FilmSegment Segment => direction != null && SegmentIndex >= 0 && SegmentIndex < direction.Segments.Count ? direction.Segments[SegmentIndex] : null;
    public string DirectionPath => directionPath;
    public string MixPath => direction != null ? Path.Combine(direction.Dir, "audio", direction.Name + "_mix.wav") : null;
    public readonly List<string> Warnings = new();

    /// <summary>playback rate of the film clock (the viewer's playback bar, speed button); 1 = real time. The recorder never changes it.</summary>
    public float Rate { get; set; } = 1f;

    /// <summary>true between BeginScrub and EndScrub: the playback bar's scrubber is being dragged, time and audio are held</summary>
    public bool Scrubbing { get; private set; }

    bool scrubWasPaused;

    bool Loaded => hm != null && hm.AudioLoaded && hm.Timeline != null && hm.LeadDancer != null && hm.FollowDancer != null && CaptureMatches();

    bool CaptureMatches() =>
        hm.Manifest != null && string.Equals(Path.GetFileName(hm.Manifest.Folder?.TrimEnd('/', '\\') ?? ""), direction.Capture, StringComparison.OrdinalIgnoreCase);

    /// <summary>load the direction, its narration timeline and the capture's data (the capture is loaded when it is not
    /// the one shown; INTERFACE.md: never reloaded when it is) and show film frame 0 paused. Null (logged) on error.</summary>
    public static FilmDirector Prepare(string directionPath, string aspect)
    {
        try
        {
            if (string.IsNullOrEmpty(directionPath)) throw new ArgumentException("no direction path");
            string full = Path.GetFullPath(directionPath);
            string asp = FilmAspect.Normalise(aspect);
            if (Instance != null && !Instance.stopped && string.Equals(Instance.directionPath, full, StringComparison.OrdinalIgnoreCase))
            {
                if (Instance.aspect != asp) Instance.SetAspect(asp);
                return Instance;
            }

            if (Instance != null) Instance.Stop();
            HeadMovement head = HeadMovement.Instance != null ? HeadMovement.Instance : Object.FindAnyObjectByType<HeadMovement>();
            if (head == null) throw new InvalidOperationException("no HeadMovement in the scene (enter Play mode in head-movement.unity)");
            FilmDirection d = FilmDirection.Load(full);
            if (d.Segments.Count == 0) throw new InvalidOperationException($"{Path.GetFileName(full)} has no segments");
            if (string.IsNullOrEmpty(d.Capture)) throw new InvalidOperationException($"{Path.GetFileName(full)} names no capture");
            GameObject go = new("Film Director");
            FilmDirector fd = go.AddComponent<FilmDirector>();
            fd.Init(head, d, full, asp);
            LastStaticError = null;
            return fd;
        }
        catch (Exception e)
        {
            LastStaticError = e.Message;
            Debug.LogError($"FilmDirector.Prepare: {e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    public static FilmDirector Begin(string directionPath, string aspect) => Begin(directionPath, aspect, 0f);

    /// <summary>start the film at startFilmTime: the first frame rendered after this call shows it, every later frame
    /// advances by Time.deltaTime</summary>
    public static FilmDirector Begin(string directionPath, string aspect, float startFilmTime)
    {
        FilmDirector fd = Prepare(directionPath, aspect);
        if (fd == null) return null;
        fd.StartAt(startFilmTime);
        return fd;
    }

    /// <summary>desktop preview (not recorded): Begin + the pre-mixed soundtrack on the film clock</summary>
    public static FilmDirector Show(string directionPath, string aspect, float startFilmTime)
    {
        FilmDirector fd = Begin(directionPath, aspect, startFilmTime);
        if (fd == null) return null;
        fd.playSoundtrack = true;
        fd.soundtrackStarted = false;
        FilmPlaybackBar.Attach(fd); // the viewer's playback bar: desktop sessions only (the recorder starts films through Prepare / Begin)
        return fd;
    }

    void Init(HeadMovement head, FilmDirection d, string path, string asp)
    {
        Instance = this;
        hm = head;
        direction = d;
        directionPath = path;
        aspect = asp;
        createdReal = Time.realtimeSinceStartup;
        Warnings.AddRange(d.Warnings);
        clock = new FilmClock(d);
        targets = new FilmTargets(hm);
        sources = new FilmCameraSource { Warn = w => { if (!Warnings.Contains(w)) Warnings.Add(w); } };
        filmCamera = new FilmCamera(d, clock, targets, sources) { Vertical = Vertical };
        overlay = gameObject.AddComponent<FilmOverlay>();
        overlay.Init(this);
        annotations = gameObject.AddComponent<Annotation3D>();
        annotations.Init(this, overlay.LabelRoot);
        gameObject.AddComponent<FilmLate>().Director = this;
        segLayers = d.Segments.Select(ReadLayers).ToArray();
        if (!CaptureMatches())
        {
            loadRequested = true;
            nextLoadTry = Time.realtimeSinceStartup + 2f;
            hm.LoadCapture(d.Capture); // retried from Update while HeadMovement is starting up
        }
    }

    void SetAspect(string asp)
    {
        aspect = asp;
        if (filmCamera != null) filmCamera.Vertical = Vertical;
        overlay?.OnAspectChanged();
        annotations?.OnAspectChanged();
    }

    void StartAt(float start)
    {
        filmTime = Mathf.Clamp(float.IsFinite(start) ? start : 0f, 0f, FilmDuration);
        running = true;
        justBegan = true;
        finished = false;
        Paused = false;
    }

    void Fail(string message)
    {
        lastError = message;
        Debug.LogError($"FilmDirector: {message}");
    }

    public void Stop()
    {
        if (stopped) return;
        stopped = true;
        running = false;
        if (playSoundtrack) FilmSoundtrack.StopAll();
        ReleaseControl();
        if (sources != null) sources.Destroy();
        if (Instance == this) Instance = null;
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (!stopped)
        {
            stopped = true;
            ReleaseControl();
            sources?.Destroy();
        }

        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ transport (CLI / preview)

    /// <summary>show film time t (keeps running or paused as it was). Everything on screen is a pure function of the film time
    /// (the segment, the dance time, camera, layers, role fades, captions, call-outs, the phones' frames), so only the pre-mixed
    /// soundtrack needs telling: it is moved to t at once (held, when the film is paused).</summary>
    public void Seek(float t)
    {
        filmTime = Mathf.Clamp(float.IsFinite(t) ? t : 0f, 0f, FilmDuration);
        finished = false;
        justBegan = true; // the next frame shows exactly t
        if (playSoundtrack && FilmSoundtrack.Current != null) FilmSoundtrack.Current.SeekTo(filmTime);
    }

    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        if (!playSoundtrack) return;
        FilmSoundtrack st = FilmSoundtrack.Current;
        if (st == null)
        {
            soundtrackStarted = false; // not started yet (still loading): Update starts it at the clock's time
            return;
        }

        if (paused)
        {
            st.SetHold(true);
        }
        else
        {
            st.SeekTo(filmTime);
            st.SetHold(false);
        }
    }

    /// <summary>the scrubber is grabbed: the film holds (audio too) and follows Seek calls; EndScrub gives it back as it was</summary>
    public void BeginScrub()
    {
        if (Scrubbing) return;
        Scrubbing = true;
        scrubWasPaused = Paused;
        SetPaused(true);
    }

    public void EndScrub()
    {
        if (!Scrubbing) return;
        Scrubbing = false;
        if (!scrubWasPaused) SetPaused(false);
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        if (stopped) return;
        if (!Loaded)
        {
            // the capture is loaded by the recorder (or by Init); retry while HeadMovement is still starting up
            if (hm != null && !CaptureMatches() && Time.realtimeSinceStartup >= nextLoadTry)
            {
                nextLoadTry = Time.realtimeSinceStartup + 2f;
                loadRequested = true;
                if (!hm.LoadCapture(direction.Capture) && Time.realtimeSinceStartup - createdReal > 30f)
                {
                    Fail($"HeadMovement has no capture '{direction.Capture}' in StreamingAssets");
                }
            }

            return;
        }

        if (!controlTaken) TakeControl();
        if (!sourcesPrepared)
        {
            sources.Prepare(hm.Manifest.Folder, direction, transform);
            sourcesPrepared = true;
            if (!sources.HasFeature) Warnings.Add($"no FilmCameraVideo: phone glyphs + POV flights from {sources.Status}; no source video");
        }

        if (running && !Paused)
        {
            if (justBegan) justBegan = false;
            else filmTime += Time.deltaTime * Rate;
        }
        else if (justBegan)
        {
            justBegan = false;
        }

        if (filmTime >= FilmDuration) filmTime = FilmDuration;
        Apply(filmTime);
        if (running && filmTime >= FilmDuration) finished = true;

        if (playSoundtrack && FilmSoundtrack.Current != null && !Mathf.Approximately(FilmSoundtrack.Current.Pitch, Rate)) FilmSoundtrack.Current.Pitch = Rate;
        if (playSoundtrack && running && !Paused && !soundtrackStarted)
        {
            string mix = MixPath;
            if (mix != null && File.Exists(mix))
            {
                // 25 fps frames and 20 ms audio buffers make the player's position jitter by about 40 ms: re-sync at 80 ms
                FilmSoundtrack.Play(mix, () => FilmTime).ResyncThreshold = 0.08f;
            }
            else
            {
                Warnings.Add($"no soundtrack {mix} (film/audio/make_film_mix.py)");
            }

            soundtrackStarted = true;
        }
    }

    /// <summary>called by FilmLate after every other LateUpdate (DanceLayers, the graph, the HUD): screen overlays and
    /// 3D call-outs for the frame being rendered</summary>
    public void LateTick()
    {
        if (stopped || !Loaded || appliedFrames == 0) return;
        Camera c = cam != null ? cam : DanceText.ViewCamera;
        overlay.Tick(filmTime, DanceTime);
        annotations.Tick(filmTime, DanceTime, c);
    }

    void Apply(float film)
    {
        float dance = clock.Dance(film);
        CaptureTimeline tl = hm.Timeline;
        double first = tl.AudioTimes[0] - tl.TimeToAudio, last = tl.AudioTimes[tl.AudioTimes.Length - 1] - tl.TimeToAudio;
        dance = Mathf.Clamp(dance, (float)first, (float)last);
        DanceTime = dance;
        int si = direction.SegmentIndexAt(film);
        SegmentIndex = si;
        FilmSegment seg = direction.Segments[si];
        SegmentId = seg.Id;
        Speed = clock.Speed(film);
        hm.DriveExternally(dance + tl.TimeToAudio);

        ApplyLayers(film, si);

        if (cam == null) cam = DanceText.ViewCamera;
        // the orbit rig stays parked for the whole film: the editor CLI commands of the other tools (hm_orbit, hm_hair, hm_shoes,
        // hm_spine, ...) "un-park" it, and an enabled CameraControl rewrites the camera's FOV in every LateUpdate (measured
        // 2026-10-08: three excerpt recordings had the POV shot at 60 deg instead of the phone's 79.85 -> the picture 1.45x too
        // big, video and 3D together; the film's POV shots are only correct when the render camera IS the phone)
        if (rig != null && rig.enabled) rig.enabled = false;
        // the camera must follow its target: a stale custom aspect (measured 2026-10-08: 16:9 on a 1080x1920 target after a
        // 16:9 recording in the same editor session) squeezes the whole 3D picture to a third of its width
        if (cam != null && cam.pixelHeight > 0 && Mathf.Abs(cam.aspect - (float)cam.pixelWidth / cam.pixelHeight) > 0.02f) cam.ResetAspect();
        filmCamera.Vertical = Vertical;
        FilmPose pose = filmCamera.Evaluate(film);
        if (cam != null) FilmCamera.Apply(cam.transform, cam, pose); // the rig is parked; its camera's local pose is restored on stop
        if (cam != null)
        {
            // near plane: close-ups at the feet and the flight into a phone's lens
            float near = filmCamera.Current != null && (filmCamera.Current.Mode is "feet_closeup" or "fly_to_camera" or "camera_tour") ? 0.03f : camNear;
            if (near > 0 && Mathf.Abs(cam.nearClipPlane - near) > 1e-4f) cam.nearClipPlane = near;
        }

        // the phones: glyphs from above, the full-frame video at a phone's POV
        float glyphs = LayerValue(film, si, l => l.Glyphs);
        sources.SetGlyphs(glyphs, dance, cam);
        string pov = filmCamera.PovCamera;
        if (pov != null && filmCamera.PovVideoOpacity > 0.001f)
        {
            sources.SetVideo(pov, filmCamera.PovVideoOpacity, dance, Speed, filmCamera.PovComposite);
            videoShown = true;
        }
        else if (videoShown)
        {
            sources.SetVideo(null, 0f, dance, Speed, null);
            videoShown = false;
        }

        // a phone shot whose layers say so: the 3D dancers are there while the camera flies to the phone and fade out as its video fades in (a cut
        // to the phone has them gone at once); without the video (an outage) they stay, so the shot is never empty
        if (segLayers[si].AvatarsFadeWithVideo)
        {
            float fade = pov != null && sources.VideoShowing ? Mathf.Clamp01(filmCamera.PovVideoOpacity) : 0f;
            float a = LayerValue(film, si, x => x.Avatar) * Mathf.Clamp01(LayerValue(film, si, x => x.Skeleton)) * (1f - fade);
            if (Mathf.Abs(a - avatarNow) > 1e-3f)
            {
                avatarNow = a;
                hm.SetDirectorAvatarOpacity(a);
            }
        }

        appliedFrames++;
    }

    // ------------------------------------------------------------------ layers

    SegLayers ReadLayers(FilmSegment s)
    {
        string view = (s.ViewState ?? "orbit").ToLowerInvariant();
        SegLayers l = new();
        bool graphView = view is "dance_graph" or "fingerprint";
        float factor = view switch
        {
            "orbit" or "camera_tour" => 1f,
            "overhead" => 2f / 3f,
            "geometry" => 0.5f,
            "physics" => 1f / 3f,
            _ => 0f
        };
        l.Avatar = s.LayerFloat("avatars", HeadMovement.DefaultAvatarOpacity * factor);
        l.AvatarsFadeWithVideo = s.LayerBool("avatars_fade_with_video", false);
        l.Skeleton = Mathf.Clamp01(s.LayerFloat("dance_full_size", graphView ? 0f : 1f));
        l.Physics = s.LayerString("skeleton_mode", "") == "physics" || s.LayerBool("physics", view == "physics");
        string footprints = s.LayerString("footprints", null);
        l.Floor = s.LayerBool("floor", false) || footprints is "recent" or "all";
        l.Grid = s.LayerBool("grid", true);
        l.FcLabelsCurrent = s.LayerString("floorcraft_labels", "all") == "current";
        l.TWidth = s.LayerFloat("t_width_m", float.NaN);
        l.OldTWidth = s.LayerFloat("old_t_width_m", float.NaN);
        l.TBrightness = s.LayerFloat("t_brightness", float.NaN);
        l.Footprints = footprints == "all" ? FloorPatterns.FootprintMode.All : FloorPatterns.FootprintMode.Recent;
        l.Timing = footprints is "touchdown_rings_neutral" or "touchdown_rings";
        l.Tension = view is "orbit" or "physics" && !graphView;
        l.Counterbalance = s.LayerBool("counterbalance", false) && !graphView;
        l.Traces = s.LayerBool("traces", view == "geometry") && !graphView;
        l.Neck = s.LayerBool("neck", false) && !graphView;
        string fc = s.Layers["floorcraft"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean
            ? s.LayerBool("floorcraft", false) ? "full_record" : "off"
            : s.LayerString("floorcraft", "off");
        l.Axis = fc is "current_t" or "full_record" or "on";
        l.Record = fc is "full_record" or "on";
        l.AllPivots = l.Record;
        l.Moves = s.LayerBool("moves", true);
        l.Inset = s.LayerBool("graph_inset", false) && !graphView;
        string graph = s.LayerString("graph", graphView ? "full" : "off");
        l.GraphMode = graph is "full" or "path" ? DanceGraphLayer.Mode.Path : graph == "fingerprint" ? DanceGraphLayer.Mode.Fingerprint
            : graph == "static" ? DanceGraphLayer.Mode.Static : DanceGraphLayer.Mode.Off;
        l.Graph = l.GraphMode != DanceGraphLayer.Mode.Off ? 1f : 0f;
        l.Glyphs = s.Layers["camera_glyphs"] != null && s.LayerString("camera_glyphs", "on") != "off" ? 1f : 0f;
        l.BeatCounter = s.LayerBool("beat_counter", false);
        // the hand-contact lights belong to the full-size dance: hidden in the graph view states (user 2026-10-08), else shown
        l.Contacts = s.LayerBool("hand_contacts", !graphView) ? 1f : 0f;
        return l;
    }

    public SegLayers LayersOf(int segment) => segLayers != null && segment >= 0 && segment < segLayers.Length ? segLayers[segment] : null;

    /// <summary>a continuous layer value at film time f: blended from the previous segment's over LayerBlendSeconds
    /// (cuts switch at once)</summary>
    float LayerValue(float film, int si, Func<SegLayers, float> get)
    {
        FilmSegment s = direction.Segments[si];
        float v = get(segLayers[si]);
        if (si == 0) return v;
        bool cut = Mathf.Abs(direction.Segments[si - 1].D1 - s.D0) > FilmClock.CutJump;
        if (cut) return v;
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((film - s.F0) / LayerBlendSeconds));
        return Mathf.Lerp(get(segLayers[si - 1]), v, k);
    }

    void TakeControl()
    {
        controlTaken = true;
        DanceLayers layers = DanceLayers.Ensure();
        if (layers != null && layers.Tour != null && layers.Tour.Active) layers.Tour.Stop(); // the tour restores its own layers
        if (layers != null && layers.Hud != null) layers.Hud.FilmMode = true; // the move caption: the name only, no debug badge
        savedHead.Clear();
        foreach (string layer in HeadLayers) savedHead[layer] = hm.LayerVisible(layer);
        if (hm.Footprints != null) savedFootprints = hm.Footprints.Mode;
        rig = hm.OrbitCamera;
        cam = DanceText.ViewCamera;
        if (rig != null)
        {
            rigWasEnabled = rig.enabled;
            rig.enabled = false; // parked: nothing else writes the camera while the film runs
        }

        if (cam != null)
        {
            camFov = cam.fieldOfView;
            camPhysical = cam.usePhysicalProperties;
            camShift = cam.lensShift;
            camNear = cam.nearClipPlane;
            camParent = cam.transform.parent;
            camLocalPos = cam.transform.localPosition;
            camLocalRot = cam.transform.localRotation;
        }

        appliedSegment = -1;
        avatarNow = graphNow = -1f;
        skeletonNow = 1f;
        contactsNow = -1f;
        ApplyFloorYaw();
    }

    void ReleaseControl()
    {
        if (!controlTaken) return;
        controlTaken = false;
        if (hm != null)
        {
            hm.ReleaseExternal();
            foreach (KeyValuePair<string, bool> kv in savedHead)
            {
                try
                {
                    if (hm.LayerVisible(kv.Key) != kv.Value) hm.SetLayerVisible(kv.Key, kv.Value);
                }
                catch (ArgumentException)
                {
                    // layer gone
                }
            }

            hm.SetDirectorAvatarOpacity(null);
            hm.GetComponent<ContactDetection>()?.SetOrbFade(1f);
            if (yawOverridden)
            {
                yawOverridden = false;
                hm.SetFloorYaw(hm.Manifest != null ? hm.Manifest.floor_yaw_deg : 0f); // back to the capture's own
            }

            if (hm.Footprints != null) hm.Footprints.SetMode(savedFootprints);
        }

        foreach (KeyValuePair<LineRenderer, (float width, bool on)> kv in savedWidth)
        {
            if (kv.Key == null) continue;
            kv.Key.widthMultiplier = kv.Value.width;
            kv.Key.enabled = kv.Value.on;
        }

        savedWidth.Clear();
        DanceLayers layers = DanceLayers.Instance;
        if (layers != null)
        {
            layers.CounterbalanceOverride = null;
            layers.TracesOverride = null;
            layers.NeckOverride = null;
            layers.AxisOverride = null;
            layers.FloorcraftOverride = null;
            layers.GraphOverride = null;
            layers.MovesOverride = null;
            layers.InsetOverride = null;
            layers.ShowAllPivots = false;
            layers.ApplyVisibility();
            if (tLookSaved && tLookOwner != null)
            {
                tLookOwner.TWidth = savedTWidth;
                tLookOwner.OldTWidth = savedOldTWidth;
                tLookOwner.TBrightness = savedTBrightness;
                tLookOwner.LabelsOnlyCurrent = false;
            }

            tLookSaved = false;
            tLookOwner = null;
            if (layers.Graph != null) layers.Graph.SetFade(1f);
            if (layers.Hud != null) layers.Hud.FilmMode = false;
        }

        if (overlay != null) overlay.Restore();
        if (cam != null)
        {
            if (camFov > 0) cam.fieldOfView = camFov;
            cam.usePhysicalProperties = camPhysical;
            cam.lensShift = camShift;
            if (camNear > 0) cam.nearClipPlane = camNear;
            if (camParent != null && cam.transform.parent == camParent)
            {
                cam.transform.localPosition = camLocalPos;
                cam.transform.localRotation = camLocalRot;
            }
        }

        if (rig != null)
        {
            rig.enabled = rigWasEnabled;
            if (rig.enabled) rig.SnapFollow();
        }
    }

    void ApplyLayers(float film, int si)
    {
        DanceLayers layers = DanceLayers.Instance;
        SegLayers l = segLayers[si];
        object key = layers != null ? (object)layers.Graph ?? layers.Counterbalance ?? (object)layers : null;
        if (!ReferenceEquals(key, layersKey))
        {
            // DanceLayers rebuilt its layers (capture loaded): apply everything again
            layersKey = key;
            appliedSegment = -1;
            graphNow = avatarNow = skeletonNow = contactsNow = -1f;
            ApplyFloorYaw();
        }

        if (savedWidth.Count > 0 && savedWidth.Keys.Any(x => x == null))
        {
            // the dancers were recreated: their lines start from their own widths again
            savedWidth.Clear();
            contactLines = null;
            skeletonNow = -1f;
        }

        if (si != appliedSegment)
        {
            appliedSegment = si;
            SetHead("floor", l.Floor);
            SetHead("grid", l.Grid);
            SetHead("timing", l.Timing);
            SetHead("physics", l.Physics);
            SetHead("tension", l.Tension);
            SetHead("avatars", true);
            SetHead("hud", false);
            if (hm.Footprints != null && hm.Footprints.Mode != l.Footprints) hm.Footprints.SetMode(l.Footprints);
            if (layers != null)
            {
                layers.CounterbalanceOverride = l.Counterbalance;
                layers.TracesOverride = l.Traces;
                layers.NeckOverride = l.Neck;
                layers.AxisOverride = l.Axis;
                layers.FloorcraftOverride = l.Record;
                layers.ShowAllPivots = l.AllPivots;
                layers.GraphOverride = l.GraphMode;
                layers.MovesOverride = l.Moves;
                layers.InsetOverride = l.Inset;
                layers.ApplyVisibility();
                ApplyFloorCraftLook(layers.FloorCraft, l);
            }
        }

        float avatar = LayerValue(film, si, x => x.Avatar);
        float skeleton = LayerValue(film, si, x => x.Skeleton);
        float graph = LayerValue(film, si, x => x.Graph);
        // the full-size dance fades with the graph view: avatar opacity scales with the dance's size factor
        avatar *= Mathf.Clamp01(skeleton * 1.0f);
        if (!segLayers[si].AvatarsFadeWithVideo && Mathf.Abs(avatar - avatarNow) > 1e-3f)
        {
            avatarNow = avatar;
            hm.SetDirectorAvatarOpacity(avatar);
        }

        if (Mathf.Abs(skeleton - skeletonNow) > 1e-3f) SetSkeletonWidth(skeleton);
        float contacts = LayerValue(film, si, x => x.Contacts);
        if (Mathf.Abs(contacts - contactsNow) > 1e-3f)
        {
            contactsNow = contacts;
            hm.GetComponent<ContactDetection>()?.SetOrbFade(contacts);
        }

        if (layers?.Graph != null && Mathf.Abs(graph - graphNow) > 1e-3f)
        {
            graphNow = graph;
            layers.Graph.SetFade(Mathf.Max(0.02f, graph));
        }
    }

    /// <summary>the floor visuals' yaw: the capture's own (capture.json floor_yaw_deg, applied by HeadMovement) unless the direction
    /// carries a top-level "floor_yaw_deg" (degrees counter-clockwise seen from above); restored when the film stops</summary>
    void ApplyFloorYaw()
    {
        Newtonsoft.Json.Linq.JToken v = direction?.Root?["floor_yaw_deg"];
        if (hm == null || v == null || (v.Type != Newtonsoft.Json.Linq.JTokenType.Float && v.Type != Newtonsoft.Json.Linq.JTokenType.Integer)) return;
        float want = (float)v;
        yawOverridden = true;
        if (!Mathf.Approximately(hm.FloorYawDeg, want)) hm.SetFloorYaw(want);
    }

    /// <summary>a segment's own T look (thicker / brighter for the overhead floor-craft shot); NaN keeps the viewer's value</summary>
    void ApplyFloorCraftLook(FloorCraftOverlay fc, SegLayers l)
    {
        if (fc == null) return;
        if (!tLookSaved || !ReferenceEquals(tLookOwner, fc))
        {
            tLookOwner = fc;
            tLookSaved = true;
            savedTWidth = fc.TWidth;
            savedOldTWidth = fc.OldTWidth;
            savedTBrightness = fc.TBrightness;
        }

        fc.TWidth = float.IsNaN(l.TWidth) ? savedTWidth : l.TWidth;
        fc.OldTWidth = float.IsNaN(l.OldTWidth) ? savedOldTWidth : l.OldTWidth;
        fc.TBrightness = float.IsNaN(l.TBrightness) ? savedTBrightness : l.TBrightness;
        fc.LabelsOnlyCurrent = l.FcLabelsCurrent;
    }

    void SetHead(string layer, bool on)
    {
        try
        {
            if (hm.LayerVisible(layer) != on) hm.SetLayerVisible(layer, on);
        }
        catch (ArgumentException)
        {
            // a layer this HeadMovement does not have
        }
    }

    /// <summary>the full-size dance's skeleton lines (and ContactDetection's contact lines) scaled by k (the graph
    /// view isolates the state machine); spine beads follow the skeleton's width</summary>
    void SetSkeletonWidth(float k)
    {
        skeletonNow = k;
        WidthOf(hm.LeadDancer, k);
        WidthOf(hm.FollowDancer, k);
        if (contactLines == null)
        {
            contactLines = new List<LineRenderer>();
            ContactDetection contacts = hm.GetComponent<ContactDetection>();
            if (contacts != null)
            {
                foreach (System.Reflection.FieldInfo field in typeof(ContactDetection).GetFields(
                             System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                {
                    if (field.FieldType == typeof(LineRenderer) && field.GetValue(contacts) is LineRenderer line && line != null) contactLines.Add(line);
                }
            }
        }

        foreach (LineRenderer line in contactLines) Scale(line, k);
    }

    void WidthOf(Dancer d, float k)
    {
        if (d == null) return;
        d.GetComponentsInChildren(true, lineBuffer);
        foreach (LineRenderer line in lineBuffer) Scale(line, k);
    }

    /// <summary>a line at k x its own width (hidden near 0); lines are only touched once the dance shrinks</summary>
    void Scale(LineRenderer line, float k)
    {
        if (line == null) return;
        if (!savedWidth.TryGetValue(line, out (float width, bool on) w))
        {
            if (k >= 0.999f) return;
            w = (line.widthMultiplier, line.enabled);
            savedWidth[line] = w;
        }

        line.widthMultiplier = w.width * k;
        line.enabled = k > 0.01f && w.on;
    }

    // ------------------------------------------------------------------ state (hm_film_show)

    public Dictionary<string, object> State()
    {
        FilmSegment seg = Segment;
        FilmShot shot = filmCamera?.Current;
        Dictionary<string, object> s = new()
        {
            ["direction"] = directionPath, ["name"] = direction?.Name, ["capture"] = direction?.Capture, ["aspect"] = aspect,
            ["screen"] = $"{Screen.width}x{Screen.height}", ["ready"] = IsReady, ["running"] = running, ["paused"] = Paused,
            ["finished"] = IsFinished, ["filmTime"] = Math.Round(filmTime, 3), ["filmDuration"] = FilmDuration,
            ["danceTime"] = float.IsNaN(DanceTime) ? null : Math.Round(DanceTime, 3), ["speed"] = Math.Round(Speed, 3),
            ["segment"] = seg?.Id, ["viewState"] = seg?.ViewState, ["cameraMode"] = shot?.Mode, ["shot"] = shot?.Index,
            ["pov"] = filmCamera?.PovCamera, ["povVideo"] = filmCamera != null ? Math.Round(filmCamera.PovVideoOpacity, 3) : 0,
            ["sources"] = sources?.Status, ["videoFeature"] = sources != null && sources.HasVideo, ["videoShowing"] = sources != null && sources.VideoShowing,
            ["narration"] = direction?.Narration?.Path, ["mix"] = MixPath, ["soundtrack"] = playSoundtrack ? FilmSoundtrack.Current?.Describe() : "recorder",
            ["avatar"] = Math.Round(avatarNow, 3), ["skeleton"] = Math.Round(skeletonNow, 3), ["graphFade"] = Math.Round(graphNow, 3),
            ["contacts"] = Math.Round(contactsNow, 3), ["floorYawDeg"] = hm != null ? Math.Round(hm.FloorYawDeg, 3) : 0.0,
            ["externallyDriven"] = hm != null && hm.ExternallyDriven, ["frame"] = hm != null ? hm.CurrentFrame : -1,
            ["rate"] = Rate, ["scrubbing"] = Scrubbing, ["audio"] = AudioState(),
            ["error"] = lastError, ["warnings"] = Warnings.Distinct().ToList(),
            ["skippedAnnotations"] = direction != null ? direction.SkippedAnnotations.ToList() : null
        };
        if (overlay != null) s["overlay"] = overlay.State();
        if (annotations != null) s["annotations"] = annotations.State();
        if (cam != null)
        {
            s["camera"] = new Dictionary<string, object>
            {
                ["pos"] = V(cam.transform.position), ["fwd"] = V(cam.transform.forward), ["fov"] = Math.Round(cam.fieldOfView, 2)
            };
        }

        return s;
    }

    /// <summary>the desktop soundtrack's player (null when the film is recorded or has no soundtrack): where it is against the film clock</summary>
    Dictionary<string, object> AudioState()
    {
        FilmSoundtrack st = playSoundtrack ? FilmSoundtrack.Current : null;
        if (st == null) return null;
        return new Dictionary<string, object>
        {
            ["loaded"] = st.IsLoaded, ["playing"] = st.IsPlaying, ["held"] = st.Held, ["time"] = Math.Round(st.AudioTime, 3), ["resyncs"] = st.Resyncs,
            ["pitch"] = Math.Round(st.Pitch, 3), ["driftS"] = st.IsPlaying ? Math.Round(st.AudioTime - filmTime, 3) : (double?)null
        };
    }

    static float[] V(Vector3 v) => new[] { (float)Math.Round(v.x, 3), (float)Math.Round(v.y, 3), (float)Math.Round(v.z, 3) };
}

/// <summary>the director's late pass: after every other LateUpdate (DanceLayers, the dance graph and its HUD), so the
/// screen overlays and 3D call-outs see the final frame state</summary>
[DefaultExecutionOrder(1000)]
[AddComponentMenu("")]
public class FilmLate : MonoBehaviour
{
    public FilmDirector Director;

    void LateUpdate()
    {
        if (Director != null) Director.LateTick();
    }
}
