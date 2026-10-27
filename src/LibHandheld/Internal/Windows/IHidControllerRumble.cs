namespace LibHandheld.Internal.Windows;

/// <summary>A controller-owned HID motor output, stopped and closed with its input lifecycle.</summary>
internal interface IHidControllerRumble : IDisposable
{
    void Write(float lowFrequency, float highFrequency);
}
