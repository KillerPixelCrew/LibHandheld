using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using P = LibHandheld.Contracts;

namespace LibHandheld;

internal static class HandheldConversions
{
    internal static P.CapabilityDescriptor ToPublic(CapabilityDescriptor value)
    {
        return new P.CapabilityDescriptor
        {
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            Role = (P.CapabilityRole)value.Role,
            ValueKind = (P.CapabilityValueKind)value.ValueKind,
            SupportsRead = value.SupportsRead,
            SupportsWrite = value.SupportsWrite,
            SupportsAction = value.SupportsAction,
            Minimum = value.Minimum,
            Maximum = value.Maximum,
            Step = value.Step,
            Unit = (P.CapabilityUnit)value.Unit,
            PairedPowerLimitId = value.PairedPowerLimitId,
            Choices = Array.AsReadOnly(value.Choices.Select(ToPublic).ToArray()),
            PowerPresets = Array.AsReadOnly(value.PowerPresets.Select(ToPublic).ToArray()),
            MaximumLength = value.MaximumLength,
            AvailableOnAc = value.AvailableOnAc,
            AvailableOnDc = value.AvailableOnDc,
            Persistence = (P.CapabilityPersistence)value.Persistence,
            ProfileScope = (P.CapabilityProfileScope)value.ProfileScope,
            ApplyTiming = (P.CapabilityApplyTiming)value.ApplyTiming,
            Label = Label(value.Display)
        };
    }

    internal static P.CapabilityDescriptorSet ToPublic(CapabilityDescriptorSet value)
    {
        return new P.CapabilityDescriptorSet
        {
            Generation = value.Generation,
            CycleGeneration = value.CycleGeneration,
            Descriptors = Array.AsReadOnly(value.Descriptors.Select(ToPublic).ToArray())
        };
    }

    internal static P.CapabilityState ToPublic(CapabilityState value)
    {
        return new P.CapabilityState
        {
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            Available = value.Available,
            Reason = value.Reason is { } itemReason ? ToPublic(itemReason) : null,
            ObservedValue = value.ObservedValue is { } itemObservedValue ? ToPublic(itemObservedValue) : null,
            Quality = (P.HardwareStateQuality)value.Quality,
            ObservedAt = value.ObservedAt,
            DescriptorGeneration = value.DescriptorGeneration,
            CycleGeneration = value.CycleGeneration
        };
    }

    internal static CapabilityState ToNative(P.CapabilityState value)
    {
        return new CapabilityState
        {
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            Available = value.Available,
            Reason = value.Reason is { } itemReason ? ToNative(itemReason) : null,
            ObservedValue = value.ObservedValue is { } itemObservedValue ? ToNative(itemObservedValue) : null,
            Quality = (HardwareStateQuality)value.Quality,
            ObservedAt = value.ObservedAt,
            DescriptorGeneration = value.DescriptorGeneration,
            CycleGeneration = value.CycleGeneration
        };
    }

    internal static P.CapabilityValue ToPublic(CapabilityValue value)
    {
        return new P.CapabilityValue
        {
            Kind = (P.CapabilityValueKind)value.Kind,
            BooleanValue = value.BooleanValue,
            IntegerValue = value.IntegerValue,
            ChoiceValue = value.ChoiceValue,
            ColorValue = value.ColorValue,
            CurveValue = Array.AsReadOnly(value.CurveValue.Select(ToPublic).ToArray()),
            TextValue = value.TextValue
        };
    }

    internal static CapabilityValue ToNative(P.CapabilityValue value)
    {
        return new CapabilityValue
        {
            Kind = (CapabilityValueKind)value.Kind,
            BooleanValue = value.BooleanValue,
            IntegerValue = value.IntegerValue,
            ChoiceValue = value.ChoiceValue,
            ColorValue = value.ColorValue,
            CurveValue = Array.AsReadOnly(value.CurveValue.Select(ToNative).ToArray()),
            TextValue = value.TextValue
        };
    }

    internal static P.CapabilityCommand ToPublic(CapabilityCommand value)
    {
        return new P.CapabilityCommand
        {
            CommandId = value.CommandId,
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            RequestedValue = value.RequestedValue is { } itemRequestedValue ? ToPublic(itemRequestedValue) : null,
            PairedPowerLimitWatts = value.PairedPowerLimitWatts,
            ExpectedDescriptorGeneration = value.ExpectedDescriptorGeneration,
            ExpectedCycleGeneration = value.ExpectedCycleGeneration,
            Deadline = P.Deadline.After(value.Deadline.Remaining)
        };
    }

    internal static CapabilityCommand ToNative(P.CapabilityCommand value)
    {
        return new CapabilityCommand
        {
            CommandId = value.CommandId,
            CapabilityId = value.CapabilityId,
            InstanceId = value.InstanceId,
            RequestedValue = value.RequestedValue is { } itemRequestedValue ? ToNative(itemRequestedValue) : null,
            PairedPowerLimitWatts = value.PairedPowerLimitWatts,
            ExpectedDescriptorGeneration = value.ExpectedDescriptorGeneration,
            ExpectedCycleGeneration = value.ExpectedCycleGeneration,
            Deadline = Deadline.After(value.Deadline.Remaining)
        };
    }

    internal static P.CapabilityCommandResult ToPublic(CapabilityCommandResult value)
    {
        return new P.CapabilityCommandResult
        {
            CommandId = value.CommandId,
            Outcome = (P.CommandOutcome)value.Outcome,
            Reason = value.Reason is { } itemReason ? ToPublic(itemReason) : null,
            ReadbackValue = value.ReadbackValue is { } itemReadbackValue ? ToPublic(itemReadbackValue) : null,
            Rollback = (P.RollbackResult)value.Rollback,
            CompletedAt = value.CompletedAt
        };
    }

    internal static CapabilityCommandResult ToNative(P.CapabilityCommandResult value)
    {
        return new CapabilityCommandResult
        {
            CommandId = value.CommandId,
            Outcome = (CommandOutcome)value.Outcome,
            Reason = value.Reason is { } itemReason ? ToNative(itemReason) : null,
            ReadbackValue = value.ReadbackValue is { } itemReadbackValue ? ToNative(itemReadbackValue) : null,
            Rollback = (RollbackResult)value.Rollback,
            CompletedAt = value.CompletedAt
        };
    }

    internal static P.CanonicalControllerSample ToPublic(CanonicalControllerSample value)
    {
        return new P.CanonicalControllerSample
        {
            Timestamp = value.Timestamp,
            Buttons = (P.CanonicalButtons)value.Buttons,
            LeftStickX = value.LeftStickX,
            LeftStickY = value.LeftStickY,
            RightStickX = value.RightStickX,
            RightStickY = value.RightStickY,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            LeftPadX = value.LeftPadX,
            LeftPadY = value.LeftPadY,
            LeftPadForce = value.LeftPadForce,
            RightPadX = value.RightPadX,
            RightPadY = value.RightPadY,
            RightPadForce = value.RightPadForce,
            LeftStickForce = value.LeftStickForce,
            RightStickForce = value.RightStickForce,
            Motion = value.Motion is { } itemMotion ? ToPublic(itemMotion) : null
        };
    }

    internal static CanonicalControllerSample ToNative(P.CanonicalControllerSample value)
    {
        return new CanonicalControllerSample
        {
            Timestamp = value.Timestamp,
            Buttons = (CanonicalButtons)value.Buttons,
            LeftStickX = value.LeftStickX,
            LeftStickY = value.LeftStickY,
            RightStickX = value.RightStickX,
            RightStickY = value.RightStickY,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            LeftPadX = value.LeftPadX,
            LeftPadY = value.LeftPadY,
            LeftPadForce = value.LeftPadForce,
            RightPadX = value.RightPadX,
            RightPadY = value.RightPadY,
            RightPadForce = value.RightPadForce,
            LeftStickForce = value.LeftStickForce,
            RightStickForce = value.RightStickForce,
            Motion = value.Motion is { } itemMotion ? ToNative(itemMotion) : null
        };
    }

    internal static P.MotionSample ToPublic(MotionSample value)
    {
        return new P.MotionSample
        {
            GyroX = value.GyroX,
            GyroY = value.GyroY,
            GyroZ = value.GyroZ,
            HasGyro = value.HasGyro,
            AccelX = value.AccelX,
            AccelY = value.AccelY,
            AccelZ = value.AccelZ,
            HasAccelerometer = value.HasAccelerometer,
            SensorTimestamp = value.SensorTimestamp
        };
    }

    internal static MotionSample ToNative(P.MotionSample value)
    {
        return new MotionSample
        {
            GyroX = value.GyroX,
            GyroY = value.GyroY,
            GyroZ = value.GyroZ,
            HasGyro = value.HasGyro,
            AccelX = value.AccelX,
            AccelY = value.AccelY,
            AccelZ = value.AccelZ,
            HasAccelerometer = value.HasAccelerometer,
            SensorTimestamp = value.SensorTimestamp
        };
    }

    internal static P.HapticOutputFrame ToPublic(HapticOutputFrame value)
    {
        return new P.HapticOutputFrame
        {
            LowFrequency = value.LowFrequency,
            HighFrequency = value.HighFrequency,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            Timestamp = value.Timestamp
        };
    }

    internal static HapticOutputFrame ToNative(P.HapticOutputFrame value)
    {
        return new HapticOutputFrame
        {
            LowFrequency = value.LowFrequency,
            HighFrequency = value.HighFrequency,
            LeftTrigger = value.LeftTrigger,
            RightTrigger = value.RightTrigger,
            Timestamp = value.Timestamp
        };
    }

    internal static P.HapticCapabilities ToPublic(HapticCapabilities value)
    {
        return new P.HapticCapabilities
        {
            LowFrequency = (P.OutputChannelSupport)value.LowFrequency,
            HighFrequency = (P.OutputChannelSupport)value.HighFrequency,
            LeftTrigger = (P.OutputChannelSupport)value.LeftTrigger,
            RightTrigger = (P.OutputChannelSupport)value.RightTrigger,
            MaxFramesPerSecond = value.MaxFramesPerSecond,
            MinimumStartIntensity = value.MinimumStartIntensity,
            MinimumPulse = value.MinimumPulse
        };
    }

    internal static HapticCapabilities ToNative(P.HapticCapabilities value)
    {
        return new HapticCapabilities
        {
            LowFrequency = (OutputChannelSupport)value.LowFrequency,
            HighFrequency = (OutputChannelSupport)value.HighFrequency,
            LeftTrigger = (OutputChannelSupport)value.LeftTrigger,
            RightTrigger = (OutputChannelSupport)value.RightTrigger,
            MaxFramesPerSecond = value.MaxFramesPerSecond,
            MinimumStartIntensity = value.MinimumStartIntensity,
            MinimumPulse = value.MinimumPulse
        };
    }

    internal static P.PhysicalDeviceIdentity ToPublic(PhysicalDeviceIdentity value)
    {
        return new P.PhysicalDeviceIdentity
        {
            InstancePath = value.InstancePath,
            LocationPath = value.LocationPath,
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            RequiresHiding = value.RequiresHiding
        };
    }

    internal static PhysicalDeviceIdentity ToNative(P.PhysicalDeviceIdentity value)
    {
        return new PhysicalDeviceIdentity
        {
            InstancePath = value.InstancePath,
            LocationPath = value.LocationPath,
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            RequiresHiding = value.RequiresHiding
        };
    }

    internal static P.OemControlDescriptor ToPublic(OemControlDescriptor value)
    {
        return new P.OemControlDescriptor
        {
            ControlId = value.ControlId,
            Placement = (P.OemControlPlacement)value.Placement,
            SupportsLongPress = value.SupportsLongPress,
            RequiresControllerAcquisition = value.RequiresControllerAcquisition,
            CompanionApplication = value.CompanionApplication,
            Label = Label(value.Display)
        };
    }

    internal static P.DeviceIdentitySnapshot ToPublic(DeviceIdentitySnapshot value)
    {
        return new P.DeviceIdentitySnapshot
        {
            SystemManufacturer = value.SystemManufacturer,
            SystemProduct = value.SystemProduct,
            SystemSku = value.SystemSku,
            SystemFamily = value.SystemFamily,
            BaseboardManufacturer = value.BaseboardManufacturer,
            BaseboardProduct = value.BaseboardProduct,
            BaseboardVersion = value.BaseboardVersion,
            BiosVersion = value.BiosVersion,
            EcFirmwareVersion = value.EcFirmwareVersion,
            McuFirmwareVersion = value.McuFirmwareVersion,
            CpuIdentity = value.CpuIdentity,
            ProcessorName = value.ProcessorName,
            UsbEndpoints = Array.AsReadOnly(value.UsbEndpoints.Select(ToPublic).ToArray()),
            WmiProviderSignatures = Array.AsReadOnly(value.WmiProviderSignatures.ToArray())
        };
    }

    internal static DeviceIdentitySnapshot ToNative(P.DeviceIdentitySnapshot value)
    {
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = value.SystemManufacturer,
            SystemProduct = value.SystemProduct,
            SystemSku = value.SystemSku,
            SystemFamily = value.SystemFamily,
            BaseboardManufacturer = value.BaseboardManufacturer,
            BaseboardProduct = value.BaseboardProduct,
            BaseboardVersion = value.BaseboardVersion,
            BiosVersion = value.BiosVersion,
            EcFirmwareVersion = value.EcFirmwareVersion,
            McuFirmwareVersion = value.McuFirmwareVersion,
            CpuIdentity = value.CpuIdentity,
            ProcessorName = value.ProcessorName,
            UsbEndpoints = Array.AsReadOnly(value.UsbEndpoints.Select(ToNative).ToArray()),
            WmiProviderSignatures = Array.AsReadOnly(value.WmiProviderSignatures.ToArray())
        };
    }

    internal static P.UsbEndpointObservation ToPublic(UsbEndpointObservation value)
    {
        return new P.UsbEndpointObservation
        {
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            InterfaceNumber = value.InterfaceNumber,
            DeviceRelease = value.DeviceRelease,
            ReportDescriptorHash = value.ReportDescriptorHash,
            ReportLengths = Array.AsReadOnly(value.ReportLengths.ToArray()),
            LocationPath = value.LocationPath
        };
    }

    internal static UsbEndpointObservation ToNative(P.UsbEndpointObservation value)
    {
        return new UsbEndpointObservation
        {
            VendorId = value.VendorId,
            ProductId = value.ProductId,
            InterfaceNumber = value.InterfaceNumber,
            DeviceRelease = value.DeviceRelease,
            ReportDescriptorHash = value.ReportDescriptorHash,
            ReportLengths = Array.AsReadOnly(value.ReportLengths.ToArray()),
            LocationPath = value.LocationPath
        };
    }

    internal static P.CapabilityReason ToPublic(CapabilityReason value)
    {
        return new P.CapabilityReason((P.CapabilityReasonCode)value.Code, value.Detail, value.Retryable);
    }

    internal static CapabilityReason ToNative(P.CapabilityReason value)
    {
        return new CapabilityReason((CapabilityReasonCode)value.Code, value.Detail, value.Retryable);
    }

    internal static P.CurvePoint ToPublic(CurvePoint value)
    {
        return new P.CurvePoint(value.Input, value.Output);
    }

    internal static CurvePoint ToNative(P.CurvePoint value)
    {
        return new CurvePoint(value.Input, value.Output);
    }

    internal static P.CapabilityChoice ToPublic(CapabilityChoice value)
    {
        return new P.CapabilityChoice(value.Value, Label(value.Display));
    }

    internal static P.DevicePowerPreset ToPublic(DevicePowerPreset value)
    {
        return new P.DevicePowerPreset(value.Id, value.Name, value.SustainedWatts, value.SlowWatts,
                (P.DevicePowerMode)value.WindowsMode)
            { ScenarioOnAc = value.ScenarioOnAc, ScenarioOnDc = value.ScenarioOnDc };
    }

    internal static DevicePowerPreset ToNative(P.DevicePowerPreset value)
    {
        return new DevicePowerPreset(value.Id, value.Name, value.SustainedWatts, value.SlowWatts,
                (DevicePowerMode)value.WindowsMode)
            { ScenarioOnAc = value.ScenarioOnAc, ScenarioOnDc = value.ScenarioOnDc };
    }

    internal static P.OemControlEvent ToPublic(OemControlEvent value)
    {
        return new P.OemControlEvent(value.ControlId, (P.OemPressKind)value.Press, value.Timestamp,
            value.DeduplicationId, (P.OemControlEdge)value.Edge);
    }

    internal static OemControlEvent ToNative(P.OemControlEvent value)
    {
        return new OemControlEvent(value.ControlId, (OemPressKind)value.Press, value.Timestamp, value.DeduplicationId,
            (OemControlEdge)value.Edge);
    }

    private static string Label(CapabilityDisplay display)
    {
        if (display.CustomLabel is { } custom)
        {
            return custom;
        }

        return display.Key switch
        {
            DisplayKey.Tdp => "TDP",
            DisplayKey.SustainedPowerLimit => "Sustained power limit",
            DisplayKey.BoostPowerLimit => "Boost power limit",
            DisplayKey.PerformanceProfile => "Performance profile",
            DisplayKey.FanMode => "Fan mode",
            DisplayKey.FanSpeed => "Fan speed",
            DisplayKey.FanCurve => "Fan curve",
            DisplayKey.FanLeft => "Left fan",
            DisplayKey.FanRight => "Right fan",
            DisplayKey.ChargeLimit => "Charge limit",
            DisplayKey.BypassCharging => "Bypass charging",
            DisplayKey.Lighting => "Lighting",
            DisplayKey.Brightness => "Brightness",
            DisplayKey.LightingEffect => "Effect",
            DisplayKey.LightingEffectSpeed => "Effect speed",
            DisplayKey.CpuTemperature => "CPU temperature",
            DisplayKey.Battery => "Battery",
            DisplayKey.Controller => "Controller",
            DisplayKey.Motion => "Motion",
            DisplayKey.Rumble => "Rumble",
            DisplayKey.VariableRefreshRate => "Variable refresh rate",
            _ => display.Key.ToString()
        };
    }
}
