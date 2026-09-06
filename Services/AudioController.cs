using System.Runtime.InteropServices;

namespace WinUI3Desktop;

/// <summary>系统主音量控制（CoreAudio IAudioEndpointVolume COM 互操作，无需管理员权限）。</summary>
public static class AudioController
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const int ClsctxAll = 0x17;

    public static bool TryGetVolume(out double percent, out bool muted)
    {
        percent = 0;
        muted = false;
        try
        {
            var endpoint = GetEndpointVolume();
            endpoint.GetMasterVolumeLevelScalar(out var level);
            endpoint.GetMute(out var mute);
            percent = Math.Clamp(level * 100d, 0, 100);
            muted = mute;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetVolume(double percent)
    {
        try
        {
            GetEndpointVolume().SetMasterVolumeLevelScalar((float)Math.Clamp(percent, 0, 100) / 100f, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetMute(bool mute)
    {
        try
        {
            GetEndpointVolume().SetMute(mute, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AudioEndpointVolume GetEndpointVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
        var interfaceId = typeof(AudioEndpointVolume).GUID;
        device.Activate(ref interfaceId, ClsctxAll, IntPtr.Zero, out var endpointObject);
        return (AudioEndpointVolume)endpointObject;
    }

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.Interface)] out AudioEndpointVolume endpoint);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr propertyStore);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, IntPtr context);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, IntPtr context);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, IntPtr context);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(IntPtr context);

        [PreserveSig]
        int VolumeStepDown(IntPtr context);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupport);

        [PreserveSig]
        int GetVolumeRange(out float min, out float max, out float increment);
    }
}
