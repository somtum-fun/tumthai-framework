// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform.Linux.Native;

namespace osu.Framework.Threading
{
    public class AudioThread : GameThread
    {
        public AudioThread()
            : base(name: "Audio")
        {
            OnNewFrame += onNewFrame;
            PreloadBass();
        }

        public override bool IsCurrent => ThreadSafety.IsAudioThread;

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsAudioThread = true;
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.TasksRun,
            StatisticsCounterType.Tracks,
            StatisticsCounterType.Samples,
            StatisticsCounterType.SChannels,
            StatisticsCounterType.Components,
            StatisticsCounterType.MixChannels,
        };

        private readonly List<AudioManager> managers = new List<AudioManager>();

        private static readonly HashSet<int> initialised_devices = new HashSet<int>();

        private static readonly GlobalStatistic<double> cpu_usage = GlobalStatistics.Get<double>("Audio", "Bass CPU%");

        /// <summary>
        /// Estimated audio output latency in milliseconds for the currently active audio pipeline.
        /// </summary>
        internal readonly Bindable<double> OutputLatency = new Bindable<double>();

        private long frameCount;

        private void onNewFrame()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Android && androidAAudioEnabled && AndroidAAudioBridge.ConsumeDisconnectRequest())
            {
                if (initialised_devices.Count > 0)
                    updateAndroidAAudio(notifyManagers: true);
            }

            if (frameCount % 200 == 0)
                OutputLatency.Value = getOutputLatencyMs();

            if (frameCount++ % 1000 == 0)
                cpu_usage.Value = Bass.CPUUsage;

            lock (managers)
            {
                for (int i = 0; i < managers.Count; i++)
                {
                    var m = managers[i];
                    m.Update();
                }
            }
        }

        internal void RegisterManager(AudioManager manager)
        {
            lock (managers)
            {
                if (managers.Contains(manager))
                    throw new InvalidOperationException($"{manager} was already registered");

                managers.Add(manager);
            }

            manager.GlobalMixerHandle.BindTo(globalMixerHandle);
        }

        internal void UnregisterManager(AudioManager manager)
        {
            lock (managers)
                managers.Remove(manager);

            manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
        }

        protected override void OnExit()
        {
            base.OnExit();

            lock (managers)
            {
                // AudioManagers are iterated over backwards since disposal will unregister and remove them from the list.
                for (int i = managers.Count - 1; i >= 0; i--)
                {
                    var m = managers[i];

                    m.Dispose();

                    // Audio component disposal (including the AudioManager itself) is scheduled and only runs when the AudioThread updates.
                    // But the AudioThread won't run another update since it's exiting, so an update must be performed manually in order to finish the disposal.
                    m.Update();
                }

                managers.Clear();
            }

            // Safety net to ensure we have freed all devices before exiting.
            // This is mainly required for device-lost scenarios.
            // See https://github.com/ppy/osu-framework/pull/3378 for further discussion.
            foreach (int d in initialised_devices.ToArray())
                FreeDevice(d);
        }

        #region BASS Initialisation

        // TODO: All this bass init stuff should probably not be in this class.

        private WasapiProcedure? wasapiProcedure;
        private WasapiNotifyProcedure? wasapiNotifyProcedure;
        private int? wasapiMixerHandle;

        private bool androidAAudioEnabled;
        private int androidPreferredSampleRate;

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        internal bool InitDevice(int deviceId, bool useExperimentalWasapi)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // Try to initialise the device, or request a re-initialise.
            if (!Bass.Init(deviceId, Flags: (DeviceInitFlags)128)) // 128 == BASS_DEVICE_REINIT
                return false;

            if (RuntimeInfo.OS == RuntimeInfo.Platform.Android)
            {
                updateAndroidAAudio(notifyManagers: false);
            }
            else if (useExperimentalWasapi)
            {
                attemptWasapiInitialisation();
            }
            else
            {
                freeWasapi();
            }

            initialised_devices.Add(deviceId);
            OutputLatency.Value = getOutputLatencyMs();

            // Clear any non-fatal BASS error code left by probing so AudioManager.InitBass does not report a false fault.
            Bass.GetDeviceInfo(0, out _);
            return true;
        }

        internal void FreeDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);

            int selectedDevice = Bass.CurrentDevice;

            if (canSelectDevice(deviceId))
            {
                Bass.CurrentDevice = deviceId;
                Bass.Free();
            }

            if (RuntimeInfo.OS == RuntimeInfo.Platform.Android)
            {
                AndroidAAudioBridge.Stop();
                globalMixerHandle.Value = null;
            }
            else
            {
                freeWasapi();
            }

            if (selectedDevice != deviceId && canSelectDevice(selectedDevice))
                Bass.CurrentDevice = selectedDevice;

            initialised_devices.Remove(deviceId);
            OutputLatency.Value = getOutputLatencyMs();

            static bool canSelectDevice(int deviceId) => Bass.GetDeviceInfo(deviceId, out var deviceInfo) && deviceInfo.IsInitialized;
        }

        internal void ConfigureAndroidAAudio(bool enabled, int preferredSampleRate = 0)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Android)
                return;

            void apply()
            {
                bool changed = androidAAudioEnabled != enabled || (preferredSampleRate > 0 && androidPreferredSampleRate != preferredSampleRate);
                androidAAudioEnabled = enabled;
                if (preferredSampleRate > 0)
                    androidPreferredSampleRate = preferredSampleRate;

                if (initialised_devices.Count == 0)
                    return;

                if (changed || (enabled && !AndroidAAudioBridge.IsActive) || (!enabled && AndroidAAudioBridge.IsActive))
                    updateAndroidAAudio(notifyManagers: true);
            }

            if (IsCurrent)
                apply();
            else
                Scheduler.Add(apply);
        }

        private void updateAndroidAAudio(bool notifyManagers)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Android)
                return;

            if (androidAAudioEnabled)
            {
                if (AndroidAAudioBridge.Start(androidPreferredSampleRate, out int mixerHandle))
                    globalMixerHandle.Value = mixerHandle;
                else
                    globalMixerHandle.Value = null;
            }
            else
            {
                AndroidAAudioBridge.Stop();
                globalMixerHandle.Value = null;
            }

            OutputLatency.Value = getOutputLatencyMs();

            if (notifyManagers && Bass.CurrentDevice > 0)
            {
                lock (managers)
                {
                    foreach (var m in managers)
                        m.UpdateDevice(Bass.CurrentDevice);
                }
            }
        }

        private double getOutputLatencyMs()
        {
            if (AndroidAAudioBridge.IsActive)
                return AndroidAAudioBridge.GetLatencyMs();

            if (initialised_devices.Count == 0)
                return 0;

            double latency = 0;

            try
            {
                if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows && wasapiMixerHandle.HasValue && BassWasapi.GetInfo(out WasapiInfo wasapiInfo) && wasapiInfo.Frequency > 0)
                {
                    int queuedBytes = BassWasapi.GetData(IntPtr.Zero, (int)DataFlags.Available);

                    if (queuedBytes > 0)
                    {
                        int bytesPerSample = wasapiInfo.Format switch
                        {
                            WasapiFormat.Float => 4,
                            WasapiFormat.Bit32 => 4,
                            WasapiFormat.Bit24 => 3,
                            WasapiFormat.Bit16 => 2,
                            WasapiFormat.Bit8 => 1,
                            _ => 2,
                        };

                        latency = queuedBytes / ((double)wasapiInfo.Frequency * Math.Max(1, wasapiInfo.Channels) * bytesPerSample) * 1000.0;
                    }
                }
                else if (Bass.GetInfo(out var bassInfo) && bassInfo.Latency > 0)
                {
                    latency = bassInfo.Latency;
                }
                else
                {
                    latency = Math.Max(0, Bass.DeviceBufferLength);
                }
            }
            catch
            {
                latency = 0;
            }

            Bass.GetDeviceInfo(0, out _);
            return latency;
        }

        /// <summary>
        /// Makes BASS available to be consumed.
        /// </summary>
        internal static void PreloadBass()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Linux)
            {
                // required for the time being to address libbass_fx.so load failures (see https://github.com/ppy/osu/issues/2852)
                Library.Load("libbass.so", Library.LoadFlags.RTLD_LAZY | Library.LoadFlags.RTLD_GLOBAL);
            }
        }

        private bool attemptWasapiInitialisation()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            Logger.Log("Attempting local BassWasapi initialisation");

            int wasapiDevice = -1;

            // WASAPI device indices don't match normal BASS devices.
            // Each device is listed multiple times with each supported channel/frequency pair.
            //
            // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
            if (Bass.CurrentDevice > 0)
            {
                string driver = Bass.GetDeviceInfo(Bass.CurrentDevice).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
                    // In the normal execution case, BassWasapi.GetDeviceInfo will return false as soon as we reach the end of devices.
                    // This while condition is just a safety to avoid looping forever.
                    // It's intentionally quite high because if a user has many audio devices, this list can get long.
                    //
                    // Retrieving device info here isn't free. In the future we may want to investigate a better method.
                    while (wasapiDevice < 16384)
                    {
                        if (!BassWasapi.GetDeviceInfo(++wasapiDevice, out WasapiDeviceInfo info))
                            break;

                        if (info.ID == driver)
                            break;
                    }
                }
            }

            // To keep things in a sane state let's only keep one device initialised via wasapi.
            freeWasapi();
            return initWasapi(wasapiDevice);
        }

        private bool initWasapi(int wasapiDevice)
        {
            // This is intentionally initialised inline and stored to a field.
            // If we don't do this, it gets GC'd away.
            wasapiProcedure = (buffer, length, _) =>
            {
                if (globalMixerHandle.Value == null)
                    return 0;

                return Bass.ChannelGetData(globalMixerHandle.Value!.Value, buffer, length);
            };
            wasapiNotifyProcedure = (notify, device, _) => Scheduler.Add(() =>
            {
                if (notify == WasapiNotificationType.DefaultOutput)
                {
                    freeWasapi();
                    initWasapi(device);
                }
            });

            bool initialised = BassWasapi.Init(wasapiDevice, Procedure: wasapiProcedure, Flags: WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat, Buffer: 0f, Period: float.Epsilon);
            Logger.Log($"Initialising BassWasapi for device {wasapiDevice}...{(initialised ? "success!" : "FAILED")}");

            if (!initialised)
                return false;

            BassWasapi.GetInfo(out var wasapiInfo);
            int mixer = BassMix.CreateMixerStream(wasapiInfo.Frequency, wasapiInfo.Channels, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
            wasapiMixerHandle = mixer;
            globalMixerHandle.Value = mixer;
            BassWasapi.Start();

            BassWasapi.SetNotify(wasapiNotifyProcedure);
            return true;
        }

        private void freeWasapi()
        {
            if (wasapiMixerHandle == null) return;

            // The mixer probably doesn't need to be recycled. Just keeping things sane for now.
            Bass.StreamFree(wasapiMixerHandle.Value);
            BassWasapi.Stop();
            BassWasapi.Free();
            wasapiMixerHandle = null;

            if (!AndroidAAudioBridge.IsActive)
                globalMixerHandle.Value = null;
        }

        #endregion
    }
}
