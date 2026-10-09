using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

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

    public float Volume
    {
        get => source != null ? source.volume : 1f;
        set
        {
            if (source != null) source.volume = value;
        }
    }

    AudioSource source;
    AudioClip clip;
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

    void Update()
    {
        if (clip == null || clock == null || startPending || Recording) return;
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
        $"{(IsPlaying ? $", playing at {AudioTime:0.000} s" : "")}, resyncs {Resyncs}";
}
