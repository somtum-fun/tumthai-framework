// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Logging;

namespace osu.Framework.Audio
{
    /// <summary>
    /// Low-latency Android audio output bridge using Android's native AAudio API (<c>libaaudio.so</c>) directly.
    /// Pulls decoded floating-point PCM data from a BASS global mixer stream inside the real-time AAudio callback.
    /// </summary>
    internal static unsafe class AndroidAAudioBridge
    {
        private const string aaudio_lib = "aaudio";

        private const int aaudio_ok = 0;
        private const int aaudio_direction_output = 0;
        private const int aaudio_format_pcm_float = 2;
        private const int aaudio_sharing_mode_exclusive = 0;
        private const int aaudio_sharing_mode_shared = 1;
        private const int aaudio_performance_mode_low_latency = 12;
        private const int aaudio_usage_game = 14;
        private const int aaudio_callback_result_continue = 0;
        private const int aaudio_clock_monotonic = 0;

        // See Start: a managed AAudio data callback crashes CFI-enabled libaaudio.
        private static readonly bool enabled_until_native_callback = false;

        private const int channel_count = 2;
        private const int bytes_per_frame = channel_count * sizeof(float);

        private static readonly object syncLock = new object();

        private static IntPtr activeStream;
        private static int activeMixerHandle;
        private static int activeSampleRate;
        private static int activeFramesPerBurst;
        private static int activeBufferSizeFrames;
        private static int activeSharingMode = aaudio_sharing_mode_shared;
        private static volatile bool disconnectRequested;

        public static bool IsActive => Volatile.Read(ref activeStream) != IntPtr.Zero && Volatile.Read(ref activeMixerHandle) != 0;

        public static int MixerHandle => Volatile.Read(ref activeMixerHandle);

        public static int SampleRate => Volatile.Read(ref activeSampleRate);

        public static bool IsExclusive => Volatile.Read(ref activeSharingMode) == aaudio_sharing_mode_exclusive;

        public static bool ConsumeDisconnectRequest()
        {
            if (!disconnectRequested)
                return false;

            disconnectRequested = false;
            return true;
        }

        public static bool Start(int preferredSampleRate, out int mixerHandle)
        {
            mixerHandle = 0;

            if (RuntimeInfo.OS != RuntimeInfo.Platform.Android)
                return false;

            // Off until the data callback lives in a native library. libaaudio is built with CFI on many devices
            // (Android 16 on Nothing phones, Pixels), so before every callback it checks the target's CFI shadow,
            // and &onAudioData is a runtime-generated .NET thunk in anonymous memory with no shadow: the check
            // itself segfaults (SIGSEGV SEGV_ACCERR in __cfi_slowpath on the AAudio_1 thread), seconds after start.
            // BASS's own output also uses AAudio, from its native code, so the game still gets it.
            if (!enabled_until_native_callback)
            {
                Logger.Log("[AAudio] Direct AAudio output is off (its callback can't pass Android's CFI check); using BASS output.");
                return false;
            }

            lock (syncLock)
            {
                Stop();

                if (tryOpenAndStart(preferredSampleRate, aaudio_sharing_mode_exclusive, out mixerHandle))
                    return true;

                if (tryOpenAndStart(preferredSampleRate, aaudio_sharing_mode_shared, out mixerHandle))
                    return true;

                return false;
            }
        }

        private static bool tryOpenAndStart(int preferredSampleRate, int sharingMode, out int mixerHandle)
        {
            mixerHandle = 0;
            IntPtr builder = IntPtr.Zero;
            IntPtr stream = IntPtr.Zero;
            int mixer = 0;

            try
            {
                int res = AAudio_createStreamBuilder(out builder);

                if (res != aaudio_ok || builder == IntPtr.Zero)
                {
                    Logger.Log($"[AAudio] Failed to create stream builder (error {res}).", LoggingTarget.Runtime, LogLevel.Important);
                    return false;
                }

                AAudioStreamBuilder_setDirection(builder, aaudio_direction_output);
                AAudioStreamBuilder_setSharingMode(builder, sharingMode);
                AAudioStreamBuilder_setPerformanceMode(builder, aaudio_performance_mode_low_latency);
                AAudioStreamBuilder_setUsage(builder, aaudio_usage_game);
                AAudioStreamBuilder_setFormat(builder, aaudio_format_pcm_float);
                AAudioStreamBuilder_setChannelCount(builder, channel_count);

                if (preferredSampleRate > 0)
                    AAudioStreamBuilder_setSampleRate(builder, preferredSampleRate);

                AAudioStreamBuilder_setDataCallback(builder, &onAudioData, IntPtr.Zero);
                AAudioStreamBuilder_setErrorCallback(builder, &onAudioError, IntPtr.Zero);

                res = AAudioStreamBuilder_openStream(builder, out stream);

                if (res != aaudio_ok || stream == IntPtr.Zero)
                {
                    Logger.Log($"[AAudio] openStream failed for sharingMode={sharingMode} (error {res}).");
                    return false;
                }

                int actualSampleRate = AAudioStream_getSampleRate(stream);
                int actualChannels = AAudioStream_getChannelCount(stream);
                int actualFormat = AAudioStream_getFormat(stream);

                if (actualSampleRate <= 0)
                    actualSampleRate = preferredSampleRate > 0 ? preferredSampleRate : 48000;

                if (actualChannels != channel_count || actualFormat != aaudio_format_pcm_float)
                {
                    Logger.Log($"[AAudio] Unexpected stream format (channels={actualChannels}, format={actualFormat}).");
                    AAudioStream_close(stream);
                    stream = IntPtr.Zero;
                    return false;
                }

                mixer = BassMix.CreateMixerStream(actualSampleRate, channel_count, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);

                if (mixer == 0)
                {
                    Logger.Log($"[AAudio] Failed to create BASS decode mixer at {actualSampleRate}Hz: {Bass.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    AAudioStream_close(stream);
                    stream = IntPtr.Zero;
                    return false;
                }

                Bass.ChannelSetAttribute(mixer, ChannelAttribute.Buffer, 0);

                int framesPerBurst = AAudioStream_getFramesPerBurst(stream);
                int capacity = AAudioStream_getBufferCapacityInFrames(stream);

                if (framesPerBurst > 0)
                {
                    int targetBufferFrames = framesPerBurst * 2;
                    if (capacity > 0 && targetBufferFrames > capacity)
                        targetBufferFrames = capacity;

                    AAudioStream_setBufferSizeInFrames(stream, targetBufferFrames);
                }

                int bufferSizeFrames = AAudioStream_getBufferSizeInFrames(stream);
                int actualSharing = AAudioStream_getSharingMode(stream);
                int actualPerf = AAudioStream_getPerformanceMode(stream);

                Volatile.Write(ref activeMixerHandle, mixer);
                Volatile.Write(ref activeStream, stream);
                Volatile.Write(ref activeSampleRate, actualSampleRate);
                Volatile.Write(ref activeFramesPerBurst, Math.Max(0, framesPerBurst));
                Volatile.Write(ref activeBufferSizeFrames, Math.Max(0, bufferSizeFrames));
                Volatile.Write(ref activeSharingMode, actualSharing);
                disconnectRequested = false;

                res = AAudioStream_requestStart(stream);

                if (res != aaudio_ok)
                {
                    Logger.Log($"[AAudio] requestStart failed for sharingMode={sharingMode} (error {res}).");
                    Volatile.Write(ref activeStream, IntPtr.Zero);
                    Volatile.Write(ref activeMixerHandle, 0);
                    AAudioStream_close(stream);
                    Bass.StreamFree(mixer);
                    stream = IntPtr.Zero;
                    mixer = 0;
                    return false;
                }

                mixerHandle = mixer;
                Logger.Log($"[AAudio] Started low-latency stream: {actualSampleRate}Hz, burst={framesPerBurst} frames, buffer={bufferSizeFrames} frames, sharing={(actualSharing == aaudio_sharing_mode_exclusive ? "Exclusive" : "Shared")}, perfMode={actualPerf}");
                return true;
            }
            catch (Exception e)
            {
                Logger.Log($"[AAudio] Exception during stream start: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);

                if (stream != IntPtr.Zero)
                {
                    try { AAudioStream_close(stream); }
                    catch { }
                }

                if (mixer != 0)
                    Bass.StreamFree(mixer);

                Volatile.Write(ref activeStream, IntPtr.Zero);
                Volatile.Write(ref activeMixerHandle, 0);
                return false;
            }
            finally
            {
                if (builder != IntPtr.Zero)
                {
                    try { AAudioStreamBuilder_delete(builder); }
                    catch { }
                }
            }
        }

        public static void Pause()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Android)
                return;

            lock (syncLock)
            {
                IntPtr stream = Volatile.Read(ref activeStream);
                if (stream == IntPtr.Zero)
                    return;

                try
                {
                    AAudioStream_requestPause(stream);
                }
                catch
                {
                }
            }
        }

        public static void Resume()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Android)
                return;

            lock (syncLock)
            {
                IntPtr stream = Volatile.Read(ref activeStream);
                if (stream == IntPtr.Zero)
                    return;

                try
                {
                    int res = AAudioStream_requestStart(stream);
                    if (res != aaudio_ok)
                        disconnectRequested = true;
                }
                catch
                {
                    disconnectRequested = true;
                }
            }
        }

        public static void Stop()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Android)
                return;

            lock (syncLock)
            {
                IntPtr stream = Interlocked.Exchange(ref activeStream, IntPtr.Zero);
                int mixer = Interlocked.Exchange(ref activeMixerHandle, 0);
                disconnectRequested = false;

                if (stream != IntPtr.Zero)
                {
                    try
                    {
                        AAudioStream_requestStop(stream);
                    }
                    catch
                    {
                    }

                    try
                    {
                        AAudioStream_close(stream);
                    }
                    catch
                    {
                    }
                }

                if (mixer != 0)
                {
                    try
                    {
                        Bass.StreamFree(mixer);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static double GetLatencyMs()
        {
            IntPtr stream = Volatile.Read(ref activeStream);
            int sampleRate = Volatile.Read(ref activeSampleRate);

            if (stream == IntPtr.Zero || sampleRate <= 0)
                return 0;

            int bufferFrames = Volatile.Read(ref activeBufferSizeFrames);
            int burstFrames = Volatile.Read(ref activeFramesPerBurst);
            double fallbackMs = bufferFrames > 0
                ? bufferFrames * 1000.0 / sampleRate
                : (burstFrames > 0 ? burstFrames * 2000.0 / sampleRate : 0);

            try
            {
                if (AAudioStream_getTimestamp(stream, aaudio_clock_monotonic, out long framePosition, out long frameTimeNs) == aaudio_ok)
                {
                    long framesWritten = AAudioStream_getFramesWritten(stream);
                    long nowNs = getMonotonicTimeNs();

                    if (nowNs > 0 && frameTimeNs > 0 && framesWritten >= framePosition)
                    {
                        long elapsedNs = nowNs - frameTimeNs;
                        double elapsedFrames = elapsedNs * (sampleRate / 1_000_000_000.0);
                        double currentReadFrame = framePosition + elapsedFrames;
                        double pendingFrames = framesWritten - currentReadFrame;
                        double latencyMs = pendingFrames * 1000.0 / sampleRate;

                        if (latencyMs >= 0.5 && latencyMs <= 200.0)
                            return latencyMs;
                    }
                }
            }
            catch
            {
            }

            return fallbackMs;
        }

        private static long getMonotonicTimeNs()
        {
            if (clock_gettime(1, out Timespec ts) == 0) // CLOCK_MONOTONIC = 1
                return ts.tv_sec * 1_000_000_000L + ts.tv_nsec;

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int onAudioData(IntPtr stream, IntPtr userData, IntPtr audioData, int numFrames)
        {
            if (audioData == IntPtr.Zero || numFrames <= 0)
                return aaudio_callback_result_continue;

            int byteLength = numFrames * bytes_per_frame;
            int mixer = Volatile.Read(ref activeMixerHandle);

            if (mixer == 0)
            {
                new Span<byte>((void*)audioData, byteLength).Clear();
                return aaudio_callback_result_continue;
            }

            int bytesRead = Bass.ChannelGetData(mixer, audioData, byteLength);

            if (bytesRead < 0)
                bytesRead = 0;

            if (bytesRead < byteLength)
                new Span<byte>((byte*)audioData + bytesRead, byteLength - bytesRead).Clear();

            return aaudio_callback_result_continue;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void onAudioError(IntPtr stream, IntPtr userData, int error)
        {
            // Per AAudio specifications, never close or reopen the stream on the callback thread.
            // Signal the AudioThread to rebuild the stream on its next frame.
            disconnectRequested = true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public nint tv_sec;
            public nint tv_nsec;
        }

        [DllImport("libc", ExactSpelling = true)]
        private static extern int clock_gettime(int clk_id, out Timespec tp);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudio_createStreamBuilder(out IntPtr builder);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStreamBuilder_delete(IntPtr builder);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setDirection(IntPtr builder, int direction);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setSharingMode(IntPtr builder, int sharingMode);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setPerformanceMode(IntPtr builder, int performanceMode);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setUsage(IntPtr builder, int usage);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setFormat(IntPtr builder, int format);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setChannelCount(IntPtr builder, int channelCount);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setSampleRate(IntPtr builder, int sampleRate);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setDataCallback(IntPtr builder, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int> callback, IntPtr userData);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern void AAudioStreamBuilder_setErrorCallback(IntPtr builder, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, void> callback, IntPtr userData);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStreamBuilder_openStream(IntPtr builder, out IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_close(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_requestStart(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_requestPause(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_requestStop(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getSampleRate(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getChannelCount(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getFormat(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getSharingMode(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getPerformanceMode(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getFramesPerBurst(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getBufferSizeInFrames(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getBufferCapacityInFrames(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_setBufferSizeInFrames(IntPtr stream, int numFrames);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern long AAudioStream_getFramesWritten(IntPtr stream);

        [DllImport(aaudio_lib, ExactSpelling = true)]
        private static extern int AAudioStream_getTimestamp(IntPtr stream, int clockid, out long framePosition, out long timeNanoseconds);
    }
}
