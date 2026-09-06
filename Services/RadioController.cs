using Windows.Devices.Radios;

namespace WinUI3Desktop;

/// <summary>WLAN / 蓝牙无线电状态查询与开关（Windows.Devices.Radios API）。</summary>
public static class RadioController
{
    public readonly record struct RadioStateInfo(bool Available, bool IsOn);

    public static async Task<RadioStateInfo> GetStateAsync(RadioKind kind)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                return new RadioStateInfo(false, false);
            }

            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(item => item.Kind == kind);
            if (radio is null)
            {
                return new RadioStateInfo(false, false);
            }

            return new RadioStateInfo(true, radio.State == RadioState.On);
        }
        catch
        {
            return new RadioStateInfo(false, false);
        }
    }

    public static async Task<RadioStateInfo> SetStateAsync(RadioKind kind, bool on)
    {
        try
        {
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(item => item.Kind == kind);
            if (radio is null)
            {
                return new RadioStateInfo(false, false);
            }

            var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            var applied = result == RadioAccessStatus.Allowed;
            return new RadioStateInfo(true, applied ? on : radio.State == RadioState.On);
        }
        catch
        {
            return new RadioStateInfo(false, false);
        }
    }
}
