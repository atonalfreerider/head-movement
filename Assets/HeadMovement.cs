using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using VRTKLite.Controllers;

[RequireComponent(typeof(ContactDetection))]
public class HeadMovement : MonoBehaviour
{
    static readonly string assetPath = Application.streamingAssetsPath;

    public static HeadMovement Instance;

    int FrameCount = -1;
    float Fps = 30;
    float AudioOffset;

    /// <summary>audio time of every pose frame (times.json for v3 captures, else a uniform fps grid)</summary>
    CaptureTimeline timeline;

    Dancer Lead;
    Dancer Follow;

    // v3: skinned SMPL-X bodies and analysis overlays (only when the capture provides the files)
    readonly Dictionary<Role, SmplxAvatar> avatars = new();
    TimingOverlay timingOverlay;
    PhysicsOverlay physicsOverlay;
    TimingData timingData;
    readonly List<string> loadWarnings = new();

    ContactDetection contactDetection;
    FloorPatterns floorPatterns;
    FloorGrid floorGrid;
    SkeletonStyle skeletonStyle;
    SkeletonLegend skeletonLegend;
    PhysicsData physicsData;
    PartnerConnection partnerConnection;
    SplatCloud splatCloud;
    RoomMesh roomMesh;
    VirtualCameraRig virtualCameraRig;

    public Material BloomMaterial;

    CameraControl cameraControl;

    AudioSource audioSource;
    bool audioLoaded = false;
    bool playing = false;
    int currentFolder = -1;

    CaptureManifest manifest;
    BeatGrid beatGrid;
    Dictionary<int, float> beatIntensityByFrame;
    int currentFrame = -1;

    // lesson transport
    static readonly float[] Speeds = { 0.5f, 0.75f, 1f };
    int speedIndex = Speeds.Length - 1;
    bool loopMeasure;
    int loopedMeasure;
    // "tension" draws nothing since 2026-10-07 (user: no extra lines for tension and pressure; PartnerConnection feeds the
    // physics skeleton colours); "physics" = the PHYSICS view mode (skeletons coloured by the estimated load + legend + COM /
    // XCoM / support markers), off by default: the default is the RHYTHM mode (skeletons pulse with the beat, SkeletonStyle)
    bool showFloor = true, showConnection = true, showCameras = false, showHud = true, showGrid = true;
    bool showAvatars = true, showTiming = true, showPhysics;

    // VIEWER_SPEC 3.2a: splats show the dancers only and start off; the room reconstruction is not shown by default
    // (M / hm_layer room still toggle it for QA; the tour never turns it on)
    bool showSplats, showRoom;

    /// <summary>VIEWER_SPEC 3.2: avatars are 65 % transparent AS DISPLAYED by default (the skeleton and the partner read
    /// through the body). Opacity = what the screen shows (hm_opacity --probe: background contribution through one body
    /// layer, Game-view pipeline); the colour pass looks the alpha up per pixel from the shaded colour (SmplxAvatar.AlphaMode,
    /// DisplayTransparency: a flat alpha 0.35 showed a bright body 48 % and a dark body 74 % transparent).</summary>
    public const float DefaultAvatarOpacity = 0.35f;

    float avatarOpacity = DefaultAvatarOpacity; // the user default (hm_opacity)
    float? directorOpacity;                    // the current view state's value while the tour shows one

    /// <summary>fires after a capture's avatars (and hair) exist, before its audio loads - attachments (shoes) hook here</summary>
    public static event Action<HeadMovement> AvatarsLoaded;

    string[] captures;

    void Awake()
    {
        Instance = this;
        captures = Directory.GetDirectories(assetPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();

        floorPatterns = gameObject.AddComponent<FloorPatterns>();
        floorGrid = new GameObject("Floor").AddComponent<FloorGrid>();
        skeletonLegend = gameObject.AddComponent<SkeletonLegend>();
        partnerConnection = gameObject.AddComponent<PartnerConnection>();
        splatCloud = new GameObject("Splats").AddComponent<SplatCloud>();
        roomMesh = new GameObject("Room").AddComponent<RoomMesh>();
        virtualCameraRig = new GameObject("Virtual Cameras").AddComponent<VirtualCameraRig>();
    }

    void Start()
    {
        FindRig();
    }

    void OnEnable()
    {
        Instance = this;
        // a script reload in Play mode keeps the serialisable flags (audioLoaded) but drops the capture (timeline,
        // poses): fall back to "nothing loaded" so the HUD and the CLI ask for hm_load instead of throwing every frame
        if (timeline == null && audioLoaded)
        {
            audioLoaded = false;
            playing = false;
        }
    }

    float nextRigSearch;

    /// <summary>the desktop camera rig (the Simulator object); null in VR, where it is inactive and never moves the user</summary>
    void FindRig()
    {
        if (cameraControl != null) return;
        GameObject simulator = GameObject.Find("Simulator");
        cameraControl = simulator != null ? simulator.GetComponent<CameraControl>() : null;
        nextRigSearch = Time.unscaledTime + 1f;
    }

    public IReadOnlyList<string> CaptureNames => captures.Select(Path.GetFileName).ToList();

    // ---- read-only accessors for the dance layers (Assets/Tour/DanceLayers.cs, VIEWER_SPEC v3) ----
    public CaptureManifest Manifest => manifest;
    public CaptureTimeline Timeline => timeline;
    public BeatGrid Beats => beatGrid;
    public int CurrentFrame => currentFrame;
    public bool AudioLoaded => audioLoaded;
    public bool IsPlaying => playing;
    public Dancer LeadDancer => Lead;
    public Dancer FollowDancer => Follow;
    public IReadOnlyDictionary<Role, SmplxAvatar> Avatars => avatars;
    public CameraControl OrbitCamera => cameraControl;
    public SkeletonStyle Skeletons => skeletonStyle;
    public FloorPatterns Footprints => floorPatterns;
    public FloorGrid Grid => floorGrid;

    /// <summary>skeleton colouring now: physics (estimated load) while the physics layer / view is on, else rhythm</summary>
    public SkeletonStyle.Mode SkeletonMode => showPhysics ? SkeletonStyle.Mode.Physics : SkeletonStyle.Mode.Rhythm;

    /// <summary>the user default avatar opacity (hm_opacity); view states scale it</summary>
    public float AvatarOpacity => avatarOpacity;

    /// <summary>the opacity the avatars show now: the view state's value while the tour shows one, else the default</summary>
    public float EffectiveAvatarOpacity => directorOpacity ?? avatarOpacity;

    /// <summary>set the user default avatar opacity (0..1); returns the effective value</summary>
    public float SetAvatarOpacity(float opacity)
    {
        avatarOpacity = Mathf.Clamp01(float.IsFinite(opacity) ? opacity : DefaultAvatarOpacity);
        ApplyAvatarOpacity();
        return EffectiveAvatarOpacity;
    }

    /// <summary>DanceTour: the view state's avatar opacity (null = back to the user default)</summary>
    public void SetDirectorAvatarOpacity(float? opacity)
    {
        directorOpacity = opacity.HasValue ? Mathf.Clamp01(opacity.Value) : null;
        ApplyAvatarOpacity();
    }

    void ApplyAvatarOpacity()
    {
        float o = EffectiveAvatarOpacity;
        foreach (SmplxAvatar avatar in avatars.Values)
        {
            if (avatar != null) avatar.Opacity = o;
        }
    }

    /// <summary>current flag of a layer (floor|grid|tension|splats|cameras|hud|avatars|timing|physics|counterbalance|traces|neck|graph|axis|floorcraft)</summary>
    public bool LayerVisible(string layer) => layer.ToLowerInvariant() switch
    {
        "floor" => showFloor, "grid" => showGrid, "tension" => showConnection, "splats" => showSplats, "room" => showRoom, "cameras" => showCameras,
        "hud" => showHud, "avatars" => showAvatars, "timing" => showTiming, "physics" => showPhysics,
        _ => DanceLayers.LayerOn(layer.ToLowerInvariant())
    };

    /// <summary>load a capture by folder name (CLI playtests, menus)</summary>
    public bool LoadCapture(string folderName)
    {
        int index = Array.FindIndex(captures, c => string.Equals(Path.GetFileName(c), folderName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        LoadCapture(index);
        return true;
    }

    public void LoadCapture(int selection)
    {
        if (selection < 0 || selection >= captures.Length) return;

        Pause();
        audioLoaded = false;

        if (Lead != null)
        {
            Destroy(Lead.gameObject);
        }

        if (Follow != null)
        {
            Destroy(Follow.gameObject);
        }

        ClearV3();

        currentFolder = selection;
        currentFrame = -1;
        if (ExternallyDriven && audioSource != null) audioSource.mute = externalSavedMute;
        externalAudioTime = float.NaN; // a new capture is never externally driven until its director says so
        loopMeasure = false;

        manifest = CaptureManifest.Load(captures[currentFolder]);
        Fps = manifest.fps;

        List<List<Vector3>> leadPoses = ReadPoses(manifest.JointsPath(Role.Lead));
        List<List<Vector3>> followPoses = ReadPoses(manifest.JointsPath(Role.Follow));
        DanceOrigin.Begin(manifest, leadPoses, followPoses); // VIEWER_SPEC 3.0: the dance starts at (0,0) and travels
        FrameCount = Mathf.Min(manifest.frame_count, Mathf.Min(leadPoses.Count, followPoses.Count));
        timeline = manifest.BuildTimeline(FrameCount);
        FrameCount = timeline.Count;
        AudioOffset = timeline.First;

        string timingPath = manifest.OptionalPath(manifest.timing);
        timingData = null;
        if (timingPath != null)
        {
            try
            {
                timingData = TimingData.Load(timingPath);
            }
            catch (Exception e)
            {
                Warn($"timing.json unreadable ({e.Message}) - timing layer disabled");
            }
        }

        float[] frameSeconds = timeline.RelativeSeconds();
        Lead = NewDancer(Role.Lead, leadPoses, frameSeconds);
        Follow = NewDancer(Role.Follow, followPoses, frameSeconds);

        beatGrid = null;
        if (manifest.beats != null)
        {
            string zoukTimeString = File.ReadAllText(manifest.PathOf(manifest.beats));
            beatGrid = new BeatGrid(JsonConvert.DeserializeObject<List<List<float>>>(zoukTimeString));
        }
        else
        {
            Debug.LogWarning($"{manifest.DisplayName}: no zouk-time-analysis.json - timing checks disabled");
        }

        contactDetection = GetComponent<ContactDetection>();
        contactDetection.Reset();
        contactDetection.Init(Lead, Follow, BloomMaterial);

        floorPatterns.Init(Lead, Follow, BloomMaterial, beatGrid, Fps, AudioOffset);
        floorPatterns.SetVisible(showFloor);
        partnerConnection.Init(Lead, Follow, BloomMaterial, Fps);
        partnerConnection.SetVisible(showConnection);
        virtualCameraRig.Load(manifest.PathOf(manifest.virtual_cameras), BloomMaterial);
        DanceOrigin.ShiftWorldLines(virtualCameraRig.transform);
        virtualCameraRig.SetVisible(showCameras);
        DanceOrigin.Place(splatCloud.transform);
        LoadRoom();
        StartCoroutine(LoadSplats());

        LoadV3();

        BuildGrid();
        skeletonStyle = new SkeletonStyle { Current = SkeletonMode };
        try
        {
            skeletonStyle.Init(Lead, Follow, timeline, timingData, beatGrid, physicsData, partnerConnection);
        }
        catch (Exception e)
        {
            Warn($"skeleton colours failed ({e.Message}) - legacy glow");
            skeletonStyle = null;
        }

        skeletonLegend.Show = showPhysics && skeletonStyle != null;
        skeletonLegend.Source = skeletonStyle?.LoadSource ?? "";

        StartCoroutine(LoadAudio());
    }

    /// <summary>the floor grid covers both dancers over the whole take (+ margin), aligned to the origin</summary>
    void BuildGrid()
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        int n = Mathf.Min(Lead.FrameCount, Follow.FrameCount);
        for (int f = 0; f < n; f += 3)
        {
            foreach (Dancer d in new[] { Lead, Follow })
            {
                Vector3 p = d.Joint(f, SmplJoint.Pelvis);
                if (float.IsNaN(p.x)) continue;
                x0 = Mathf.Min(x0, p.x);
                x1 = Mathf.Max(x1, p.x);
                z0 = Mathf.Min(z0, p.z);
                z1 = Mathf.Max(z1, p.z);
            }
        }

        floorGrid.Build(x0 > x1 ? new Rect(-1, -1, 2, 2) : Rect.MinMaxRect(x0, z0, x1, z1));
        floorGrid.SetVisible(showGrid);
    }

    /// <summary>re-colour the skeletons of the shown frame (mode switch while paused)</summary>
    public void RefreshSkeletons()
    {
        if (skeletonStyle != null) skeletonStyle.Current = SkeletonMode;
        skeletonLegend.Show = showPhysics && skeletonStyle != null;
        if (!audioLoaded || timeline == null || Lead == null) return;
        if (ExternallyDriven)
        {
            externalF0 = -1; // re-pose (re-colour) the driven time on the next Update
            return;
        }

        currentFrame = -1;
        SetToFrameNumber();
    }

    /// <summary>skinned SMPL-X avatars + timing/physics overlays, each only when the capture has its files</summary>
    void LoadV3()
    {
        foreach (Role role in new[] { Role.Lead, Role.Follow })
        {
            string skin = manifest.RolePath(manifest.smplx_skin, role);
            string motion = manifest.RolePath(manifest.smplx, role);
            if (skin == null || motion == null)
            {
                if (manifest.smplx != null) Warn($"{role}: smplx motion/skin binary missing - no avatar");
                continue;
            }

            try
            {
                SmplxAvatar avatar = SmplxAvatar.Create(skin, motion, role, null, manifest.RolePath(manifest.smplx_albedo, role));
                if (manifest.smplx_albedo != null && !avatar.Textured) Warn($"{role}: albedo declared but not applied");
                DanceOrigin.Place(avatar.transform);
                if (avatar.FrameCount < FrameCount) Warn($"{role} avatar has {avatar.FrameCount} frames < {FrameCount}");
                avatar.Opacity = EffectiveAvatarOpacity;
                avatar.SetVisible(showAvatars);
                avatars[role] = avatar;
            }
            catch (Exception e)
            {
                Warn($"{role} avatar failed to load: {e.Message}");
            }
        }

        LoadHair();
        try
        {
            AvatarsLoaded?.Invoke(this);
        }
        catch (Exception e)
        {
            Warn($"avatar attachments failed: {e.Message}");
        }

        if (timingData != null)
        {
            timingOverlay = new GameObject("Timing Overlay").AddComponent<TimingOverlay>();
            timingOverlay.Init(timingData, timeline, Lead, Follow, BloomMaterial);
            timingOverlay.SetVisible(showTiming);
        }

        string physicsPath = manifest.OptionalPath(manifest.physics);
        if (physicsPath != null)
        {
            try
            {
                PhysicsData physics = PhysicsData.Load(physicsPath);
                DanceOrigin.Shift(physics);
                physicsData = physics;
                physicsOverlay = new GameObject("Physics Overlay").AddComponent<PhysicsOverlay>();
                physicsOverlay.Init(physics, timeline, BloomMaterial);
                physicsOverlay.SetVisible(showPhysics);
            }
            catch (Exception e)
            {
                Warn($"physics.json unreadable ({e.Message}) - physics layer disabled");
            }
        }
    }

    /// <summary>the follow's groomed, simulated hair (Assets/Hair) when the capture has hair_groom.json; it lives on the
    /// follow avatar (destroyed with it) and follows the avatar's frame and visibility</summary>
    void LoadHair()
    {
        string groom = manifest.OptionalPath("hair_groom.json");
        if (groom == null || !avatars.TryGetValue(Role.Follow, out SmplxAvatar follow)) return;
        try
        {
            HairStrands hair = HairStrands.Create(follow, manifest.RolePath(manifest.smplx_skin, Role.Follow), groom, timeline.AudioTimeOf);
            if (hair.Warning != null) Warn(hair.Warning);
        }
        catch (Exception e)
        {
            Warn($"hair failed to load: {e.Message}");
        }
    }

    void ClearV3()
    {
        foreach (SmplxAvatar avatar in avatars.Values)
        {
            if (avatar != null) Destroy(avatar.gameObject);
        }

        avatars.Clear();
        if (timingOverlay != null) Destroy(timingOverlay.gameObject);
        if (physicsOverlay != null) Destroy(physicsOverlay.gameObject);
        timingOverlay = null;
        physicsOverlay = null;
        physicsData = null;
        timingData = null;
        loadWarnings.Clear();
    }

    void Warn(string message)
    {
        loadWarnings.Add(message);
        Debug.LogWarning($"{manifest?.DisplayName}: {message}");
    }

    /// <summary>the room mesh loads on demand (the layer starts off; its textures are large)</summary>
    void LoadRoom()
    {
        if (!showRoom)
        {
            roomMesh.Clear();
            return;
        }

        try
        {
            roomMesh.Load(manifest);
        }
        catch (Exception e)
        {
            roomMesh.Clear();
            Warn($"room mesh failed to load: {e.Message}");
        }

        DanceOrigin.Place(roomMesh.transform);
        roomMesh.SetVisible(showRoom);
    }

    IEnumerator LoadSplats()
    {
        yield return splatCloud.Load(manifest);
        splatCloud.SetVisible(showSplats);
    }

    [Serializable]
    public class VideoMetadata
    {
        public float duration;
        public int frame_count;
    }

    IEnumerator LoadAudio()
    {
        // Build the full path to the WAV file in StreamingAssets
        string filePath = manifest.PathOf(manifest.audio ?? "audio.wav");
        if (!filePath.Contains("://") && !File.Exists(filePath))
        {
            // exported with --no-audio (or the wav is missing): play against a silent clock so the capture works
            Warn($"no audio ({Path.GetFileName(filePath)}) - playing with a silent clock");
            OnClipReady(SilentClip());
            yield break;
        }

        // On most platforms, we need the "file://" prefix to read directly
        // For Android, UnityWebRequest can handle it without the prefix, but
        // using "file://" will work cross-platform (except WebGL).
        if (!filePath.Contains("://"))
        {
            filePath = "file://" + filePath;
        }

        // Use UnityWebRequestMultimedia to download the WAV file as an AudioClip
        using UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(filePath, AudioType.WAV);
        yield return www.SendWebRequest();

        // Check for errors
        if (www.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError($"Error loading audio: {www.error}");
            OnClipReady(SilentClip());
        }
        else
        {
            OnClipReady(DownloadHandlerAudioClip.GetContent(www));
        }
    }

    AudioClip SilentClip()
    {
        const int rate = 8000;
        int samples = Mathf.Max(rate, Mathf.CeilToInt((Mathf.Max(0f, timeline.Last) + 1f) * rate));
        return AudioClip.Create("silence", samples, 1, rate, false);
    }

    void OnClipReady(AudioClip clip)
    {
        // Create an AudioSource (if one doesn't exist)
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        // Assign the clip and play
        audioSource.clip = clip;
        audioSource.pitch = Speeds[speedIndex];
        audioSource.time = Mathf.Clamp(AudioOffset, 0, clip.length - 0.01f);
        audioLoaded = true;

        HashSet<int> framesWithUpOrDownbeat = new();
        if (beatGrid != null)
        {
            for (int i = 0; i < beatGrid.Count; i++)
            {
                if (beatGrid.Types[i] == 3) continue; // filter out high-hat

                if (timeline.Covers(beatGrid.Times[i])) framesWithUpOrDownbeat.Add(timeline.FrameAt(beatGrid.Times[i]));
            }
        }

        beatIntensityByFrame = new Dictionary<int, float>();
        float currentIntensity = 0;
        const int intensityFalloffPeriod = 5;
        for (int i = 0; i < FrameCount; i++)
        {
            if (framesWithUpOrDownbeat.Contains(i))
            {
                currentIntensity = 10;
            }
            else if (currentIntensity > 0)
            {
                currentIntensity -= 10f / intensityFalloffPeriod;
            }

            beatIntensityByFrame[i] = currentIntensity;
        }

        SetToFrameNumber();
        FeedCamera();
        if (cameraControl != null) cameraControl.SnapFollow(); // a new capture is a cut
        Debug.Log($"Loaded performance: {captures[currentFolder]}");
    }

    static List<List<Vector3>> ReadPoses(string jsonPath)
    {
        string jsonString = File.ReadAllText(jsonPath);
        List<List<Float3>> allPoses = JsonConvert.DeserializeObject<List<List<Float3>>>(jsonString);
        return allPoses.Select(pose => pose.Select(float3 => new Vector3(float3.x, float3.y, float3.z)).ToList()).ToList();
    }

    Dancer NewDancer(Role role, List<List<Vector3>> poses, float[] frameSeconds)
    {
        Dancer dancer = new GameObject(role.ToString()).AddComponent<Dancer>();
        float[][] jerk = null;
        if (timingData != null && timingData.Jerk.TryGetValue(role, out float[][] j) && j.Length >= frameSeconds.Length) jerk = j;
        // v3 captures: the skinned follow avatar carries the groomed hair; the old LineRenderer hair is off
        dancer.Init(role, poses, BloomMaterial, frameSeconds, jerk, legacyHair: manifest.version < 3);
        return dancer;
    }

    void SetToFrameNumber()
    {
        int frameNumber = GetFrameNumber();
        if (currentFrame == frameNumber) return;
        currentFrame = frameNumber;

        float currentBeatIntensity = beatIntensityByFrame.GetValueOrDefault(frameNumber, 0);

        // VIEWER_SPEC 3.3 / 3.8: rhythm mode = the beat pulse, physics mode = the estimated load (SkeletonStyle)
        Lead.SetPoseToFrame(frameNumber, currentBeatIntensity, skeletonStyle?.Colours(Role.Lead, frameNumber));
        Follow.SetPoseToFrame(frameNumber, currentBeatIntensity, skeletonStyle?.Colours(Role.Follow, frameNumber));

        contactDetection.DetectContact(frameNumber);
        floorPatterns.SetFrame(frameNumber);
        partnerConnection.SetFrame(frameNumber);
        splatCloud.SetFrame(frameNumber, Fps);

        foreach (SmplxAvatar avatar in avatars.Values) avatar.SetFrame(frameNumber);
        // overlays follow the audio clock of the shown frame, so markers and the pose never disagree
        float frameTime = timeline.AudioTimeOf(frameNumber);
        if (timingOverlay != null) timingOverlay.SetTime(frameTime);
        if (physicsOverlay != null) physicsOverlay.SetTime(frameTime);
    }

    #region LESSON TRANSPORT

    public void SlowDown() => SetSpeed(speedIndex - 1);

    public void SpeedUp() => SetSpeed(speedIndex + 1);

    void SetSpeed(int index)
    {
        speedIndex = Mathf.Clamp(index, 0, Speeds.Length - 1);
        // pitch drops with speed; an AudioMixer pitch-shifter would keep it (TODO for lessons with vocals)
        if (audioSource != null) audioSource.pitch = Speeds[speedIndex];
    }

    public void TogglePlayPause()
    {
        if (!audioLoaded) return;

        if (playing)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    void Pause()
    {
        playing = false;
        if (audioSource != null) audioSource.Pause();
    }

    void Resume()
    {
        if (GetFrameNumber() >= FrameCount - 1) Seek(AudioOffset); // play at the end restarts the take
        if (loopMeasure && audioSource.time >= beatGrid.MeasureEnd(loopedMeasure))
        {
            audioSource.time = beatGrid.MeasureStart(loopedMeasure);
        }

        playing = true;
        audioSource.Play();
    }

    /// <summary>jump to an audio-timeline time; keeps the play/pause state so paused lessons stay paused</summary>
    public void Seek(float audioTime)
    {
        if (!audioLoaded) return;

        float firstPose = timeline.First;
        float lastPose = timeline.Last;
        float target = Mathf.Clamp(audioTime, Mathf.Max(0, firstPose), Mathf.Min(lastPose, audioSource.clip.length - 0.01f));

        // Unity ignores AudioSource.time on a stopped source (fresh load, or after the clip ended), so seek
        // while playing and re-pause in the same frame - nothing is audible.
        bool wasPlaying = audioSource.isPlaying;
        if (!wasPlaying) audioSource.Play();
        audioSource.time = target;
        if (!wasPlaying) audioSource.Pause();
        SetToFrameNumber();
    }

    public void StepBeat(int direction)
    {
        if (beatGrid == null || !audioLoaded) return;

        Pause();
        Seek(direction > 0 ? beatGrid.NextBeat(audioSource.time) : beatGrid.PreviousBeat(audioSource.time));
    }

    public void StepMeasure(int direction)
    {
        if (beatGrid == null || !audioLoaded) return;

        int measure = beatGrid.MeasureIndex(audioSource.time);
        float start = beatGrid.MeasureStart(measure);
        // "previous" from a little way into a measure restarts it, like a media player
        if (direction < 0 && audioSource.time - start > 0.25f) direction = 0;
        int target = Mathf.Clamp(measure + direction, 0, beatGrid.MeasureCount - 1);
        if (loopMeasure) loopedMeasure = target;
        Seek(beatGrid.MeasureStart(target));
    }

    public void ToggleLoopMeasure()
    {
        if (beatGrid == null || !audioLoaded) return;

        loopMeasure = !loopMeasure;
        loopedMeasure = beatGrid.MeasureIndex(audioSource.time);
    }

    public void Restart() => Seek(AudioOffset);

    /// <summary>jump to a 1-based measure number (as shown in the HUD)</summary>
    public void GotoMeasure(int measureNumber)
    {
        if (beatGrid == null || !audioLoaded) return;

        int target = Mathf.Clamp(measureNumber - 1, 0, beatGrid.MeasureCount - 1);
        if (loopMeasure) loopedMeasure = target;
        Seek(beatGrid.MeasureStart(target));
    }

    /// <summary>nearest supported speed (0.5, 0.75, 1)</summary>
    public void SetPlaybackSpeed(float speed)
    {
        int best = 0;
        for (int i = 1; i < Speeds.Length; i++)
        {
            if (Mathf.Abs(Speeds[i] - speed) < Mathf.Abs(Speeds[best] - speed)) best = i;
        }

        SetSpeed(best);
    }

    public void SetLoopMeasure(bool on)
    {
        if (beatGrid == null || !audioLoaded) return;
        if (on != loopMeasure) ToggleLoopMeasure();
    }

    public void Play()
    {
        if (audioLoaded && !playing) Resume();
    }

    public void Stop() => Pause();

    /// <summary>show/hide a visual layer: floor (footprints) | grid | tension | splats | room | cameras | hud | avatars |
    /// timing | physics | counterbalance | traces | neck | graph | axis | floorcraft | balance. Returns the new state.</summary>
    public bool SetLayerVisible(string layer, bool visible)
    {
        switch (layer.ToLowerInvariant())
        {
            case "floor": floorPatterns.SetVisible(showFloor = visible); break;
            case "grid": floorGrid.SetVisible(showGrid = visible); break;
            case "tension": partnerConnection.SetVisible(showConnection = visible); break;
            case "splats": splatCloud.SetVisible(showSplats = visible); break;
            case "room":
                showRoom = visible;
                if (visible && !roomMesh.HasContent && manifest != null) LoadRoom();
                roomMesh.SetVisible(visible);
                break;
            case "cameras": virtualCameraRig.SetVisible(showCameras = visible); break;
            case "hud": showHud = visible; break;
            case "avatars": SetAvatarsVisible(visible); break;
            case "timing": SetTimingVisible(visible); break;
            case "physics": SetPhysicsVisible(visible); break;
            case "counterbalance" or "traces" or "neck" or "graph" or "axis" or "floorcraft" or "balance" or "moves": DanceLayers.SetLayer(layer.ToLowerInvariant(), visible); break;
            default:
                throw new ArgumentException($"unknown layer '{layer}' (floor|grid|tension|splats|room|cameras|hud|avatars|timing|physics|counterbalance|traces|neck|graph|axis|floorcraft|balance|moves)");
        }

        return visible;
    }

    void SetAvatarsVisible(bool visible)
    {
        showAvatars = visible;
        foreach (SmplxAvatar avatar in avatars.Values) avatar.SetVisible(visible);
    }

    void SetTimingVisible(bool visible)
    {
        showTiming = visible;
        if (timingOverlay != null) timingOverlay.SetVisible(visible);
    }

    void SetPhysicsVisible(bool visible)
    {
        showPhysics = visible;
        if (physicsOverlay != null) physicsOverlay.SetVisible(visible);
        RefreshSkeletons(); // physics mode colours the skeletons by load; rhythm mode pulses them
    }

    /// <summary>skeleton mode by name (hm_skeleton): physics turns the physics layer on, rhythm turns it off</summary>
    public void SetSkeletonMode(SkeletonStyle.Mode mode) => SetPhysicsVisible(mode == SkeletonStyle.Mode.Physics);

    /// <summary>machine-readable state for CLI playtests</summary>
    public Dictionary<string, object> State()
    {
        Dictionary<string, object> state = new()
        {
            ["captures"] = CaptureNames,
            ["capture"] = manifest?.DisplayName,
            ["audioLoaded"] = audioLoaded,
            ["playing"] = playing,
            ["frame"] = currentFrame,
            ["frameCount"] = FrameCount,
            ["fps"] = Fps,
            ["speed"] = Speeds[speedIndex],
            ["loopMeasure"] = loopMeasure,
            ["layers"] = new Dictionary<string, bool>
            {
                ["floor"] = showFloor, ["grid"] = showGrid, ["tension"] = showConnection, ["splats"] = showSplats, ["room"] = showRoom,
                ["cameras"] = showCameras, ["hud"] = showHud, ["avatars"] = showAvatars, ["timing"] = showTiming,
                ["physics"] = showPhysics
            },
            ["version"] = manifest?.version ?? 0,
            ["timesDriven"] = timeline?.FromTimes ?? false,
            ["warnings"] = loadWarnings.ToList(),
            ["externalTime"] = ExternallyDriven ? (object)externalAudioTime : null
        };
        DanceLayers.AppendState(state);
        state["avatarOpacity"] = avatarOpacity;
        state["avatarOpacityEffective"] = EffectiveAvatarOpacity;
        if (cameraControl != null) state["camera"] = cameraControl.State();
        if (!audioLoaded) return state;

        state["audioTime"] = audioSource.time;
        state["frameAudioTime"] = timeline.AudioTimeOf(currentFrame);
        // the frame the binary search picks for the current audio time (playtest: must equal "frame" when paused)
        state["frameForAudioTime"] = timeline.FrameAt(audioSource.time);
        state["meanFrameInterval"] = FrameCount > 1 ? (timeline.Last - timeline.First) / (FrameCount - 1) : 0f;
        state["jerkSource"] = Lead.JerkSource;
        state["avatars"] = avatars.ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv =>
        {
            Bounds b = kv.Value.BakedBounds();
            return (object)new Dictionary<string, object>
            {
                ["visible"] = kv.Value.Visible, ["frame"] = kv.Value.CurrentFrame, ["bones"] = kv.Value.BoneCount,
                ["vertices"] = kv.Value.VertexCount, ["triangles"] = kv.Value.TriangleCount,
                ["textured"] = kv.Value.Textured, ["textureSize"] = kv.Value.TextureSize,
                ["opacity"] = kv.Value.Opacity, ["layerVisible"] = kv.Value.LayerVisible, ["feetHidden"] = kv.Value.FeetHidden,
                ["textureFormat"] = kv.Value.TextureFormatName, ["fkErrorMm"] = kv.Value.FkErrorMm(),
                ["pelvis"] = Vec(kv.Value.BonePosition(0)), ["queueOffset"] = kv.Value.QueueOffset,
                ["minY"] = b.min.y, ["maxY"] = b.max.y, ["sizeX"] = b.size.x, ["sizeZ"] = b.size.z,
                ["head"] = Vec(kv.Value.BonePosition(15)), ["skeletonHead"] = Vec(Lead != null && kv.Key == Role.Lead
                    ? Lead.Joint(currentFrame, SmplJoint.Head) : Follow.Joint(currentFrame, SmplJoint.Head))
            };
        });
        if (timingOverlay != null)
        {
            state["timing"] = new Dictionary<string, object>
            {
                ["touchdowns"] = timingOverlay.TouchdownCount, ["visibleRings"] = timingOverlay.VisibleRings,
                ["absoluteValid"] = timingOverlay.AbsoluteValid, ["absoluteKnown"] = timingData != null && timingData.AbsoluteKnown
            };
        }

        if (physicsOverlay != null)
        {
            state["physics"] = new Dictionary<string, object>
            {
                ["frame"] = physicsOverlay.Frame, ["lead"] = physicsOverlay.State(Role.Lead),
                ["follow"] = physicsOverlay.State(Role.Follow)
            };
        }
        if (beatGrid != null)
        {
            state["measure"] = beatGrid.MeasureIndex(audioSource.time) + 1;
            state["measureCount"] = beatGrid.MeasureCount;
        }

        state["steps"] = floorPatterns.Steps.Count;
        state["footprints"] = new Dictionary<string, object>
        {
            ["mode"] = floorPatterns.Mode.ToString().ToLowerInvariant(), ["visiblePrints"] = floorPatterns.VisiblePrints,
            ["recentSeconds"] = floorPatterns.RecentSeconds, ["steps"] = floorPatterns.Steps.Count
        };
        state["grid"] = new Dictionary<string, object>
        {
            ["visible"] = floorGrid.Visible, ["crosses"] = floorGrid.CrossCount, ["planeAlpha"] = floorGrid.PlaneAlpha,
            ["extent"] = new[] { floorGrid.Extent.xMin, floorGrid.Extent.yMin, floorGrid.Extent.xMax, floorGrid.Extent.yMax },
            ["crossArmM"] = floorGrid.CrossArm, ["crossWidthM"] = floorGrid.CrossWidth, ["crossBrightness"] = floorGrid.Brightness
        };
        state["skeletonMode"] = SkeletonMode.ToString().ToLowerInvariant();
        state["skeletonLegend"] = skeletonLegend.Show;
        state["connectionLines"] = partnerConnection.Connections.Count(c => c.Line != null && c.Line.enabled);
        state["stepsOnBeat"] = floorPatterns.Steps.Count(s => !float.IsNaN(s.ErrorMs) && Mathf.Abs(s.ErrorMs) <= floorPatterns.OnBeatMs);
        state["activeConnections"] = partnerConnection.Connections
            .Where(c => c.Active != null && currentFrame >= 0 && currentFrame < c.Active.Length && c.Active[currentFrame])
            .Select(c => $"{c.Name}:{c.Signal[currentFrame]:+0.00;-0.00}").ToList();
        state["splatsLoaded"] = splatCloud.HasContent;
        state["room"] = new Dictionary<string, object>
        {
            ["loaded"] = roomMesh.HasContent, ["vertices"] = roomMesh.VertexCount, ["triangles"] = roomMesh.TriangleCount,
            ["submeshes"] = roomMesh.SubmeshCount, ["textures"] = roomMesh.TextureCount,
            ["boundsMin"] = Vec(roomMesh.transform.TransformPoint(roomMesh.LocalBounds.min)),
            ["boundsMax"] = Vec(roomMesh.transform.TransformPoint(roomMesh.LocalBounds.max))
        };
        return state;
    }

    #endregion

    #region EXTERNAL CLOCK (Assets/Film: the film director owns dance time)

    // NaN = HeadMovement's own audio clock drives the frame; otherwise the film director's dance time (audio clock)
    float externalAudioTime = float.NaN;
    bool externalSavedMute;
    int externalF0 = -1, externalF1 = -1;
    float externalK = -1f;

    /// <summary>true while a director (FilmDirector) drives the shown time: the song AudioSource is muted, the lesson
    /// keys are ignored and poses blend between the two neighbouring frames (smooth slow motion)</summary>
    public bool ExternallyDriven => !float.IsNaN(externalAudioTime);

    public float ExternalAudioTime => externalAudioTime;

    /// <summary>physics.json of the loaded capture (Unity coordinates, origin-shifted), null without one</summary>
    public PhysicsData Physics => physicsData;

    /// <summary>show this audio-clock time (seconds; reference time + timeline.TimeToAudio) on the next Update and keep
    /// showing it until the next call: the caller owns the clock (film slow motion, holds, replays). Mutes and pauses the
    /// song; ReleaseExternal hands the clock back.</summary>
    public void DriveExternally(float audioTime)
    {
        if (!audioLoaded || timeline == null || !float.IsFinite(audioTime)) return;
        if (!ExternallyDriven)
        {
            Pause();
            if (audioSource != null)
            {
                externalSavedMute = audioSource.mute;
                audioSource.mute = true;
            }

            externalF0 = externalF1 = -1;
        }

        externalAudioTime = audioTime;
    }

    /// <summary>back to the song clock, paused at the time shown last (no-op when not driven)</summary>
    public void ReleaseExternal()
    {
        if (!ExternallyDriven) return;
        float t = externalAudioTime;
        externalAudioTime = float.NaN;
        externalF0 = externalF1 = -1;
        if (audioSource != null) audioSource.mute = externalSavedMute;
        currentFrame = -1; // re-pose (and un-blend) the nearest frame
        if (audioLoaded && timeline != null) Seek(t);
    }

    /// <summary>pose the externally driven time: joints lerp / bone rotations slerp between the two neighbouring frames;
    /// everything keyed by frame (colours, contacts, footprints, overlays, hair) uses the nearer frame</summary>
    void ShowExternalTime()
    {
        double t = externalAudioTime;
        double[] ts = timeline.AudioTimes;
        int n = ts.Length;
        int f0, f1;
        float k;
        if (n == 0) return;
        if (t <= ts[0] || n == 1)
        {
            f0 = f1 = 0;
            k = 0f;
        }
        else if (t >= ts[n - 1])
        {
            f0 = f1 = n - 1;
            k = 0f;
        }
        else
        {
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (ts[mid] <= t) lo = mid;
                else hi = mid;
            }

            f0 = lo;
            f1 = hi;
            k = (float)((t - ts[lo]) / Math.Max(1e-9, ts[hi] - ts[lo]));
        }

        int nearest = k < 0.5f ? f0 : f1;
        if (f0 == externalF0 && f1 == externalF1 && Mathf.Abs(k - externalK) < 1e-6f && currentFrame == nearest) return;
        externalF0 = f0;
        externalF1 = f1;
        externalK = k;
        currentFrame = nearest;

        float beat = beatIntensityByFrame != null ? beatIntensityByFrame.GetValueOrDefault(nearest, 0) : 0f;
        Lead.SetPoseToFrameBlend(f0, f1, k, beat, skeletonStyle?.Colours(Role.Lead, nearest));
        Follow.SetPoseToFrameBlend(f0, f1, k, beat, skeletonStyle?.Colours(Role.Follow, nearest));
        contactDetection.DetectContact(nearest);
        floorPatterns.SetFrame(nearest);
        partnerConnection.SetFrame(nearest);
        splatCloud.SetFrame(nearest, Fps);
        foreach (SmplxAvatar avatar in avatars.Values) avatar.SetFrameBlend(f0, f1, k);
        float frameTime = timeline.AudioTimeOf(nearest);
        if (timingOverlay != null) timingOverlay.SetTime(frameTime);
        if (physicsOverlay != null) physicsOverlay.SetExactTime((float)System.Math.Min(System.Math.Max(t, ts[0]), ts[n - 1])); // sub-frame COM / XCoM
    }

    #endregion

    void Update()
    {
        if (ExternallyDriven && (!audioLoaded || timeline == null || Lead == null || Follow == null))
        {
            if (audioSource != null) audioSource.mute = externalSavedMute;
            externalAudioTime = float.NaN; // a reload dropped the capture: back to the normal clock
        }

        if (ExternallyDriven)
        {
            ShowExternalTime();
            FeedCamera();
            return;
        }

        HandleKeyboard();

        if (!audioLoaded) return;

        if (playing)
        {
            if (loopMeasure && audioSource.time >= beatGrid.MeasureEnd(loopedMeasure))
            {
                audioSource.time = beatGrid.MeasureStart(loopedMeasure);
            }

            bool pastLastFrame = audioSource.time >= timeline.Last;
            if (!audioSource.isPlaying || pastLastFrame)
            {
                Pause();
                Seek(timeline.Last);
            }

            SetToFrameNumber();
        }

        FeedCamera();
    }

    /// <summary>VIEWER_SPEC 5.2: the camera follows the couple in XZ only - the pelvis midpoint of the shown frame (no
    /// height: the rig never bobs with the dancers' centre of gravity). The rig damps it and owns the height.</summary>
    void FeedCamera()
    {
        if (cameraControl == null && Time.unscaledTime >= nextRigSearch) FindRig();
        // (timeline null: a script reload in Play mode dropped the capture's non-serialised state - do nothing)
        if (cameraControl == null || !cameraControl.gameObject.activeInHierarchy || Lead == null || Follow == null || !audioLoaded ||
            timeline == null) return;
        int f = currentFrame >= 0 ? currentFrame : GetFrameNumber();
        Vector3 p = (Lead.Joint(f, SmplJoint.Pelvis) + Follow.Joint(f, SmplJoint.Pelvis)) * 0.5f;
        cameraControl.SetFollow(new Vector2(p.x, p.z), timeline.AudioTimeOf(f), playing);
    }

    /// <summary>O key: hand the camera to the view-state director, or take it back (free-fly from where it is)</summary>
    public void ToggleDirector()
    {
        if (cameraControl == null) return;
        if (cameraControl.Mode == CameraControl.Owner.Director)
        {
            cameraControl.TakeControl();
            return;
        }

        DanceLayers layers = DanceLayers.Ensure();
        DanceTour tour = layers != null ? layers.Tour : null;
        if (tour == null) return;
        if (tour.Active) cameraControl.UseDirector();
        else if (audioLoaded) tour.Hold(DanceTour.View.Orbit);
    }

    void HandleKeyboard()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.rightArrowKey.wasPressedThisFrame)
        {
            SpeedUp();
        }
        else if (keyboard.leftArrowKey.wasPressedThisFrame)
        {
            SlowDown();
        }
        else if (keyboard.spaceKey.wasPressedThisFrame)
        {
            TogglePlayPause();
        }

        if (keyboard.periodKey.wasPressedThisFrame) StepBeat(1);
        if (keyboard.commaKey.wasPressedThisFrame) StepBeat(-1);
        if (keyboard.rightBracketKey.wasPressedThisFrame) StepMeasure(1);
        if (keyboard.leftBracketKey.wasPressedThisFrame) StepMeasure(-1);
        if (keyboard.lKey.wasPressedThisFrame) ToggleLoopMeasure();
        if (keyboard.homeKey.wasPressedThisFrame || keyboard.rKey.wasPressedThisFrame) Restart();

        if (keyboard.fKey.wasPressedThisFrame) floorPatterns.SetVisible(showFloor = !showFloor);
        if (keyboard.tKey.wasPressedThisFrame) // footprints: recent -> all -> off -> recent
        {
            floorPatterns.SetMode(floorPatterns.Mode switch
            {
                FloorPatterns.FootprintMode.Recent => FloorPatterns.FootprintMode.All,
                FloorPatterns.FootprintMode.All => FloorPatterns.FootprintMode.Off,
                _ => FloorPatterns.FootprintMode.Recent
            });
        }
        if (keyboard.gKey.wasPressedThisFrame) SetLayerVisible("splats", !showSplats);
        if (keyboard.mKey.wasPressedThisFrame) SetLayerVisible("room", !showRoom);
        if (keyboard.cKey.wasPressedThisFrame) virtualCameraRig.SetVisible(showCameras = !showCameras);
        if (keyboard.hKey.wasPressedThisFrame) showHud = !showHud;
        if (keyboard.vKey.wasPressedThisFrame) SetAvatarsVisible(!showAvatars);
        if (keyboard.bKey.wasPressedThisFrame) SetTimingVisible(!showTiming);
        if (keyboard.pKey.wasPressedThisFrame) SetPhysicsVisible(!showPhysics);
        if (keyboard.oKey.wasPressedThisFrame) ToggleDirector();

        Key[] digits =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
            Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0
        };
        for (int i = 0; i < digits.Length; i++)
        {
            if (keyboard[digits[i]].wasPressedThisFrame)
            {
                LoadCapture(i);
                break;
            }
        }
    }

    /// <summary>nearest pose frame to the audio clock (binary search over the capture's real frame times)</summary>
    int GetFrameNumber() => timeline.FrameAt(audioSource.time);

    void OnGUI()
    {
        if (!showHud) return;

        bool overlays = timingOverlay != null || physicsOverlay != null; // v3 HUD lines are longer
        GUILayout.BeginArea(new Rect(12, 12, overlays ? 620 : 460, overlays ? 520 : 400), GUI.skin.box);
        if (manifest == null)
        {
            GUILayout.Label("Press 1-9/0 to load a performance:");
            for (int i = 0; i < Mathf.Min(captures.Length, 10); i++)
            {
                GUILayout.Label($"  {(i + 1) % 10}  {Path.GetFileName(captures[i])}");
            }
        }
        else if (audioLoaded)
        {
            float t = audioSource.time;
            string measure = beatGrid != null
                ? $"measure {beatGrid.MeasureIndex(t) + 1}/{beatGrid.MeasureCount}  eighth {beatGrid.BeatIndex(t) % BeatGrid.BeatsPerMeasure + 1}"
                : "no beats";
            GUILayout.Label($"{manifest.DisplayName}   {(playing ? "playing" : "paused")}   x{Speeds[speedIndex]:0.##}" +
                            (loopMeasure ? $"   loop m{loopedMeasure + 1}" : ""));
            GUILayout.Label($"{t - AudioOffset:0.00}s   frame {currentFrame}/{FrameCount}   {measure}");
            foreach (Role role in new[] { Role.Lead, Role.Follow })
            {
                if (floorPatterns.LatestStep(role, currentFrame, out FloorPatterns.Step step) && !float.IsNaN(step.ErrorMs))
                {
                    // no on-beat verdict when timing.json says the A/V offset is unknown (absolute_timing.valid false)
                    bool unsynced = timingData != null && !timingData.AbsoluteValid;
                    GUILayout.Label($"{role} last step: {(step.ErrorMs >= 0 ? "+" : "")}{step.ErrorMs:0} ms " +
                                    (unsynced ? "vs beat (unsynced)"
                                        : $"({(Mathf.Abs(step.ErrorMs) <= floorPatterns.OnBeatMs ? "on beat" : step.ErrorMs > 0 ? "late" : "early")})"));
                }
            }

            foreach (PartnerConnection.Connection c in showPhysics ? partnerConnection.Connections : Enumerable.Empty<PartnerConnection.Connection>())
            {
                if (c.Active != null && currentFrame < c.Active.Length && c.Active[currentFrame])
                {
                    float s = c.Signal[currentFrame];
                    GUILayout.Label($"{c.Name}: {(s >= 0 ? "tension" : "compression")} {Mathf.Abs(s):0.00} (estimated)");
                }
            }
        }

        if (audioLoaded)
        {
            float frameTime = timeline.AudioTimeOf(currentFrame);
            if (timingOverlay != null && showTiming)
            {
                foreach (string line in timingOverlay.HudLines(frameTime)) GUILayout.Label(line);
            }

            if (physicsOverlay != null && showPhysics)
            {
                foreach (string line in physicsOverlay.HudLines()) GUILayout.Label(line);
            }
        }

        string cameraMode = cameraControl == null ? "" : cameraControl.Mode == CameraControl.Owner.Director ? "   [camera: director]" : "   [camera: free]";
        GUILayout.Label("space play/pause   , . beat   [ ] measure   L loop   R restart   <- -> speed\n" +
                        "camera: A D orbit   W S dolly   Q E height   Z X tilt   shift fast   O director/free" + cameraMode + "\n" +
                        "F footprints   T footprints recent/all/off   G splats   C cameras   H hud   V avatars   B timing   P physics mode   M room");
        GUILayout.EndArea();

        if (audioLoaded && timingOverlay != null && showTiming)
        {
            timingOverlay.DrawTicker(new Rect(12, Screen.height - 96, Screen.width - 24, 84), audioSource.time);
        }
    }

    static float[] Vec(Vector3 v) => new[] { v.x, v.y, v.z };

    [Serializable]
    class Float3
    {
        public float x;
        public float y;
        public float z;
    }
}
