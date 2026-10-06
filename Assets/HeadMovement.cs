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
    PartnerConnection partnerConnection;
    SplatCloud splatCloud;
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
    bool showFloor = true, showConnection = true, showSplats = true, showCameras = false, showHud = true;
    bool showAvatars = true, showTiming = true, showPhysics = true;

    string[] captures;

    void Awake()
    {
        Instance = this;
        captures = Directory.GetDirectories(assetPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();

        floorPatterns = gameObject.AddComponent<FloorPatterns>();
        partnerConnection = gameObject.AddComponent<PartnerConnection>();
        splatCloud = new GameObject("Splats").AddComponent<SplatCloud>();
        virtualCameraRig = new GameObject("Virtual Cameras").AddComponent<VirtualCameraRig>();
    }

    void Start()
    {
        cameraControl = GameObject.Find("Simulator").GetComponent<CameraControl>();
    }

    public IReadOnlyList<string> CaptureNames => captures.Select(Path.GetFileName).ToList();

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
        loopMeasure = false;

        manifest = CaptureManifest.Load(captures[currentFolder]);
        Fps = manifest.fps;

        List<List<Vector3>> leadPoses = ReadPoses(manifest.JointsPath(Role.Lead));
        List<List<Vector3>> followPoses = ReadPoses(manifest.JointsPath(Role.Follow));
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
        virtualCameraRig.SetVisible(showCameras);
        StartCoroutine(LoadSplats());

        LoadV3();

        StartCoroutine(LoadAudio());
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
                SmplxAvatar avatar = SmplxAvatar.Create(skin, motion, role);
                if (avatar.FrameCount < FrameCount) Warn($"{role} avatar has {avatar.FrameCount} frames < {FrameCount}");
                avatar.SetVisible(showAvatars);
                avatars[role] = avatar;
            }
            catch (Exception e)
            {
                Warn($"{role} avatar failed to load: {e.Message}");
            }
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
        timingData = null;
        loadWarnings.Clear();
    }

    void Warn(string message)
    {
        loadWarnings.Add(message);
        Debug.LogWarning($"{manifest?.DisplayName}: {message}");
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
        }
        else
        {
            // Get the AudioClip from the download handler
            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

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
            Debug.Log($"Loaded performance: {captures[currentFolder]}");
        }
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
        dancer.Init(role, poses, BloomMaterial, frameSeconds, jerk);
        return dancer;
    }

    void SetToFrameNumber()
    {
        int frameNumber = GetFrameNumber();
        if (currentFrame == frameNumber) return;
        currentFrame = frameNumber;

        float currentBeatIntensity = beatIntensityByFrame.GetValueOrDefault(frameNumber, 0);

        Lead.SetPoseToFrame(frameNumber, currentBeatIntensity);
        Follow.SetPoseToFrame(frameNumber, currentBeatIntensity);

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

    /// <summary>show/hide a visual layer: floor | tension | splats | cameras | hud | avatars | timing | physics.
    /// Returns the new state.</summary>
    public bool SetLayerVisible(string layer, bool visible)
    {
        switch (layer.ToLowerInvariant())
        {
            case "floor": floorPatterns.SetVisible(showFloor = visible); break;
            case "tension": partnerConnection.SetVisible(showConnection = visible); break;
            case "splats": splatCloud.SetVisible(showSplats = visible); break;
            case "cameras": virtualCameraRig.SetVisible(showCameras = visible); break;
            case "hud": showHud = visible; break;
            case "avatars": SetAvatarsVisible(visible); break;
            case "timing": SetTimingVisible(visible); break;
            case "physics": SetPhysicsVisible(visible); break;
            default:
                throw new ArgumentException($"unknown layer '{layer}' (floor|tension|splats|cameras|hud|avatars|timing|physics)");
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
    }

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
                ["floor"] = showFloor, ["tension"] = showConnection, ["splats"] = showSplats,
                ["cameras"] = showCameras, ["hud"] = showHud, ["avatars"] = showAvatars, ["timing"] = showTiming,
                ["physics"] = showPhysics
            },
            ["version"] = manifest?.version ?? 0,
            ["timesDriven"] = timeline?.FromTimes ?? false,
            ["warnings"] = loadWarnings.ToList()
        };
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
                ["vertices"] = kv.Value.VertexCount, ["fkErrorMm"] = kv.Value.FkErrorMm(),
                ["minY"] = b.min.y, ["maxY"] = b.max.y, ["sizeX"] = b.size.x, ["sizeZ"] = b.size.z,
                ["head"] = Vec(kv.Value.BonePosition(15)), ["skeletonHead"] = Vec(Lead != null && kv.Key == Role.Lead
                    ? Lead.Joint(currentFrame, SmplJoint.Head) : Follow.Joint(currentFrame, SmplJoint.Head))
            };
        });
        if (timingOverlay != null)
        {
            state["timing"] = new Dictionary<string, object>
            {
                ["touchdowns"] = timingOverlay.TouchdownCount, ["visibleRings"] = timingOverlay.VisibleRings
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
        state["stepsOnBeat"] = floorPatterns.Steps.Count(s => !float.IsNaN(s.ErrorMs) && Mathf.Abs(s.ErrorMs) <= floorPatterns.OnBeatMs);
        state["activeConnections"] = partnerConnection.Connections
            .Where(c => c.Active != null && currentFrame >= 0 && currentFrame < c.Active.Length && c.Active[currentFrame])
            .Select(c => $"{c.Name}:{c.Signal[currentFrame]:+0.00;-0.00}").ToList();
        state["splatsLoaded"] = splatCloud.HasContent;
        return state;
    }

    #endregion

    void Update()
    {
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

        if (cameraControl.gameObject.activeInHierarchy)
        {
            int frameNumber = GetFrameNumber();
            Vector3 center = Vector3.Lerp(Lead.Center(frameNumber), Follow.Center(frameNumber), .5f);
            center = new Vector3(center.x, center.y - 0.5f, center.z);
            cameraControl.Center = center;
        }
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
        if (keyboard.tKey.wasPressedThisFrame) partnerConnection.SetVisible(showConnection = !showConnection);
        if (keyboard.gKey.wasPressedThisFrame) splatCloud.SetVisible(showSplats = !showSplats);
        if (keyboard.cKey.wasPressedThisFrame) virtualCameraRig.SetVisible(showCameras = !showCameras);
        if (keyboard.hKey.wasPressedThisFrame) showHud = !showHud;
        if (keyboard.vKey.wasPressedThisFrame) SetAvatarsVisible(!showAvatars);
        if (keyboard.bKey.wasPressedThisFrame) SetTimingVisible(!showTiming);
        if (keyboard.pKey.wasPressedThisFrame) SetPhysicsVisible(!showPhysics);

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
                    GUILayout.Label($"{role} last step: {(step.ErrorMs >= 0 ? "+" : "")}{step.ErrorMs:0} ms " +
                                    $"({(Mathf.Abs(step.ErrorMs) <= floorPatterns.OnBeatMs ? "on beat" : step.ErrorMs > 0 ? "late" : "early")})");
                }
            }

            foreach (PartnerConnection.Connection c in partnerConnection.Connections)
            {
                if (c.Active != null && currentFrame < c.Active.Length && c.Active[currentFrame])
                {
                    float s = c.Signal[currentFrame];
                    GUILayout.Label($"{c.Name}: {(s >= 0 ? "tension" : "compression")} {Mathf.Abs(s):0.00}");
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

        GUILayout.Label("space play/pause   , . beat   [ ] measure   L loop   R restart   <- -> speed\n" +
                        "WASD/QE orbit   F floor   T tension   G splats   C cameras   H hud\n" +
                        "V avatars   B timing   P physics");
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
