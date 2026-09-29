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
  SOURCE: AudioLinkDesktopAudio.cs
  ROLE: Scene component: finds AudioLink, feeds it desktop audio, runs the safety check
  =============================================
  (c) 2026 Shep Shep. MIT License, see LICENSE.md.
  SHEP SHEP is a registered trademark in Germany.
  =============================================
*/

// Drop into a scene that has AudioLink. In Play mode on Windows, AudioLink listens to
// whatever the default output device plays. Unity's own output is muted while this runs,
// verified with a quiet test tone first, so the loopback capture cannot echo.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Audio;

namespace ShepShep.AudioTools
{
    [AddComponentMenu("AudioLink/AudioLink Desktop Audio")]
    [DisallowMultipleComponent]
    public sealed class AudioLinkDesktopAudio : MonoBehaviour
    {
        public enum MuteKind { None, ListenerFilter, ListenerVolume }
        public enum CarrierKind { Filter, Clip }

        [Tooltip("Optional. Leave empty to use the first AudioLink in the scene.")]
        public MonoBehaviour audioLink;

        [Tooltip("Lifts quiet desktop audio to full level, so AudioLink reacts the same at any app or Windows volume.")]
        public bool autoLevel = true;

        public string Status { get; private set; } = "Enter Play mode to start.";
        public string CheckSummary { get; private set; }
        public string DeviceName { get; private set; }
        public string DeviceFormat { get; private set; }
        public float DesktopLevel { get; private set; }
        public float AudioLinkLevel { get; private set; }
        public float LatencyMs { get; private set; }
        public long DropoutFrames { get; private set; }
        public bool Feeding { get; private set; }
        public MuteKind Mute { get; private set; }
        public CarrierKind Carrier { get; private set; }
        public float LevelBoostDb { get; private set; }
        public float LevelBoostLimitDb { get; private set; }
        public float DropoutMs { get; private set; }
        public float BufferMs { get; private set; }

        public static string ReportPath => Path.Combine(Path.GetTempPath(), "audiolink-desktop-audio-check.txt");

        static float? pendingListenerVolume;

        DesktopAudioCapture capture;
        CarrierFeed feed;
        ToneDetector detector;
        MonoBehaviour link;
        FieldInfo sourceField;
        AudioSource source;
        bool createdSource, haveSaved, checkPassed, warnedMultiple, reportWritten;
        SourceState saved;
        AudioSource originalSource;
        DesktopAudioCarrier carrier;
        AudioClip carrierClip;
        readonly List<DesktopAudioListenerMute> mutes = new List<DesktopAudioListenerMute>();
        MuteKind muteKind;
        int muteRefresh, sampleRate, dspBufferFrames, lastFrames, lastCalls;
        float lastRms, lastLeak, lastWritten, lastMix;
        bool lastPlaying, lastVirtual;
        readonly float[] probe = new float[1024];

        // Filter first (lowest delay); the clip carrier works wherever the filter is invisible to AudioLink.
        static readonly CarrierKind[] ComboCarriers = { CarrierKind.Filter, CarrierKind.Clip, CarrierKind.Clip };
        static readonly MuteKind[] ComboMutes = { MuteKind.ListenerFilter, MuteKind.ListenerFilter, MuteKind.ListenerVolume };
        const int ChunkFrames = 256;
        readonly float[] chunk = new float[ChunkFrames * 2];
        CarrierKind carrierKind;
        int clipFrames, writePos, leadFrames;
        float frameMax, clipWrittenPeak;
        bool pumpStarted;

        // VRChat's ClientSim deletes EditorOnly objects right after the scene loads, which runs after Awake.
        // Untag only the Play mode copy so the tool keeps running; the saved scene keeps its EditorOnly tag.
        void Awake()
        {
            if (Application.isPlaying && gameObject.CompareTag("EditorOnly")) gameObject.tag = "Untagged";
        }

        // Called when the component is added or reset in the Inspector.
        void Reset() => FindSceneAudioLink();

        void OnEnable()
        {
            if (Application.isPlaying) StartCoroutine(Run());
        }

        /// <summary>Fills the AudioLink field with the first active AudioLink in this object's scene. Returns true if one is set.</summary>
        public bool FindSceneAudioLink()
        {
            if (audioLink != null) return true;
            foreach (var mb in FindBehaviours())
            {
                if (!IsAudioLink(mb) || mb.gameObject.scene != gameObject.scene) continue;
                audioLink = mb;
                return true;
            }
            return false;
        }

        void OnDisable()
        {
            StopAllCoroutines();
            Teardown(Application.isPlaying ? "Disabled." : "Enter Play mode to start.");
        }

        IEnumerator Run()
        {
            checkPassed = reportWritten = false;
            Mute = MuteKind.None;
            CheckSummary = null;
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
            {
                Fail("Windows only: desktop capture uses WASAPI loopback.");
                yield break;
            }

            Status = "Looking for AudioLink in the scene...";
            while ((link = ResolveAudioLink()) == null) yield return new WaitForSecondsRealtime(1f);
            sourceField = link.GetType().GetField("audioSource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (sourceField == null || sourceField.FieldType != typeof(AudioSource))
            {
                Fail("This AudioLink version has no 'audioSource' field; the tool needs updating.");
                yield break;
            }
            string takeOverError = TakeOverSource();
            if (takeOverError != null) { Fail(takeOverError); yield break; }

            sampleRate = AudioSettings.outputSampleRate;
            int bufferCount;
            AudioSettings.GetDSPBufferSize(out dspBufferFrames, out bufferCount);
            detector = new ToneDetector(sampleRate, sampleRate * 2);
            capture = new DesktopAudioCapture(sampleRate);
            capture.Tap = detector.Add;
            feed = new CarrierFeed(sampleRate) { Capture = capture };
            capture.Start();

            Status = "Opening desktop audio...";
            while (!capture.Running)
            {
                if (capture.GaveUp) { Fail("Desktop capture failed: " + capture.LastError); yield break; }
                if (capture.WaitingForPermission)
                    Status = "Waiting for permission to capture audio. Look for an antivirus or Windows prompt and allow Unity.";
                else if (capture.LastError != null)
                    Status = "Retrying desktop capture: " + capture.LastError;
                yield return null;
            }
            DeviceName = capture.DeviceName;
            DeviceFormat = capture.Format;

            yield return SafetyCheck();
            if (!checkPassed) yield break;

            // Audio buffered during the check is skipped by the first read; live feeding starts current.
            capture.Tap = null;
            feed.AutoLevel = autoLevel;
            feed.Current = CarrierFeed.Mode.Desktop;
            Feeding = true;
            Debug.Log("[AudioLink Desktop Audio] Feeding AudioLink from " + DeviceName
                + ". Unity's own audio output is muted while this runs. " + CheckSummary, this);
        }

        // ------------------------------------------------------------ safety check

        IEnumerator SafetyCheck()
        {
            var report = new StringBuilder();
            report.AppendLine("AudioLink Desktop Audio safety check, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("Capture: " + capture.DeviceName + ", " + capture.Format);
            report.AppendLine("Unity output: " + sampleRate + " Hz, " + AudioSettings.speakerMode + ", DSP buffer " + dspBufferFrames
                + (EditorAudioMuted() ? ", Game view audio muted" : ""));
            report.AppendLine("AudioLink source: " + (createdSource
                ? "own source on '" + PathOf(source.transform) + "'" + (originalSource != null ? "; AudioLink's '" + PathOf(originalSource.transform) + "' left alone, reconnected afterwards" : "")
                : "borrowed '" + PathOf(source.transform) + "'"));
            report.AppendLine("AudioListener.volume at start: " + AudioListener.volume);
            report.AppendLine("Audio listeners: " + DescribeListeners());
            report.AppendLine("Test tone: " + CarrierFeed.ToneHz + " Hz at " + CarrierFeed.ToneAmplitude + " peak; leak limit " + CarrierFeed.LeakThreshold);
            report.AppendLine();

            Status = "Checking that Unity's own output is muted...";
            float minRms = CarrierFeed.ToneAmplitude * 0.7071f * 0.5f;
            bool anyVisible = false, anyLeak = false;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (attempt == 2)
                {
                    if (anyVisible || anyLeak) break; // the retry only covers a first attempt where nothing arrived
                    report.AppendLine("AudioLink saw nothing; retrying once after a second.");
                    yield return new WaitForSecondsRealtime(1f);
                }
                for (int k = 0; k < ComboCarriers.Length; k++)
                {
                    CarrierKind c = ComboCarriers[k];
                    MuteKind m = ComboMutes[k];
                    ApplyMute(m); // mute first, so the tone never plays unmuted
                    StartCarrier(c);
                    yield return MeasureTone();

                    bool visible = lastRms > minRms;
                    bool measured = lastFrames >= sampleRate / 4;
                    bool leaked = measured && lastLeak > CarrierFeed.LeakThreshold;
                    string verdict = !visible ? (lastMix > 0.01f ? "tone reaches Unity's mix, but AudioLink's read of the source is empty" : "AudioLink cannot see the signal")
                        : leaked ? "Unity output leaks into the capture"
                        : measured ? "OK" : "OK (no loopback data, so no feedback path)";
                    report.AppendLine("Attempt " + attempt + ", " + Name(c) + " + " + Name(m) + ": AudioLink RMS " + lastRms.ToString("F4")
                        + " (needs > " + minRms.ToString("F4") + "), source " + (lastPlaying ? "playing" : "NOT playing")
                        + (lastVirtual ? " (virtual)" : "") + ", carrier callbacks " + lastCalls
                        + ", carrier wrote peak " + lastWritten.ToString("F4")
                        + ", Unity mix peak " + (lastMix < 0 ? "not measured" : lastMix.ToString("F4"))
                        + ", loopback " + lastFrames + " frames, tone in loopback " + lastLeak.ToString("F5")
                        + " (limit " + CarrierFeed.LeakThreshold.ToString("F5") + ") -> " + verdict);
                    anyVisible |= visible;
                    anyLeak |= leaked;

                    if (visible && !leaked)
                    {
                        Mute = m;
                        Carrier = c;
                        checkPassed = true;
                        CheckSummary = "Passed: " + Name(c) + " with " + Name(m) + (attempt > 1 ? " on the second attempt" : "") + ".";
                        // Auto level may not lift a leak above the same -12 dB loop gain the check allows at unity.
                        feed.MaxGain = Mathf.Clamp(CarrierFeed.LeakThreshold / Mathf.Max(lastLeak, 1e-6f), 1f, CarrierFeed.MaxBoost);
                        LevelBoostLimitDb = 20f * Mathf.Log10(feed.MaxGain);
                        report.AppendLine().AppendLine(CheckSummary);
                        report.AppendLine("Auto level limit: +" + LevelBoostLimitDb.ToString("0") + " dB (keeps loop gain at or below -12 dB)");
                        WriteReport(report);
                        yield break;
                    }
                    StopCarrier();
                    RemoveMute(false);
                    yield return null;
                }
            }

            string why = !anyVisible
                ? "AudioLink could not see the test signal." + (EditorAudioMuted() ? " Game view audio is muted, which may be the cause; unmute it (this tool keeps Unity silent anyway)." : "")
                : anyLeak ? "Unity's own output could not be muted, so feeding would echo. If music near 1 kHz was playing, pause it and re-enable this component."
                : "No method passed.";
            CheckSummary = "Failed: " + why;
            report.AppendLine().AppendLine(CheckSummary);
            WriteReport(report);
            Fail(why + " Details: " + ReportPath);
        }

        IEnumerator MeasureTone()
        {
            detector.Arm(sampleRate / 4); // skip the loopback's own latency
            int callsBefore = carrier != null ? carrier.Calls : 0;
            if (carrier != null) carrier.WrittenPeak = 0f;
            clipWrittenPeak = 0f;
            foreach (var mute in mutes) if (mute != null) mute.Peak = 0f;
            feed.Current = CarrierFeed.Mode.Tone;
            float start = Time.realtimeSinceStartup;
            double sum = 0;
            int count = 0;
            lastPlaying = true;
            lastVirtual = false;
            while (Time.realtimeSinceStartup - start < 1.1f)
            {
                EnforceMute();
                if (Time.realtimeSinceStartup - start > 0.25f && source != null)
                {
                    source.GetOutputData(probe, 0);
                    double s = 0;
                    for (int i = 0; i < probe.Length; i++) s += probe[i] * probe[i];
                    sum += Math.Sqrt(s / probe.Length);
                    count++;
                    lastPlaying &= source.isPlaying;
                    lastVirtual |= source.isVirtual;
                }
                yield return null;
            }
            feed.Current = CarrierFeed.Mode.Silence;
            detector.Disarm();
            lastCalls = carrier != null ? carrier.Calls - callsBefore : 0;
            lastWritten = carrier != null ? carrier.WrittenPeak : clipWrittenPeak;
            lastMix = -1f;
            foreach (var mute in mutes) if (mute != null && mute.enabled) lastMix = Mathf.Max(lastMix, mute.Peak);
            lastRms = count > 0 ? (float)(sum / count) : 0f;
            lastFrames = detector.Frames;
            lastLeak = (float)detector.Amplitude(CarrierFeed.ToneHz);
        }

        // ------------------------------------------------------------ live status

        void Update()
        {
            if (!Application.isPlaying) return;
            if (muteKind != MuteKind.None) EnforceMute();
            PumpClip();
            if (capture == null) return;

            float decay = Mathf.Exp(-Time.unscaledDeltaTime * 6f);
            DesktopLevel = Mathf.Max(capture.TakePeak(), DesktopLevel * decay);
            LatencyMs = (capture.AverageBacklogFrames + (carrierKind == CarrierKind.Clip ? leadFrames : dspBufferFrames)) * 1000f / Mathf.Max(1, sampleRate);
            DropoutFrames = capture.UnderrunFrames;
            DropoutMs = DropoutFrames * 1000f / Mathf.Max(1, sampleRate);
            BufferMs = capture.CushionFrames * 1000f / Mathf.Max(1, sampleRate);
            if (!Feeding) return;

            feed.AutoLevel = autoLevel;
            LevelBoostDb = 20f * Mathf.Log10(feed.Gain);
            if (source != null)
            {
                source.GetOutputData(probe, 0);
                float peak = 0f;
                for (int i = 0; i < probe.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(probe[i]));
                AudioLinkLevel = Mathf.Max(peak, AudioLinkLevel * decay);
                if (!source.isPlaying || source.clip != carrierClip)
                {
                    Status = "Another component took over AudioLink's audio source (a video player?). Disable and re-enable this component to resume.";
                    return;
                }
            }
            if (capture.GaveUp) Status = "Desktop capture stopped: " + capture.LastError;
            else if (!capture.Running) Status = "Reconnecting to the output device...";
            else if (capture.MillisecondsSinceLastPacket > 500 || DesktopLevel < 0.0005f) Status = "Feeding AudioLink. The desktop is silent; play something.";
            else Status = "Feeding AudioLink from " + capture.DeviceName + ".";
        }

        // ------------------------------------------------------------ mute

        void ApplyMute(MuteKind kind)
        {
            RemoveMute(false);
            muteKind = kind;
            if (kind == MuteKind.ListenerVolume)
            {
                if (!pendingListenerVolume.HasValue) pendingListenerVolume = AudioListener.volume;
                AudioListener.volume = 0f;
            }
            else if (kind == MuteKind.ListenerFilter) AttachListenerMutes();
        }

        void EnforceMute()
        {
            if (muteKind == MuteKind.ListenerVolume)
            {
                if (AudioListener.volume != 0f) AudioListener.volume = 0f;
            }
            else if (muteKind == MuteKind.ListenerFilter && ++muteRefresh >= 30)
            {
                muteRefresh = 0;
                AttachListenerMutes();
            }
        }

        void AttachListenerMutes()
        {
            foreach (var listener in FindListeners())
            {
                var m = listener.GetComponent<DesktopAudioListenerMute>();
                if (m == null)
                {
                    m = listener.gameObject.AddComponent<DesktopAudioListenerMute>();
                    m.hideFlags = HideFlags.NotEditable;
                }
                m.enabled = true;
                if (!mutes.Contains(m)) mutes.Add(m);
            }
            mutes.RemoveAll(x => x == null);
        }

        void RemoveMute(bool destroy)
        {
            muteKind = MuteKind.None;
            foreach (var m in mutes)
            {
                if (m == null) continue;
                m.enabled = false;
                if (destroy) Destroy(m);
            }
            if (destroy) mutes.Clear();
            RestoreGlobals();
        }

        /// <summary>Restores AudioListener.volume if this tool changed it. Also called by the editor on leaving Play mode.</summary>
        public static void RestoreGlobals()
        {
            if (!pendingListenerVolume.HasValue) return;
            AudioListener.volume = pendingListenerVolume.Value;
            pendingListenerVolume = null;
        }

        // ------------------------------------------------------------ AudioLink source

        string TakeOverSource()
        {
            var existing = sourceField.GetValue(link) as AudioSource;
            if (existing != null && IsUdonProxy(link))
            {
                // In Udon worlds the field cannot be redirected at runtime, so borrow AudioLink's own source.
                if (!existing.isActiveAndEnabled) return "AudioLink's audio source '" + existing.name + "' is disabled. Enable it, or clear AudioLink's Audio Source field.";
                source = existing;
                saved = SourceState.From(source);
                haveSaved = true;
            }
            else
            {
                // Use a separate source and point AudioLink at it. AudioLink's input source is also the
                // target of its video player, which can claim it; that source is left untouched.
                originalSource = existing;
                source = gameObject.AddComponent<AudioSource>();
                source.hideFlags = HideFlags.NotEditable;
                createdSource = true;
                sourceField.SetValue(link, source);
            }
            source.Stop();
            source.outputAudioMixerGroup = null; // a mixer route would bypass the listener mute
            source.bypassEffects = false;
            source.bypassListenerEffects = false;
            source.bypassReverbZones = true;
            source.volume = 1f;
            source.pitch = 1f;
            source.mute = false;
            source.spatialBlend = 0f;
            source.spatialize = false;
            source.priority = 0;
            source.loop = true;
            source.playOnAwake = false;
            return null;
        }

        void RestoreSource()
        {
            var s = source;
            source = null;
            if (s != null)
            {
                if (createdSource)
                {
                    if (link != null && sourceField != null && ReferenceEquals(sourceField.GetValue(link), s)) sourceField.SetValue(link, originalSource);
                    Destroy(s);
                }
                else if (haveSaved) saved.ApplyTo(s);
            }
            createdSource = haveSaved = false;
            originalSource = null;
        }

        // Filter carrier: a silent looping clip keeps the source playing and a filter writes the audio into it.
        // Lowest delay, but Unity does not always include filter output in GetOutputData, which AudioLink reads.
        // Clip carrier: the audio is written into a looping clip just ahead of the play position.
        // GetOutputData always sees clip data; the cost is roughly one frame of extra delay.
        void StartCarrier(CarrierKind kind)
        {
            StopCarrier();
            carrierKind = kind;
            feed.Current = CarrierFeed.Mode.Silence;
            if (kind == CarrierKind.Filter)
            {
                carrierClip = AudioClip.Create("Desktop Audio Carrier (silent)", sampleRate, 2, sampleRate, false);
                carrier = source.gameObject.AddComponent<DesktopAudioCarrier>();
                carrier.hideFlags = HideFlags.NotEditable;
                carrier.Feed = feed;
            }
            else
            {
                clipFrames = ChunkFrames * ((sampleRate + ChunkFrames - 1) / ChunkFrames);
                carrierClip = AudioClip.Create("Desktop Audio Carrier (live clip)", clipFrames, 2, sampleRate, false);
                pumpStarted = false;
            }
            source.clip = carrierClip;
            source.loop = true;
            source.Play();
        }

        // Clip carrier, every frame: keep the clip written just far enough ahead of the play position
        // to cover Unity's audio block plus one frame, so the mixer never reaches unwritten audio.
        void PumpClip()
        {
            if (carrierKind != CarrierKind.Clip || carrierClip == null || source == null || feed == null || !source.isPlaying) return;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1f / 240f, 0.1f);
            frameMax = Mathf.Max(dt, frameMax * 0.99f);
            leadFrames = dspBufferFrames + Mathf.CeilToInt(frameMax * 1.25f * sampleRate) + ChunkFrames;
            int play = source.timeSamples;
            int ahead = Wrap(writePos - play);
            if (!pumpStarted || ahead > clipFrames / 2)
            {
                // Start, or a hitch let the play position overtake what was written: continue just past the mixer.
                writePos = Wrap((play + dspBufferFrames + ChunkFrames - 1) / ChunkFrames * ChunkFrames);
                ahead = Wrap(writePos - play);
                pumpStarted = true;
            }
            while (ahead < leadFrames)
            {
                feed.Fill(chunk, 2);
                for (int i = 0; i < chunk.Length; i++) { float a = Math.Abs(chunk[i]); if (a > clipWrittenPeak) clipWrittenPeak = a; }
                carrierClip.SetData(chunk, writePos);
                writePos = Wrap(writePos + ChunkFrames);
                ahead += ChunkFrames;
            }
        }

        int Wrap(int frames) => ((frames % clipFrames) + clipFrames) % clipFrames;

        void StopCarrier()
        {
            if (feed != null) feed.Current = CarrierFeed.Mode.Silence;
            if (source != null)
            {
                source.Stop();
                if (source.clip == carrierClip) source.clip = null;
            }
            if (carrier != null)
            {
                carrier.Feed = null;
                carrier.enabled = false;
                Destroy(carrier);
                carrier = null;
            }
            if (carrierClip != null)
            {
                Destroy(carrierClip);
                carrierClip = null;
            }
        }

        // ------------------------------------------------------------ lifecycle helpers

        void Teardown(string finalStatus)
        {
            Feeding = false;
            Safe(StopCarrier);
            Safe(RestoreSource);
            Safe(() => RemoveMute(true));
            if (capture != null)
            {
                capture.Tap = null;
                Safe(capture.Stop);
                capture = null;
            }
            feed = null;
            detector = null;
            link = null;
            sourceField = null;
            DesktopLevel = AudioLinkLevel = 0f;
            if (finalStatus != null) Status = finalStatus;
        }

        void Fail(string message)
        {
            Teardown(message);
            if (CheckSummary == null) CheckSummary = message;
            if (!reportWritten)
            {
                var entry = new StringBuilder();
                entry.AppendLine("AudioLink Desktop Audio, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ": stopped before the safety check.");
                entry.AppendLine(message);
                WriteReport(entry);
            }
            Debug.LogWarning("[AudioLink Desktop Audio] " + message, this);
        }

        MonoBehaviour ResolveAudioLink()
        {
            if (audioLink != null)
            {
                if (IsAudioLink(audioLink)) return audioLink;
                foreach (var mb in audioLink.GetComponents<MonoBehaviour>()) if (IsAudioLink(mb)) return mb;
            }
            MonoBehaviour found = null;
            int count = 0;
            foreach (var mb in FindBehaviours())
            {
                if (!IsAudioLink(mb) || !mb.gameObject.activeInHierarchy) continue;
                if (found == null) found = mb;
                count++;
            }
            if (count > 1 && !warnedMultiple)
            {
                warnedMultiple = true;
                Debug.LogWarning("[AudioLink Desktop Audio] " + count + " AudioLinks in the scene; using '" + found.name + "'. Assign one to choose.", this);
            }
            return found;
        }

        static bool IsAudioLink(MonoBehaviour mb) => mb != null && mb.GetType().FullName == "AudioLink.AudioLink";

        static bool IsUdonProxy(MonoBehaviour mb)
        {
            for (var t = mb.GetType(); t != null; t = t.BaseType) if (t.Name == "UdonSharpBehaviour") return true;
            return false;
        }

        static MonoBehaviour[] FindBehaviours()
        {
#if UNITY_2022_3_OR_NEWER
            return FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            return FindObjectsOfType<MonoBehaviour>();
#endif
        }

        static AudioListener[] FindListeners()
        {
#if UNITY_2022_3_OR_NEWER
            return FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            return FindObjectsOfType<AudioListener>();
#endif
        }

        static string DescribeListeners()
        {
            var listeners = FindListeners();
            if (listeners.Length == 0) return "none";
            var parts = new List<string>();
            foreach (var l in listeners)
                parts.Add(PathOf(l.transform) + " [scene " + l.gameObject.scene.name + (l.enabled ? "" : ", component disabled") + "]");
            return listeners.Length + ": " + string.Join("; ", parts);
        }

        static bool EditorAudioMuted()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorUtility.audioMasterMute;
#else
            return false;
#endif
        }

        static string Name(CarrierKind c) => c == CarrierKind.Filter ? "filter carrier" : "live clip carrier";
        static string Name(MuteKind m) => m == MuteKind.ListenerFilter ? "listener filter mute" : m == MuteKind.ListenerVolume ? "listener volume mute" : "no mute";

        static string PathOf(Transform t)
        {
            string p = t.name;
            for (t = t.parent; t != null; t = t.parent) p = t.name + "/" + p;
            return p;
        }

        // Newest entry first; older history is kept below, capped in size.
        void WriteReport(StringBuilder entry)
        {
            reportWritten = true;
            try
            {
                string previous = File.Exists(ReportPath) ? File.ReadAllText(ReportPath) : "";
                if (previous.Length > 60000) previous = previous.Substring(0, 60000);
                File.WriteAllText(ReportPath, entry + "\n----------------------------------------\n\n" + previous);
            }
            catch (Exception e) { Debug.LogWarning("[AudioLink Desktop Audio] Could not write report: " + e.Message); }
        }

        static void Safe(Action action)
        {
            try { action(); }
            catch (Exception e) { Debug.LogWarning("[AudioLink Desktop Audio] Cleanup: " + e.Message); }
        }

        struct SourceState
        {
            AudioClip clip;
            bool loop, mute, spatialize, bypassEffects, bypassListenerEffects, bypassReverbZones, playOnAwake, playing;
            float volume, pitch, spatialBlend;
            int priority;
            AudioMixerGroup group;

            public static SourceState From(AudioSource s) => new SourceState
            {
                clip = s.clip, loop = s.loop, mute = s.mute, spatialize = s.spatialize,
                bypassEffects = s.bypassEffects, bypassListenerEffects = s.bypassListenerEffects,
                bypassReverbZones = s.bypassReverbZones, playOnAwake = s.playOnAwake, playing = s.isPlaying,
                volume = s.volume, pitch = s.pitch, spatialBlend = s.spatialBlend, priority = s.priority,
                group = s.outputAudioMixerGroup,
            };

            public void ApplyTo(AudioSource s)
            {
                s.Stop();
                s.clip = clip; s.loop = loop; s.mute = mute; s.spatialize = spatialize;
                s.bypassEffects = bypassEffects; s.bypassListenerEffects = bypassListenerEffects;
                s.bypassReverbZones = bypassReverbZones; s.playOnAwake = playOnAwake;
                s.volume = volume; s.pitch = pitch; s.spatialBlend = spatialBlend; s.priority = priority;
                s.outputAudioMixerGroup = group;
                if (playing && clip != null && s.isActiveAndEnabled) s.Play();
            }
        }
    }
}
