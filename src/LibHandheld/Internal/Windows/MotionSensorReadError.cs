namespace LibHandheld.Internal.Windows;

/// <summary>A failed report remains value data until a transition actually needs a diagnostic line.</summary>
internal readonly record struct MotionSensorReadError(
    MotionSensorErrorKind Kind,
    int HResult = 0,
    uint PropertyId = 0,
    ushort VariantType = 0)
{
    public override string ToString()
    {
        return $"{Kind}: HRESULT=0x{HResult:X8}, field={PropertyId}, variant={VariantType}";
    }
}

internal enum MotionSensorErrorKind
{
    Closed,
    GyrometerData,
    AccelerometerData,
    Timestamp,
    Field,
    VariantType,
    NonFiniteValue,
    NativeFailure
}
