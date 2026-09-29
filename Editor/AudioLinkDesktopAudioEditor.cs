/*
                                     *=--
                                     =@@@%%#*%@*
                                     -@@@@@@@%=:
         =+                      .+#@@@@@@@@@*+:
        +@@+                  .-*@@@@@@@@@@#.
       .@@@#                 =@@@@@@@@@@@%-
       =@@@#             .-*@@@@@@@@@@@@@:
       -@@@#         .-+%@@@@@@@@@@@@@@@%
        :%@@@*=::-=*#@@@@@@@@@@@@@@@@@@@.
          -+*@@@@@@@@@@@@@@@@@@@@@@@@@@@%#*:
            =%@@@@@@@@@@@@@@@@@@@%#+--=+*#%@@+.
          -%@@@@@@@@@@@******+=:.          :@@@+
         +@@@@@@@@@+=+-                     .::+:
      :*@@@@@@@@#=
    :#@@#+-----.
  :*@%@:
 -%*..:

  ___ _  _ ___ ___   ___ _  _ ___ ___
 / __| || | __| _ \ / __| || | __| _ \
 \__ \ __ | _||  _/ \__ \ __ | _||  _/
 |___/_||_|___|_|   |___/_||_|___|_|

  ================ SHEP SHEP =================
  PRODUCT: AudioLink Desktop Audio
  SOURCE: AudioLinkDesktopAudioEditor.cs
  ROLE: Inspector, menus and listener volume restore after Play mode
  =============================================
  (c) 2026 Shep Shep. MIT License, see LICENSE.md.
  SHEP SHEP is a registered trademark in Germany.
  =============================================
*/

using System.IO;
using UnityEditor;
using UnityEngine;

namespace ShepShep.AudioTools
{
    [CustomEditor(typeof(AudioLinkDesktopAudio))]
    sealed class AudioLinkDesktopAudioEditor : Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var t = (AudioLinkDesktopAudio)target;
            EditorGUILayout.Space();
            if (!Application.isPlaying)
            {
                if (t.audioLink == null && GUILayout.Button("Find AudioLink in Scene"))
                {
                    Undo.RecordObject(t, "Find AudioLink");
                    if (!t.FindSceneAudioLink())
                        Debug.LogWarning("[AudioLink Desktop Audio] No active AudioLink in this scene.", t);
                }
                EditorGUILayout.HelpBox("Enter Play mode. AudioLink will listen to whatever Windows plays on the default output device. "
                    + "Unity's own audio is muted while this runs, so the capture cannot echo.", MessageType.Info);
                return;
            }
            EditorGUILayout.HelpBox(t.Status, t.Feeding ? MessageType.Info : MessageType.Warning);
            if (!string.IsNullOrEmpty(t.DeviceName)) EditorGUILayout.LabelField("Device", t.DeviceName);
            if (!string.IsNullOrEmpty(t.DeviceFormat)) EditorGUILayout.LabelField("Format", t.DeviceFormat);
            Meter("Desktop", t.DesktopLevel);
            Meter("AudioLink input", t.AudioLinkLevel);
            if (t.Feeding)
            {
                string via = t.Carrier == AudioLinkDesktopAudio.CarrierKind.Clip ? "live clip" : "filter";
                EditorGUILayout.LabelField("Latency (approx.)", Mathf.RoundToInt(t.LatencyMs) + " ms (" + via + ")");
                EditorGUILayout.LabelField("Buffer", Mathf.RoundToInt(t.BufferMs) + " ms");
                EditorGUILayout.LabelField("Dropouts", t.DropoutFrames == 0 ? "none" : t.DropoutMs.ToString("0") + " ms of gaps since Play");
                EditorGUILayout.LabelField("Auto level", t.autoLevel
                    ? "+" + t.LevelBoostDb.ToString("0") + " dB (limit +" + t.LevelBoostLimitDb.ToString("0") + " dB)" : "off");
            }
            if (!string.IsNullOrEmpty(t.CheckSummary)) EditorGUILayout.LabelField("Safety check", t.CheckSummary, EditorStyles.wordWrappedLabel);
            if (File.Exists(AudioLinkDesktopAudio.ReportPath) && GUILayout.Button("Open Check Report"))
                EditorUtility.OpenWithDefaultApp(AudioLinkDesktopAudio.ReportPath);
        }

        static void Meter(string label, float peak)
        {
            float db = peak > 1e-5f ? 20f * Mathf.Log10(peak) : -100f;
            var rect = EditorGUILayout.GetControlRect();
            EditorGUI.ProgressBar(rect, Mathf.InverseLerp(-60f, 0f, db), label + (db > -60f ? "  " + db.ToString("0") + " dB" : "  silent"));
        }
    }

    static class AudioLinkDesktopAudioMenu
    {
        const string ObjectName = "AudioLink Desktop Audio";

        // Same menus and priority as AudioLink's own "Add AudioLink Prefab to Scene", so it sits beside it.
        [MenuItem("GameObject/AudioLink/Add Desktop Audio to Scene", false, 49)]
        [MenuItem("Tools/AudioLink/Add Desktop Audio to Scene", false)]
        static void Add()
        {
#if UNITY_2022_3_OR_NEWER
            var existing = Object.FindObjectsByType<AudioLinkDesktopAudio>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var existing = Object.FindObjectsOfType<AudioLinkDesktopAudio>(true);
#endif
            if (existing.Length > 0)
            {
                Selection.activeObject = existing[0].gameObject;
                EditorGUIUtility.PingObject(existing[0]);
                return;
            }
            var go = new GameObject(ObjectName) { tag = "EditorOnly" };
            go.AddComponent<AudioLinkDesktopAudio>().FindSceneAudioLink();
            Undo.RegisterCreatedObjectUndo(go, "Add " + ObjectName);
            Selection.activeObject = go;
        }

        [MenuItem("Tools/AudioLink/Open Desktop Audio Check Report")]
        static void OpenReport() => EditorUtility.OpenWithDefaultApp(AudioLinkDesktopAudio.ReportPath);

        [MenuItem("Tools/AudioLink/Open Desktop Audio Check Report", true)]
        static bool CanOpenReport() => File.Exists(AudioLinkDesktopAudio.ReportPath);
    }

    [InitializeOnLoad]
    static class AudioLinkDesktopAudioPlayModeGuard
    {
        static AudioLinkDesktopAudioPlayModeGuard()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode) AudioLinkDesktopAudio.RestoreGlobals();
            };
        }
    }
}
