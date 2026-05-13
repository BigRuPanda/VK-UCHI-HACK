// Microphone.cs
// Adapted from unity-webgl-microphone-master (MIT License, Copyright 2018 Razer, Inc.)
// Provides UnityEngine.Microphone API shim for WebGL builds.
//
// Usage in SpeechBridge:
//   RequestPermission(): Microphone.Init()  (triggers getUserMedia)
//   Update():            Microphone.Update() + Microphone.GetVolumeDirect()

#if UNITY_WEBGL && !UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnityEngine
{
    /// <summary>
    /// WebGL shim for UnityEngine.Microphone.
    /// Wraps MicrophonePlugin.jslib which uses getUserMedia + AudioContext + AnalyserNode.
    /// </summary>
    public class Microphone
    {
        [DllImport("__Internal")] public  static extern void   Init();
        [DllImport("__Internal")] public  static extern void   QueryAudioInput();
        [DllImport("__Internal")] private static extern int    GetNumberOfMicrophones();
        [DllImport("__Internal")] private static extern string GetMicrophoneDeviceName(int index);
        [DllImport("__Internal")] private static extern float  GetMicrophoneVolume(int index);

        /// <summary>
        /// Returns document.volume directly from JS — does NOT depend on device enumeration.
        /// Use this for real-time level polling. Always valid after Init() succeeds.
        /// </summary>
        [DllImport("__Internal")] public  static extern float  GetMicVolumeDirect();

        private static List<Action> _sActions = new List<Action>();

        /// <summary>
        /// Must be called every frame (in MonoBehaviour.Update) to dispatch JS callbacks.
        /// </summary>
        public static void Update()
        {
            for (int i = 0; i < _sActions.Count; ++i)
                _sActions[i].Invoke();
        }

        /// <summary>Names of all detected audio input devices (populated after Init()).</summary>
        public static string[] devices
        {
            get
            {
                var list = new List<string>();
                int size = GetNumberOfMicrophones();
                for (int i = 0; i < size; ++i)
                    list.Add(GetMicrophoneDeviceName(i));
                return list.ToArray();
            }
        }

        /// <summary>
        /// Peak amplitude [0..1] per device. Requires device enumeration to be complete.
        /// Prefer GetVolumeDirect() for real-time level metering.
        /// </summary>
        public static float[] volumes
        {
            get
            {
                var list = new List<float>();
                int size = GetNumberOfMicrophones();
                for (int i = 0; i < size; ++i)
                    list.Add(GetMicrophoneVolume(i));
                return list.ToArray();
            }
        }

        // Stub methods to satisfy any code that uses the standard Microphone API
        public static bool IsRecording(string deviceName) => false;
        public static void GetDeviceCaps(string deviceName, out int minFreq, out int maxFreq)
        {
            minFreq = 0;
            maxFreq = 0;
        }
        public static void End(string deviceName) { }
    }
}

#endif
