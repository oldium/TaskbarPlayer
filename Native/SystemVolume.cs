using System.Runtime.InteropServices;

namespace TaskbarPlayer.Native;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid FormatId;
    public int PropertyId;
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PROPERTYKEY key);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notifyData);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>
/// The volume of the default output device. The connection to it is kept, and Windows tells when the volume or the
/// default device changes, so nothing has to be asked again and again. To be used from one thread (the UI thread).
/// </summary>
internal static class SystemVolume
{
    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const int CLSCTX_ALL = 23;

    private static Guid s_context = Guid.NewGuid();

    private static IMMDeviceEnumerator? s_enumerator;
    private static IAudioEndpointVolume? s_volume;
    private static Listener? s_listener;
    // Set from Windows' threads when another device became the default: the kept connection is then the wrong one.
    private static volatile bool s_stale;

    public static (float Level, bool Muted)? Get()
    {
        return Use(v =>
        {
            if (v.GetMasterVolumeLevelScalar(out float level) != 0 || v.GetMute(out bool muted) != 0)
            {
                // The device may be gone: connect anew next time.
                s_stale = true;
                return ((float, bool)?)null;
            }
            return (level, muted);
        });
    }

    public static void SetLevel(float level) =>
        Use(v => v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref s_context));

    public static void SetMuted(bool muted) =>
        Use(v => v.SetMute(muted, ref s_context));

    /// <summary>
    /// From now on <paramref name="changed"/> is called when the volume, the mute state or the default device
    /// changes. It is called on threads of Windows' choosing and has to hand over to the right one itself.
    /// False when Windows refused: the volume then has to be asked for as before.
    /// </summary>
    public static bool Watch(Action changed)
    {
        StopWatching();
        try
        {
            var listener = new Listener(changed);
            s_enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (s_enumerator.RegisterEndpointNotificationCallback(listener) != 0) return false;
            s_listener = listener;
            // Connect anew, this time with the listener on the device.
            s_stale = true;
            return Endpoint() is not null;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public static void StopWatching()
    {
        var listener = s_listener;
        if (listener is null) return;
        s_listener = null;
        try
        {
            s_volume?.UnregisterControlChangeNotify(listener);
            s_enumerator?.UnregisterEndpointNotificationCallback(listener);
        }
        catch (COMException)
        {
        }
    }

    private static T? Use<T>(Func<IAudioEndpointVolume, T> action)
    {
        try
        {
            var volume = Endpoint();
            return volume is null ? default : action(volume);
        }
        catch (COMException)
        {
            s_stale = true;
            return default;
        }
    }

    // The kept connection to the default device, made anew after the default device changed.
    private static IAudioEndpointVolume? Endpoint()
    {
        if (s_volume is not null && !s_stale) return s_volume;

        if (s_volume is not null)
        {
            if (s_listener is not null) s_volume.UnregisterControlChangeNotify(s_listener);
            Marshal.ReleaseComObject(s_volume);
            s_volume = null;
        }
        s_stale = false;

        s_enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (s_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0 || device is null) return null;
        try
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o) != 0) return null;
            s_volume = (IAudioEndpointVolume)o;
            if (s_listener is not null) s_volume.RegisterControlChangeNotify(s_listener);
            return s_volume;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private sealed class Listener(Action changed) : IMMNotificationClient, IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            changed();
            return 0;
        }

        public int OnDefaultDeviceChanged(int dataFlow, int role, string? defaultDeviceId)
        {
            if (dataFlow != eRender || role != eMultimedia) return 0;
            s_stale = true;
            changed();
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnPropertyValueChanged(string deviceId, PROPERTYKEY key) => 0;
    }
}
