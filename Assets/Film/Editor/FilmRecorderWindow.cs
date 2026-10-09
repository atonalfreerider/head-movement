using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Head Movement / Record Settings...: capture choice (kept in EditorPrefs for the Record menu items), test
/// excerpts, and the running job's progress (FilmRecorder).</summary>
public class FilmRecorderWindow : EditorWindow
{
    float start, duration;
    bool fourK;
    Vector2 scroll;

    public static void Open()
    {
        FilmRecorderWindow w = GetWindow<FilmRecorderWindow>(false, "Film Recorder");
        w.minSize = new Vector2(420, 300);
        w.Show();
    }

    void OnInspectorUpdate()
    {
        if (FilmRecorder.Current?.Active ?? false) Repaint();
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("Capture", EditorStyles.boldLabel);
        string[] films = FilmRecorder.FilmCaptures();
        string[] options = new[] { $"Newest with a film ({FilmRecorder.DefaultCapture() ?? "none"})" }.Concat(films).ToArray();
        string chosen = EditorPrefs.GetString(FilmRecorder.CapturePref, "");
        int index = Mathf.Max(0, System.Array.IndexOf(films, chosen) + 1);
        int picked = EditorGUILayout.Popup(index, options);
        if (picked != index) EditorPrefs.SetString(FilmRecorder.CapturePref, picked == 0 ? "" : films[picked - 1]);
        string capture = picked == 0 ? FilmRecorder.DefaultCapture() : films[picked - 1];
        if (capture != null) EditorGUILayout.LabelField("Direction", FilmRecorder.FindDirection(capture) ?? "-", EditorStyles.miniLabel);
        EditorGUILayout.LabelField("Director", FilmRecorder.DirectorType() != null ? "FilmDirector" : "missing - records a plain pipeline test",
            EditorStyles.miniLabel);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        fourK = EditorGUILayout.Toggle("4K (2160 px short side)", fourK);
        start = EditorGUILayout.FloatField(new GUIContent("Start (film s)", "test excerpts: 0 = from the beginning"), start);
        duration = EditorGUILayout.FloatField(new GUIContent("Duration (s)", "0 = to the end of the film"), duration);

        bool busy = FilmRecorder.Current?.Active ?? false;
        using (new EditorGUI.DisabledScope(busy || EditorApplication.isCompiling))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Record Vertical", GUILayout.Height(28))) Queue("vertical");
            if (GUILayout.Button("Record Horizontal", GUILayout.Height(28))) Queue("horizontal");
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Job", EditorStyles.boldLabel);
        FilmRecorder.Job j = FilmRecorder.Current;
        if (j == null)
        {
            EditorGUILayout.LabelField("idle");
        }
        else
        {
            EditorGUILayout.LabelField("State", $"{j.state}  ({j.mode}, {j.capture}, {j.preset})");
            EditorGUILayout.LabelField("Message", j.message ?? "", EditorStyles.wordWrappedLabel);
            if (j.expectedFrames > 0)
            {
                Rect r = GUILayoutUtility.GetRect(18, 18, "TextField");
                float p = Mathf.Clamp01(j.frames / (float)j.expectedFrames);
                EditorGUI.ProgressBar(r, p, $"{j.frames} / {j.expectedFrames} frames   film {j.filmTime:0.0} s   {j.realElapsed:0} s real");
            }

            EditorGUILayout.LabelField("Output", j.output ?? "", EditorStyles.wordWrappedMiniLabel);
            if (j.report != null) EditorGUILayout.LabelField("Report", j.report, EditorStyles.wordWrappedMiniLabel);
            foreach (string w in j.warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!busy))
            {
                if (GUILayout.Button("Stop")) Debug.Log($"[film] {FilmRecorder.Stop()}");
            }

            using (new EditorGUI.DisabledScope(j.report == null || !File.Exists(j.report ?? "")))
            {
                if (GUILayout.Button("Open Report")) EditorUtility.OpenWithDefaultApp(Path.ChangeExtension(j.report, ".md"));
            }

            if (GUILayout.Button("Recordings Folder"))
            {
                Directory.CreateDirectory(FilmRecorder.RecordingsDir);
                EditorUtility.RevealInFinder(FilmRecorder.RecordingsDir + Path.DirectorySeparatorChar);
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Renders contain the copyrighted song and the narration voice: private files in head-movement/Recordings " +
                                "(git-ignored). Do not publish them.", MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    void Queue(string aspect)
    {
        string err = FilmRecorder.Queue(new FilmRecorder.Options
        {
            aspect = aspect, size = fourK ? "4k" : "hd", capture = EditorPrefs.GetString(FilmRecorder.CapturePref, ""), start = start,
            duration = duration, source = "window"
        }, true);
        if (err != null && err != "cancelled") EditorUtility.DisplayDialog("Head Movement - Record", err, "OK");
    }
}
