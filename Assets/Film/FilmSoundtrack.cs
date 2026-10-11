using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using HmVr;

/// <summary>
/// Plays a film's ONE pre-mixed soundtrack (film/INTERFACE.md section 2): &lt;film dir&gt;/audio/&lt;direction&gt;_mix.wav,
/// 48 kHz stereo, sample 0 = film time 0, made by film/audio/make_film_mix.py (narration over the studio song, the song
/// locked to dance time at 1.0x and ducked under speech, a free-running bed under slow motion / holds / replays that
/// crossfades back to the locked song). Because the whole film is pre-mixed the music cannot drift: the player only has
/// to start at the film clock's time.
///
///   FilmSoundtrack.Preload(path)                      // optional: decode before the film starts (recordings do this)
///   FilmSoundtrack.Play(path, () => director.FilmTime) // starts at the clock's current film time
///   FilmSoundtrack.StopAll()
///
/// While a Unity Recorder renders (Recording = true) the audio is rendered offline in lock with the frames
/// (AudioRenderer), so the player never seeks; outside a recording it re-syncs when it drifts by more than
/// ResyncThreshold (the editor's real-time playback can stall). The clip is 2D, bypasses effects and is not spatialised.
///
/// SLOW MOTION: the film clock's rate is the player's pitch (Pitch = the director's Rate). A film whose soundtrack is speech must not be
/// heard slowed (0.25x = two octaves down), so SlowAudio = Mute fades the sound out in ~120 ms when the rate is under SlowBelow (0.95) and back in
/// when it is SlowBelow or more; the player keeps running (muted, at the rate's pitch once silent) so it stays in step with the film clock and
/// a seek while slow is consistent. Rates above 1x keep their pitch (the owner asked about slow motion only). The state is Gain / SlowMuted.
/// </summary>
[AddComponentMenu("")]
public class FilmSoundtrack : MonoBehaviour
{
    public static FilmSoundtrack Current { get; private set; }

    public string WavPath { get; private set; }
    public string Error { get; private set; }
    public bool IsLoaded => clip != null && clip.loadState == AudioDataLoadState.Loaded;
    public bool IsPlaying => source != null && source.isPlaying;
    public float Length => clip != null ? clip.length : 0f;
    public int SampleRate => clip != null ? clip.frequency : 0;
    public int Channels => clip != null ? clip.channels : 0;

    /// <summary>playback position in film seconds (sample exact)</summary>
    public float AudioTime => source != null && clip != null ? source.timeSamples / (float)clip.frequency : 0f;

    /// <summary>film time at which playback started (the clock's value on the start frame)</summary>
    public float StartedAt { get; private set; } = float.NaN;

    /// <summary>Time.frameCount of the start frame (-1 before)</summary>
    public int StartFrame { get; private set; } = -1;

    public int Resyncs { get; private set; }

    /// <summary>set by the recorder: AudioRenderer keeps the clip locked to the frames, never seek</summary>
    public bool Recording;

    public float ResyncThreshold = 0.04f;

    /// <summary>held by the viewer (the film is paused or its scrubber is being dragged): the source stays paused at its position and
    /// Update never restarts it; SetHold(false) plays on from the film clock's time</summary>
    public bool Held { get; private set; }

    /// <summary>rates under this are slow motion (<see cref="SlowAudioRules"/> holds the rules, tested in EditMode)</summary>
    public const float SlowBelow = HmVr.SlowAudioRules.SlowBelow;

    /// <summary>the fade out / in of the slow-motion mute, seconds</summary>
    public const float FadeSeconds = HmVr.SlowAudioRules.FadeSeconds;

    /// <summary>playback rate of the clip (the film clock's rate; the re-sync follows it). With SlowAudio = Mute the player itself keeps
    /// its pitch at 1 until the sound has faded out, so the fade is never heard as a drop in pitch.</summary>
    public float Pitch
    {
        get => requestedPitch;
        set
        {
            requestedPitch = value;
            ApplyOutput();
        }
    }

    /// <summary>what the player actually runs at (differs from Pitch only during the fade out of a slow-motion mute)</summary>
    public float EffectivePitch => source != null ? source.pitch : 1f;

    /// <summary>what the sound does under SlowBelow; set by the director from the library entry (dialogue, slowmo_audio)</summary>
    public SlowAudioMode SlowAudio
    {
        get => slowAudio;
        set
        {
            slowAudio = value;
            ApplyOutput();
        }
    }

    /// <summary>the sound is (being) faded out because the film is in slow motion and this lesson does not slow its sound</summary>
    public bool SlowMuted => HmVr.SlowAudioRules.Muted(slowAudio, requestedPitch);

    /// <summary>0 to 1: the slow-motion fade (1 = full, 0 = silent); Volume is the level before it</summary>
    public float Gain { get; private set; } = 1f;

    /// <summary>real seconds the last slow-motion fade out / fade in took, from the frame that asked for it to the frame the gain arrived (-1 = none yet; for the playtests)</summary>
    public float LastFadeOutSeconds { get; private set; } = -1f;

    public float LastFadeInSeconds { get; private set; } = -1f;

    public float Volume
    {
        get => volume;
        set
        {
            volume = value;
            ApplyOutput();
        }
    }

    AudioSource source;
    AudioClip clip;
    float requestedPitch = 1f, volume = 1f, fadeStart;
    bool fadeMuting, fading;
    SlowAudioMode slowAudio = SlowAudioMode.Pitch;
    Func<float> clock;
    bool startPending;
    bool loading;

    /// <summary>start decoding the file (no playback); reuses the current player when it holds the same file</summary>
    public static FilmSoundtrack Preload(string wavPath)
    {
        if (string.IsNullOrEmpty(wavPath)) throw new ArgumentException("no soundtrack path");
        wavPath = System.IO.Path.GetFullPath(wavPath);
        if (Current != null && string.Equals(Current.WavPath, wavPath, StringComparison.OrdinalIgnoreCase) && Current.Error == null)
            return Current;

        StopAll();
        GameObject go = new("Film Soundtrack");
        DontDestroyOnLoad(go);
        FilmSoundtrack s = go.AddComponent<FilmSoundtrack>();
        s.WavPath = wavPath;
        s.source = go.AddComponent<AudioSource>();
        s.source.playOnAwake = false;
        s.source.loop = false;
        s.source.spatialBlend = 0f;
        s.source.priority = 0;
        s.source.bypassEffects = true;
        s.source.bypassListenerEffects = true;
        s.source.bypassReverbZones = true;
        s.source.dopplerLevel = 0f;
        Current = s;
        s.loading = true;
        s.StartCoroutine(s.Load());
        return s;
    }

    /// <summary>play the soundtrack from the film clock's current time (loads it first when needed)</summary>
    public static FilmSoundtrack Play(string wavPath, Func<float> filmClock)
    {
        FilmSoundtrack s = Preload(wavPath);
        s.clock = filmClock;
        s.startPending = true;
        if (s.IsLoaded) s.StartNow();
        return s;
    }

    public static void StopAll()
    {
        if (Current == null) return;
        if (Current.source != null) Current.source.Stop();
        Destroy(Current.gameObject);
        Current = null;
    }

    IEnumerator Load()
    {
        string uri = new Uri(WavPath).AbsoluteUri;
        using UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV);
        if (req.downloadHandler is DownloadHandlerAudioClip dh)
        {
            dh.streamAudio = false; // fully decoded PCM: sample-exact positions
            dh.compressed = false;
        }

        yield return req.SendWebRequest();
        loading = false;
        if (req.result != UnityWebRequest.Result.Success)
        {
            Error = $"soundtrack {WavPath}: {req.error}";
            Debug.LogError(Error);
            yield break;
        }

        AudioClip c = DownloadHandlerAudioClip.GetContent(req);
        if (c == null)
        {
            Error = $"soundtrack {WavPath}: not a readable WAV";
            Debug.LogError(Error);
            yield break;
        }

        if (c.loadState == AudioDataLoadState.Unloaded) c.LoadAudioData();
        while (c.loadState == AudioDataLoadState.Loading) yield return null;
        if (c.loadState != AudioDataLoadState.Loaded)
        {
            Error = $"soundtrack {WavPath}: load state {c.loadState}";
            Debug.LogError(Error);
            yield break;
        }

        c.name = System.IO.Path.GetFileNameWithoutExtension(WavPath);
        clip = c;
        source.clip = clip;
        if (startPending) StartNow();
    }

    /// <summary>pause (true) or resume (false) at the clock's time; the clip stays loaded</summary>
    public void SetHold(bool hold)
    {
        Held = hold;
        if (source == null) return;
        if (hold)
        {
            if (source.isPlaying) source.Pause();
            return;
        }

        if (clip == null || clock == null || startPending) return;
        float t = clock();
        if (t >= 0f && t < clip.length - 0.02f) PlayAt(SamplesOf(t));
    }

    /// <summary>move the player to film time t (the viewer seeks): playing on from there, or just positioned while held; a time past
    /// the end silences it. Before the clip is loaded nothing is needed: it starts at the clock's time when it is.</summary>
    public void SeekTo(float t)
    {
        if (clip == null || source == null) return;
        if (t >= clip.length - 0.02f)
        {
            if (source.isPlaying) source.Stop();
            return;
        }

        int s = SamplesOf(t);
        if (Held)
        {
            source.timeSamples = s;
            return;
        }

        PlayAt(s);
    }

    int SamplesOf(float t) => Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, t) * clip.frequency), 0, clip.samples - 1);

    /// <summary>position and play (the position is written again after Play: a stopped or paused source may restart from its old one)</summary>
    void PlayAt(int samples)
    {
        source.timeSamples = samples;
        if (!source.isPlaying) source.Play();
        source.timeSamples = samples;
    }

    void StartNow()
    {
        startPending = false;
        float t = clock != null ? clock() : 0f;
        StartedAt = t;
        StartFrame = Time.frameCount;
        if (t >= clip.length) return;
        source.timeSamples = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, t) * clip.frequency), 0, clip.samples - 1);
        source.Play();
    }

    /// <summary>the slow-motion fade (the unscaled clock: a paused or slowed game must not stretch it) and the player's output</summary>
    void TickGain()
    {
        // a recording never mutes (the recorder plays 1x and renders the audio offline)
        bool muting = SlowMuted && !Recording;
        if (muting != fadeMuting)
        {
            fadeMuting = muting;
            fading = true;
            fadeStart = Time.unscaledTime;
        }

        Gain = Recording ? 1f : HmVr.SlowAudioRules.Step(Gain, SlowMuted, Time.unscaledDeltaTime);
        if (fading && (muting ? Gain <= 0f : Gain >= 1f))
        {
            fading = false;
            if (muting) LastFadeOutSeconds = Time.unscaledTime - fadeStart;
            else LastFadeInSeconds = Time.unscaledTime - fadeStart;
        }

        ApplyOutput();
    }

    void ApplyOutput()
    {
        if (source == null) return;
        // while the sound is still fading out for a slow-motion mute it stays at 1x pitch: only a silent player runs at the slow rate
        source.pitch = HmVr.SlowAudioRules.PlayerPitch(slowAudio, requestedPitch, Gain);
        source.volume = volume * Gain;
    }

    void Update()
    {
        TickGain();
        if (clip == null || clock == null || startPending || Recording || Held) return;
        float t = clock();
        if (t < 0f || t >= clip.length - 0.02f)
        {
            if (source.isPlaying && t >= clip.length - 0.02f) source.Stop();
            return;
        }

        if (!source.isPlaying)
        {
            source.timeSamples = Mathf.RoundToInt(t * clip.frequency);
            source.Play();
            Resyncs++;
            return;
        }

        if (Mathf.Abs(AudioTime - t) > ResyncThreshold)
        {
            source.timeSamples = Mathf.RoundToInt(t * clip.frequency);
            Resyncs++;
        }
    }

    void OnDestroy()
    {
        if (Current == this) Current = null;
        if (clip != null) Destroy(clip);
    }

    public string Describe() =>
        $"{System.IO.Path.GetFileName(WavPath)}: {(Error ?? (loading ? "loading" : IsLoaded ? $"{Length:0.00} s {SampleRate} Hz x{Channels}" : "?"))}" +
        $"{(IsPlaying ? $", playing at {AudioTime:0.000} s" : "")}, resyncs {Resyncs}{(SlowMuted ? $", slow-motion mute (gain {Gain:0.00})" : "")}";
}
