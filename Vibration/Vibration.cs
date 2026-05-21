////////////////////////////////////////////////////////////////////////////////
//
// @author Benoît Freslon @benoitfreslon
// https://github.com/BenoitFreslon/Vibration
// https://benoitfreslon.com
//
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using UnityEngine;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

#if UNITY_IOS
using System.Collections;
using System.Runtime.InteropServices;
#endif

#if UNITY_WEBGL
using System.Runtime.InteropServices;
#endif

namespace Vibrations
{
    public static class Vibration
    {

#if UNITY_IOS
        [DllImport ( "__Internal" )]
        private static extern bool _HasVibrator ();

        [DllImport ( "__Internal" )]
        private static extern void _Vibrate ();

        [DllImport ( "__Internal" )]
        private static extern void _VibratePop ();

        [DllImport ( "__Internal" )]
        private static extern void _VibratePeek ();

        [DllImport ( "__Internal" )]
        private static extern void _VibrateNope ();

        [DllImport("__Internal")]
        private static extern void _impactOccurred(string style);

        [DllImport("__Internal")]
        private static extern void _notificationOccurred(string style);

        [DllImport("__Internal")]
        private static extern void _selectionChanged();
#endif

#if UNITY_ANDROID
        public static AndroidJavaClass unityPlayer;
        public static AndroidJavaObject currentActivity;
        public static AndroidJavaObject vibrator;
        public static IntPtr vibratorPtr;

        public static AndroidJavaObject context;

        public static AndroidJavaClass vibrationEffect;

        private static Dictionary<ImpactFeedbackStyle, CachedValues> cachedJNIValues = new Dictionary<ImpactFeedbackStyle, CachedValues>();

        struct CachedValues
        {
            public unsafe UnityEngine.jvalue* oneShot;
            public unsafe UnityEngine.jvalue* oneShotMs;
            /// <summary>
            /// JNI global ref for the cached <see cref="VibrationEffect"/> (API 26+ only).
            /// Kept so the ref is valid across threads / envs once promoted from the local returned by createOneShot.
            /// </summary>
            public IntPtr oneShotEffectGlobalRef;
        }
#endif

        public class Settings
        {
            public struct Preset
            {
                public Preset(long durationMS, float strength)
                {
                    DurationMS = durationMS;
                    Strength = strength;
                }
                
                public long DurationMS;
                public float Strength;
            }

            public Dictionary<ImpactFeedbackStyle, Preset> AndroidEventDurations = new Dictionary<ImpactFeedbackStyle, Preset>()
            {
                { ImpactFeedbackStyle.Selection, new Preset(25L, 0.6f) },
                { ImpactFeedbackStyle.Success, new Preset(25L, 0.6f) },
                { ImpactFeedbackStyle.Warning, new Preset(100L, -1f) },
                { ImpactFeedbackStyle.Error, new Preset(500L, -1f) },
                { ImpactFeedbackStyle.Light, new Preset(25L, 0.6f) },
                { ImpactFeedbackStyle.Medium, new Preset(30L, 0.8f) },
                { ImpactFeedbackStyle.Heavy, new Preset(35L, 0.9f) },
                { ImpactFeedbackStyle.Rigid, new Preset(25L, 0.6f) },
                { ImpactFeedbackStyle.Soft, new Preset(25L, 0.5f) },
            };
        }

        private static IntPtr vibrateMethodID = IntPtr.Zero;
        private static IntPtr vibrateEffectMethodID = IntPtr.Zero;

        private static AndroidJavaObject? mainLooper = null!;

#if UNITY_WEBGL
        [DllImport("__Internal")]
        private static extern void VibrateWebgl(int ms);
#endif

        private static bool initialized = false;
        public static bool IsPaused { get; private set; }


        public static void Init(Settings settings = null)
        {
            if (initialized) return;

            // Setup android version
            androidVersion = 0;
            if (Application.platform == RuntimePlatform.Android)
            {
                string androidVersionStr = SystemInfo.operatingSystem;
                int sdkPos = androidVersionStr.IndexOf("API-");
                androidVersion = int.Parse(androidVersionStr.Substring(sdkPos + 4, 2).ToString());
            }
            
            platform = Application.platform;
            isMobilePlatform = Application.isMobilePlatform;
            
            settings ??= new Settings();

            AndroidJNIHelper.debug = true;

#if UNITY_ANDROID
            if (isMobilePlatform)
            {

                unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                vibrator = currentActivity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                vibratorPtr = vibrator.GetRawObject();
                context = currentActivity.Call<AndroidJavaObject>("getApplicationContext");

                if (AndroidVersion >= 26)
                {
                    vibrationEffect = new AndroidJavaClass("android.os.VibrationEffect");
                }

                if (isMobilePlatform)
                {
                    if (Vibration.AndroidVersion >= 26)
                    {
                        unsafe
                        {
                            foreach (var preset in settings.AndroidEventDurations)
                            {
                                int amplitude = preset.Value.Strength < 0f
                                    ? -1
                                    : Mathf.RoundToInt(Mathf.Clamp01(preset.Value.Strength) * 255);
                                using var oneShotLocal = Vibration.vibrationEffect.CallStatic<AndroidJavaObject>("createOneShot", preset.Value.DurationMS, amplitude);
                                IntPtr localRef = oneShotLocal.GetRawObject();
                                if (localRef == IntPtr.Zero)
                                {
                                    Debug.LogWarning($"Vibration preset {preset.Key}: createOneShot returned null JNI ref.");
                                    continue;
                                }

                                IntPtr globalRef = AndroidJNI.NewGlobalRef(localRef);
                                if (globalRef == IntPtr.Zero)
                                {
                                    Debug.LogWarning($"Vibration preset {preset.Key}: AndroidJNI.NewGlobalRef failed.");
                                    continue;
                                }

                                UnityEngine.jvalue[] oneShotJNIArgs = new UnityEngine.jvalue[1];
                                oneShotJNIArgs[0].l = globalRef;

                                cachedJNIValues[preset.Key] = new CachedValues()
                                {
                                    oneShot = GetPointer(oneShotJNIArgs),
                                    oneShotEffectGlobalRef = globalRef
                                };
                            }
                        }

                        if (vibrateEffectMethodID == IntPtr.Zero)
                        {
                            vibrateEffectMethodID = AndroidJNIHelper.GetMethodID(
                                Vibration.vibrator.GetRawClass(),
                                "vibrate",
                                "(Landroid/os/VibrationEffect;)V"
                            );
                        }
                    }
                    else
                    {
                        unsafe
                        {
                            foreach (var preset in settings.AndroidEventDurations)
                            {
                                cachedJNIValues[preset.Key] = new CachedValues()
                                {
                                    oneShotMs = GetPointer(AndroidJNIHelper.CreateJNIArgArray(new object[] { preset.Value.DurationMS }))
                                };
                            }
                        }
                    }

                    if (vibrateMethodID == IntPtr.Zero)
                    {
                        vibrateMethodID = AndroidJNIHelper.GetMethodID(
                            Vibration.vibrator.GetRawClass(),
                            "vibrate",
                            "(J)V"
                        );
                    }
                }
            }
#endif

            initialized = true;
        }

        public static void Vibrate(ImpactFeedbackStyle style)
        {
            if (!IsAvailable())
                return;

#if UNITY_IOS
            Vibration.VibrateIOS(style);
#elif UNITY_ANDROID

            if (!cachedJNIValues.TryGetValue(style, out var cachedValue))
                return;
            
            unsafe
            {
                VibrateWithIntensityAndroid(cachedValue.oneShotMs, cachedValue.oneShot);
            }
#endif
        }

        public static async UniTaskVoid VibrateLooped(ImpactFeedbackStyle style, int _loop, int _delayMs = 50)
        {
            for (int i = 0; i < _loop; i++)
            {
                Vibrate(style);
                await UniTask.Delay(_delayMs);
            }
        }
        
        
        #region IOS specific methods

        public static void VibrateIOS(ImpactFeedbackStyle style)
        {
            if (!IsAvailable()) return;
#if UNITY_IOS
            if (style == ImpactFeedbackStyle.Error || style == ImpactFeedbackStyle.Warning || style == ImpactFeedbackStyle.Error)
            {
                _notificationOccurred(style.ToString());
            }
            else
            {
                _impactOccurred(style.ToString());
            }
#endif
        }

        public static void VibrateIOS_SelectionChanged()
        {
            if (!IsAvailable()) return;
#if UNITY_IOS
            _selectionChanged();
#endif
        }

        #endregion

        #region Android specific methods

#if UNITY_ANDROID

        /// <summary>
        /// Custom vibration that lets us pick intensity
        /// </summary>
        /// <param name="_milliseconds"></param>
        /// <param name="_intensity"></param>
        static unsafe void VibrateWithIntensityAndroid(UnityEngine.jvalue* _milliseconds, UnityEngine.jvalue* _oneShot)
        {
            if (isMobilePlatform && currentActivity != null && vibratorPtr != IntPtr.Zero)
            {
                // JNI object refs embedded in cached jvalue arrays were created on the Android UI /
                // Unity main thread; calling Vibrator through JNIEnv on another thread corrupts ART.
                void InvokeOnUiThread()
                {
                    try
                    {
                        if (AndroidVersion >= 26)
                        {
                            AndroidJNI.CallVoidMethodUnsafe(Vibration.vibratorPtr, vibrateEffectMethodID, _oneShot);
                        }
                        else
                        {
                            AndroidJNI.CallVoidMethodUnsafe(Vibration.vibratorPtr, vibrateMethodID, _milliseconds);
                        }
                    }
                    catch (System.Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }
                }

                currentActivity.Call("runOnUiThread", new AndroidJavaRunnable(InvokeOnUiThread));
            }
        }
#endif

        #endregion

        static unsafe jvalue* GetPointer(UnityEngine.jvalue[] _arr)
        {
            GCHandle handle = GCHandle.Alloc(_arr, GCHandleType.Pinned);
            IntPtr jvaluePtr = (IntPtr)handle.AddrOfPinnedObject();
            return (jvalue*)jvaluePtr.ToPointer();
        }

        ///<summary>
        /// Tiny pop vibration
        ///</summary>
        public static void VibratePop()
        {
            if (!IsAvailable()) return;
            if (isMobilePlatform)
            {
#if UNITY_IOS
            _VibratePop ();
#elif UNITY_ANDROID
                VibrateAndroid(50);
#elif UNITY_WEBGL
                VibrateWebgl ( 50 );
#endif
            }
        }

        ///<summary>
        /// Small peek vibration
        ///</summary>
        public static void VibratePeek()
        {
            if (!IsAvailable()) return;
            if (isMobilePlatform)
            {
#if UNITY_IOS
            _VibratePeek ();
#elif UNITY_ANDROID
                VibrateAndroid(100);
#elif UNITY_WEBGL
                VibrateWebgl ( 100 );
#endif
            }
        }

        ///<summary>
        /// 3 small vibrations
        ///</summary>
        public static async Task VibrateNope()
        {
            if (!IsAvailable()) return;
            if (isMobilePlatform)
            {
#if UNITY_IOS
            _VibrateNope ();
#elif UNITY_ANDROID
                long[] pattern = { 0, 50, 50, 50 };
                VibrateAndroid(pattern, -1);
#elif UNITY_WEBGL
                VibrateWebgl ( 50 );
                for (int i = 0; i < 50; i++)
                    await Task.Yield ();
                VibrateWebgl ( 50 );
                for (int i = 0; i < 50; i++)
                    await Task.Yield ();
                VibrateWebgl ( 50 );
#endif

            }
        }

        #region Android specific methods

#if UNITY_ANDROID
        ///<summary>
        /// Only on Android
        /// https://developer.android.com/reference/android/os/Vibrator.html#vibrate(long)
        ///</summary>
        public static void VibrateAndroid(long milliseconds)
        {
            if (!IsAvailable()) return;
            if (isMobilePlatform)
            {
                if (AndroidVersion >= 26)
                {
                    AndroidJavaObject createOneShot = vibrationEffect.CallStatic<AndroidJavaObject>("createOneShot", milliseconds, -1);
                    vibrator.Call("vibrate", createOneShot);
                }
                else
                {
                    vibrator.Call("vibrate", milliseconds);
                }
            }
        }

        ///<summary>
        /// Only on Android
        /// https://proandroiddev.com/using-vibrate-in-android-b0e3ef5d5e07
        ///</summary>
        public static void VibrateAndroid(long[] pattern, int repeat)
        {
            if (!IsAvailable()) return;
            if (isMobilePlatform)
            {
                if (AndroidVersion >= 26)
                {
                    long[] amplitudes;
                    AndroidJavaObject createWaveform = vibrationEffect.CallStatic<AndroidJavaObject>("createWaveform", pattern, repeat);
                    vibrator.Call("vibrate", createWaveform);
                }
                else
                {
                    vibrator.Call("vibrate", pattern, repeat);
                }
            }
        }
#endif

        ///<summary>
        ///Only on Android
        ///</summary>
        public static void CancelAndroid()
        {
            if (isMobilePlatform)
            {
#if UNITY_ANDROID
                vibrator.Call("cancel");
#endif
            }
        }

        #endregion

        public static bool HasVibrator()
        {
            if (isMobilePlatform)
            {
#if UNITY_WEBGL
            return true;
#endif
#if UNITY_ANDROID

                if (context == null)
                    return false;
                
                AndroidJavaClass contextClass = new AndroidJavaClass("android.content.Context");
                string Context_VIBRATOR_SERVICE = contextClass.GetStatic<string>("VIBRATOR_SERVICE");
                AndroidJavaObject systemService = context.Call<AndroidJavaObject>("getSystemService", Context_VIBRATOR_SERVICE);
                if (systemService.Call<bool>("hasVibrator"))
                {
                    return true;
                }
                else
                {
                    return false;
                }

#elif UNITY_IOS
            return _HasVibrator ();
#else
                return false;
#endif
            }
            else
            {
                return false;
            }
        }

        public static void Vibrate()
        {
            if (!IsAvailable()) return;
#if UNITY_ANDROID || UNITY_IOS
            if (isMobilePlatform)
            {
                Handheld.Vibrate();
            }
#elif UNITY_WEBGL
            if ( isMobilePlatform ) {
                VibrateWebgl ( 1 );
            }
#endif
        }

        private static int androidVersion;
        private static RuntimePlatform platform;
        private static bool isMobilePlatform;
        
        public static int AndroidVersion
        {
            get
            {
                return androidVersion;
            }
        }

        public static void SetActive(bool active)
        {
            IsPaused = !active;
            if (IsPaused)
            {
                CancelAndroid();
            }
        }

        public static bool IsAvailable()
        {
            if (!HasVibrator()) return false;
            if (IsPaused) return false;
            return true;
        }
    }

    public enum ImpactFeedbackStyle
    {
        Selection = 0,
        Success = 1,
        Warning = 2,
        Error = 3,
        Light = 4,
        Medium = 5,
        Heavy = 6,
        Rigid = 7,
        Soft = 8,
        None = -1
    }
}
