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
  SOURCE: DesktopAudioCarrier.cs
  ROLE: Runtime helper that writes the desktop feed into AudioLink's audio source
  =============================================
  (c) 2026 Shep Shep. MIT License, see LICENSE.md.
  SHEP SHEP is a registered trademark in Germany.
  =============================================
*/

using UnityEngine;

namespace ShepShep.AudioTools
{
    /// <summary>Added at runtime to the audio source AudioLink reads; writes the desktop feed into it.</summary>
    [AddComponentMenu("")]
    public sealed class DesktopAudioCarrier : MonoBehaviour
    {
        [System.NonSerialized] public volatile CarrierFeed Feed;
        /// <summary>Audio callbacks so far; shows in the check report whether Unity's mixer ran.</summary>
        [System.NonSerialized] public int Calls;
        /// <summary>Loudest sample written since last reset; proves the carrier produced the test tone.</summary>
        [System.NonSerialized] public float WrittenPeak;

        void OnAudioFilterRead(float[] data, int channels)
        {
            Calls++;
            var feed = Feed;
            if (feed == null) { System.Array.Clear(data, 0, data.Length); return; }
            feed.Fill(data, channels);
            float p = WrittenPeak;
            for (int i = 0; i < data.Length; i++) { float a = data[i] < 0f ? -data[i] : data[i]; if (a > p) p = a; }
            WrittenPeak = p;
        }
    }
}
