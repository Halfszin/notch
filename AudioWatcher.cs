using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Orbi;

// Avisa quando a saída de som padrão muda (ex.: fone Bluetooth conectou). Chega numa thread do Windows.
sealed class AudioWatcher : IMMNotificationClient
{
    public event Action<string>? DefaultOutputChanged;

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // O Windows avisa uma vez por "papel"; Multimídia é o que toca música.
        if (flow == DataFlow.Render && role == Role.Multimedia) DefaultOutputChanged?.Invoke(defaultDeviceId);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
