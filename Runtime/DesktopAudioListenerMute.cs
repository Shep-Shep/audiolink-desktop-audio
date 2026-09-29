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
  SOURCE: DesktopAudioListenerMute.cs
  ROLE: Runtime helper beside the AudioListener that silences Unity's own output
  =============================================
  (c) 2026 Shep Shep. MIT License, see LICENSE.md.
  SHEP SHEP is a registered trademark in Germany.
  =============================================
*/

using UnityEngine;

namespace ShepShep.AudioTools
{
    /// <summary>Added at runtime beside the AudioListener; silences Unity's final output.</summary>
    [AddComponentMenu("")]
    public sealed class DesktopAudioListenerMute : MonoBehaviour
    {
        /// <summary>Loudest sample in Unity's mix before silencing, since last reset. Diagnostic only.</summary>
        [System.NonSerialized] public float Peak;

        void OnAudioFilterRead(float[] data, int channels)
        {
            float p = Peak;
            for (int i = 0; i < data.Length; i++) { float a = data[i] < 0f ? -data[i] : data[i]; if (a > p) p = a; }
            Peak = p;
            System.Array.Clear(data, 0, data.Length);
        }
    }
}
