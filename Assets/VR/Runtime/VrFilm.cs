using System.Collections.Generic;
using UnityEngine;

namespace HmVr
{
    /// <summary>
    /// The lesson as the VR mode sees it: the library's film that is playing, with the same transport the flat playback bar uses
    /// (FilmPlaybackBar: exact seeks that re-sync the soundtrack, captions, layers; the scrubber holds the film while it is dragged).
    /// Everything on screen is a function of the film clock, so slow motion (the clock's rate) keeps the captions, the 3D layers and the
    /// soundtrack together. The soundtrack follows the rate as the flat speed button does: pitched with it for a music lesson, and for a
    /// lesson whose soundtrack is dialogue (library entry "dialogue") faded out below 1x instead, with the captions staying (FilmSoundtrack,
    /// docs/FILM_LIBRARY.md section 7).
    /// </summary>
    public static class VrFilm
    {
        public static readonly float[] SlowRates = { 1f, 0.5f, 0.25f, 0.1f };
        static readonly List<LibraryChapter> NoChapters = new();

        public static FilmLibrary Library => FilmLibrary.Instance;
        public static FilmDirector Director => FilmDirector.Instance;
        public static FilmPlaybackBar Bar => FilmPlaybackBar.Instance;

        public static bool InLesson => Library != null && Library.State == FilmLibrary.Mode.Playing && Director != null;

        public static LibraryEntry Entry => Library?.Current;
        public static float Time => Director != null ? Director.FilmTime : 0f;
        public static float Duration => Director != null ? Director.FilmDuration : 0f;
        public static float Rate => Director != null ? Director.Rate : 1f;

        /// <summary>"dialogue paused" (or "sound paused") while slow motion has faded this lesson's sound out; empty otherwise</summary>
        public static string SlowAudioHint => Director != null ? Director.SlowAudioHint : "";
        public static bool Paused => Director != null && Director.Paused;
        public static bool Playing => Director != null && !Director.Paused && Director.FilmTime < Director.FilmDuration - 0.05f;
        public static IReadOnlyList<LibraryChapter> Chapters => Bar != null ? Bar.Chapters : NoChapters;
        public static int ChapterIndex => Bar != null ? Bar.ChapterIndex : -1;

        public static string ChapterTitle
        {
            get
            {
                int i = ChapterIndex;
                return i >= 0 && i < Chapters.Count ? Chapters[i].Title : "";
            }
        }

        public static void TogglePlay()
        {
            if (Bar != null) Bar.TogglePlay();
            else if (Director != null) Director.SetPaused(!Director.Paused);
        }

        public static void Pause()
        {
            if (Director != null && !Director.Paused) Director.SetPaused(true);
        }

        public static void Resume()
        {
            if (Director == null || !Director.Paused) return;
            if (Bar != null) Bar.TogglePlay();
            else Director.SetPaused(false);
        }

        public static void SeekTo(float t)
        {
            if (Bar != null) Bar.SeekTo(t);
            else if (Director != null) Director.Seek(t);
        }

        public static void SeekBy(float seconds) => SeekTo(Time + seconds);

        public static void NextChapter()
        {
            if (Bar != null) Bar.NextChapter();
        }

        public static void PreviousChapter()
        {
            if (Bar != null) Bar.PreviousChapter();
        }

        public static void GoToChapter(int i)
        {
            IReadOnlyList<LibraryChapter> c = Chapters;
            if (i >= 0 && i < c.Count) SeekTo(c[i].StartS);
        }

        /// <summary>slow motion: the film clock's rate (1, 0.5, 0.25, 0.1); the soundtrack follows it exactly as the flat speed button does (dialogue is faded out)</summary>
        public static void SetRate(float rate)
        {
            if (Director != null) Director.Rate = Mathf.Clamp(rate, 0.05f, 4f);
            if (Bar != null) Bar.Touch();
        }

        public static void BeginScrub()
        {
            if (Director != null) Director.BeginScrub();
        }

        public static void ScrubTo(float u)
        {
            if (Director == null) return;
            Director.Seek(Mathf.Clamp01(u) * Director.FilmDuration);
        }

        public static void EndScrub()
        {
            if (Director != null) Director.EndScrub();
        }

        public static void BackToLibrary()
        {
            if (Library != null) Library.Back();
        }

        public static FilmNarration.Chunk[] CaptionChunks()
        {
            FilmNarration n = Director?.Direction?.Narration;
            if (n == null) return null;
            List<FilmNarration.Chunk> c = n.ChunksFor(Director.Aspect);
            return c != null && c.Count > 0 ? c.ToArray() : null;
        }
    }
}
