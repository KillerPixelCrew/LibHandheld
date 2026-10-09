from pathlib import Path
import re

root = Path(__file__).parent / 'src/LibHandheld'
copies = {
    'Capabilities': ['CapabilityDescriptor', 'CapabilityCommand', 'CapabilityState', 'CapabilityRole', 'CapabilityReason', 'CapabilityProfileScope', 'DevicePowerPreset', 'CapabilityDisplay'],
    'Input': ['CanonicalControllerState', 'HapticOutput', 'PhysicalDeviceIdentity', 'OemControls'],
    'Identity': ['DeviceIdentitySnapshot'],
    'Lifecycle': ['Deadline', 'ControllerHandoff'],
}
for folder, names in copies.items():
    for name in names:
        src = root / 'Internal' / folder / (name + '.cs')
        data = src.read_text(encoding='utf-8-sig')
        data = data.replace('namespace LibHandheld.Internal.' + folder + ';', 'namespace LibHandheld.Contracts;')
        data = re.sub(r'^internal (?=(?:sealed |readonly |abstract )?(?:record|class|enum|struct|interface))', 'public ', data, flags=re.M)
        (root / 'Contracts' / (name + '.cs')).write_text(data, encoding='utf-8', newline='\r\n')
        src.unlink()

for src in list(root.rglob('*.cs')) + list((root.parent.parent / 'tests').rglob('*.cs')):
    data = src.read_text(encoding='utf-8-sig')
    for folder, names in copies.items():
        for name in names:
            data = data.replace('LibHandheld.Internal.' + folder + '.' + name, 'LibHandheld.Contracts.' + name)
    # Helpers still use their internal namespaces; every implementation consumes the one contract.
    if 'namespace LibHandheld.Contracts;' not in data and 'using LibHandheld.Contracts;' not in data:
        data = 'using LibHandheld.Contracts;\n' + data
    data = data.replace('CustomLabel', 'CustomText')
    data = data.replace('CommandOutcome.AppliedVerified', 'CommandOutcome.Applied').replace('CommandOutcome.AppliedUnverified', 'CommandOutcome.Applied')
    data = data.replace('wsgm.device.msi.claw', 'msi-claw').replace('wsgm.device.asus.rog-ally', 'rog-ally').replace('libhandheld.lenovo.legion-go', 'legion-go')
    data = data.replace('SourceOwnership.Plugin', 'SourceOwnership.Managed')
    src.write_text(data, encoding='utf-8', newline='\r\n')

# Outcome evidence belongs to states. An accepted transport write is Applied.
src = root / 'Contracts/CapabilityCommand.cs'
data = src.read_text()
start = data.index('    /// <summary>Written successfully;')
end = data.index('    /// <summary>Refused before', start)
data = data[:start] + '    /// <summary>The native transport accepted the requested write.</summary>\n    Applied,\n\n' + data[end:]
start = data.index('    /// <summary>\n    ///     An independently read value')
end = data.index('    /// <summary>When the command finished', start)
data = data[:start] + data[end:]
data = data[:data.index('/// <summary>What happened to the previous value')]
src.write_text(data, encoding='utf-8', newline='\r\n')

src = root / 'Internal/Capabilities/CapabilityIds.cs'
data = src.read_text().replace('const string Plugin = "plugin"', 'const string Managed = "managed"')
src.write_text(data, encoding='utf-8', newline='\r\n')

(root / 'HandheldConversions.cs').unlink()
