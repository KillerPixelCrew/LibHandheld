from pathlib import Path
import re
root=Path(__file__).parent/'src/LibHandheld'

def remove_if(data, needle, replacement=''):
    while needle in data:
        start=data.index(needle); brace=data.index('{',start); end=brace+1; depth=1
        while depth:
            depth+=(data[end]=='{')-(data[end]=='}'); end+=1
        data=data[:start]+replacement+data[end:]
    return data

for src in root.rglob('*.cs'):
    data=src.read_text(encoding='utf-8')
    data=re.sub(r',\s*RollbackResult.NotRequired','',data)
    data=data.replace(',\n        RollbackResult rollback', '')
    # Generation admission is gone; current descriptor/availability checks stay.
    data=remove_if(data,'            if (command.ExpectedCycleGeneration')
    data=remove_if(data,'        if (command.ExpectedCycleGeneration')
    data=remove_if(data,'        if (_descriptorSet is null || command.ExpectedDescriptorGeneration', '        if (_descriptorSet is null) { return new CapabilityReason(CapabilityReasonCode.HostUnavailable); }')
    data=remove_if(data,'        if (_descriptorSet is null\n            || command.ExpectedDescriptorGeneration', '        if (_descriptorSet is null) { return new CapabilityReason(CapabilityReasonCode.HostUnavailable); }')
    data=data.replace('descriptor.SectionId == DeviceSections.RgbId', 'descriptor.Role is CapabilityRole.LightingBrightness or CapabilityRole.LightingEffect or CapabilityRole.LightingEffectSpeed or CapabilityRole.LightingZoneColor or CapabilityRole.LightingPower')
    data=data.replace('{context.Sink.CycleGeneration}:', '')
    data=re.sub(r'^    private long _(?:cycle|cycleGeneration|generation|nativeDescriptorGeneration);\n', '', data, flags=re.M)
    data=re.sub(r'^\s*_cycle(?:Generation)? = context.CycleGeneration;\n', '', data,flags=re.M)
    data=data.replace('(_cycle, deadline,', '(deadline,').replace('(_cycleGeneration, deadline,', '(deadline,')
    data=data.replace('$"{_cycle}:', '$"').replace('$"{_cycleGeneration}:', '$"')
    data=data.replace('long CycleGeneration,\n    Deadline Deadline', 'Deadline Deadline')
    data=data.replace('++_generation;', '').replace('_generation++;', '')
    data=re.sub(r'^\s*var generation = \+\+_generation;\n', '',data,flags=re.M)
    src.write_text(data,encoding='utf-8',newline='\r\n')

# Remove engine detection contracts and methods. Constructors/start receive the selected profile.
for src in root.rglob('*DeviceEngine*.cs'):
    data=src.read_text(encoding='utf-8')
    needle='    public ValueTask<EngineDetectionResult> DetectAsync('
    if needle in data:
        start=data.index(needle); brace=data.index('{', start); end=brace+1; depth=1
        while depth:
            depth+=(data[end]=='{')-(data[end]=='}'); end+=1
        data=data[:start]+data[end:]
    src.write_text(data,encoding='utf-8',newline='\r\n')
src=root/'Internal/Runtime/DeviceContracts.cs'; data=src.read_text(encoding='utf-8')
start=data.index('    ValueTask<EngineDetectionResult> DetectAsync('); end=data.index(';',start)+1
data=data[:start]+data[end:]
start=data.index('internal sealed record EngineDetectionContext'); end=data.index('/// <summary>Inputs to one process-long start',start)
data=data[:start]+data[end:]
start=data.index('internal enum EngineStopReason'); end=data.index('/// <summary>Terminal cleanup request',start)
data=data[:start]+data[end:]
src.write_text(data,encoding='utf-8',newline='\r\n')
src=root/'Contracts/DeviceLifecycle.cs';data=src.read_text(encoding='utf-8')
start=data.index('/// <summary>Reason for ending a device cycle'); end=data.index('/// <summary>Diagnostic importance', start)
data=data[:start]+data[end:]
data=data.replace('    public bool HasController', '    /// <summary>Whether this exact model has a recorded hardware validation pass.</summary>\n    public bool HardwareVerified { get; init; }\n\n    /// <summary>Semantic roles actually supplied by this model and its optional accessory services.</summary>\n    public IReadOnlyList<CapabilityRole> DeclaredRoles { get; init; } = [];\n\n    public bool HasController')
src.write_text(data,encoding='utf-8',newline='\r\n')
(root/'Contracts/ControllerHandoff.cs').unlink()

src=root/'Internal/DeviceFamilies.cs';data=src.read_text(encoding='utf-8')
data=data.replace('model.DisplayName, "msi-claw")).ToArray()', 'model.DisplayName, "msi-claw") { HardwareVerified = model.HardwareVerified }).ToArray()')
start=data.index('        var engine = family.CreateEngine(model);')
data=data[:start]+'''        return family.CreateEngine(model);
    }
}
'''
data=data.replace('Func<HandheldDefinition, IDeviceEngine> CreateEngine', 'Func<HandheldDefinition, IDeviceEngine> CreateEngine')
# Generic registration can contain several plain exact family ids.
data=data.replace('candidate.Id == model.FamilyId','candidate.Models.Contains(model)')
src.write_text(data,encoding='utf-8',newline='\r\n')

src=root/'Families/Zotac/ZotacModels.cs';data=src.read_text(encoding='utf-8')
start=data.index('    internal static IDeviceEngine CreateEngine')
data=data[:start]+'''    internal static IDeviceEngine CreateEngine(HandheldDefinition definition)
    {
        return new ProfiledDeviceEngine(Zone);
    }
}
'''
src.write_text(data,encoding='utf-8',newline='\r\n')
(root/'Families/Generic/ProfiledEngineSelector.cs').unlink()

src=root/'Families/Generic/GenericModels.cs';data=src.read_text(encoding='utf-8')
data=data.replace('new HandheldDefinition(id, FamilyId, name, "generic")','new HandheldDefinition(id, id.Split(\'-\')[0] == "asus" ? "asus-flow" : id.Split(\'-\')[0], name, "generic")')
src.write_text(data,encoding='utf-8',newline='\r\n')
src=root/'Families/SteamDeck/SteamDeckModels.cs';data=src.read_text(encoding='utf-8').replace('"steamdeck"','"valve"').replace('"steam-deck"','"valve"')
src.write_text(data,encoding='utf-8',newline='\r\n')

# The snapshot identity never changes during one device lifetime; only AC status is live.
src=root/'Families/RogAlly/AllyDeviceEngine.Commands.cs';data=src.read_text(encoding='utf-8')
start=data.index('        AllyIdentityState identity;');end=data.index('        var refusal =',start)
data=data[:start]+'''        var identity = _cycleIdentity! with { OnAcPower = LibHandheld.Internal.Windows.WindowsMachineIdentity.OnAcPower() };

'''+data[end:]
src.write_text(data,encoding='utf-8',newline='\r\n')
src=root/'Internal/Windows/WindowsMachineIdentity.cs';data=src.read_text(encoding='utf-8')
data=data.replace('    internal static DeviceIdentitySnapshot Read()', '''    internal static bool OnAcPower()
    {
        return !GetSystemPowerStatus(out var status) || status.ACLineStatus != 0;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    internal static DeviceIdentitySnapshot Read()''')
src.write_text(data,encoding='utf-8',newline='\r\n')
