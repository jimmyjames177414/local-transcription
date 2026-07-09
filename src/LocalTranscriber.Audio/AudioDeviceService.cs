using NAudio.CoreAudioApi;

namespace LocalTranscriber.Audio;

public sealed class AudioDeviceService : IAudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> ListInputDevices() => ListDevices(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> ListOutputDevices() => ListDevices(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> ListDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string defaultId = "";
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // no devices of this kind present
        }

        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    IsInput: flow == DataFlow.Capture,
                    IsDefault: device.ID == defaultId));
            }
        }

        return result;
    }
}
