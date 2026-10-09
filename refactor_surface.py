from pathlib import Path
import re
root = Path(__file__).parent / 'src/LibHandheld'

def remove_block(text, marker):
    start=text.index(marker)
    brace=text.index('{',start)
    depth=1; end=brace+1
    while depth:
        depth += (text[end]=='{')-(text[end]=='}'); end+=1
    return text[:start]+text[end:]

def remove_assignments(text, names):
    # Remove one object-initializer assignment, respecting nested constructors/arrays.
    pattern=re.compile(r'\b('+'|'.join(names)+r')\s*=\s*(?!=)')
    matches=list(pattern.finditer(text))
    for match in reversed(matches):
        start=match.start(); end=match.end(); stack=[]; quote=None
        while end<len(text):
            c=text[end]
            if quote:
                if c==quote and (end==0 or text[end-1]!='\\'): quote=None
            elif c in '"\'': quote=c
            elif c in '([{': stack.append(c)
            elif c in ')]}':
                if not stack: break
                stack.pop()
            elif c==',' and not stack:
                end+=1; break
            elif c==';' and not stack: break
            end+=1
        # Only initializer assignments, never local variables/properties or statement assignments.
        before=text[:start].rstrip()
        if before and before[-1] in ',{':
            text=text[:start]+text[end:]
    return text

for src in root.rglob('*.cs'):
    data=src.read_text(encoding='utf-8-sig')
    if src.name not in ('CapabilityDescriptor.cs','CapabilityCommand.cs','CapabilityState.cs','DeviceOperations.cs'):
        data=remove_assignments(data, ['ExpectedCycleGeneration','ExpectedDescriptorGeneration','CycleGeneration','DescriptorGeneration','ReadbackValue','Rollback','SectionId','CategoryId','SortOrder','Prominence','LayoutPair','Sections','Generation'])
    data=data.replace('CommandOutcome.Applied or CommandOutcome.Applied','CommandOutcome.Applied')
    data=data.replace('CommandOutcome.Applied, CommandOutcome.Applied','CommandOutcome.Applied')
    src.write_text(data, encoding='utf-8',newline='\r\n')

for name,fields in [('CapabilityDescriptor.cs',['SectionId','CategoryId','SortOrder','Prominence','LayoutPair','Sections','Generation','CycleGeneration']),('CapabilityCommand.cs',['ExpectedCycleGeneration','ExpectedDescriptorGeneration']),('CapabilityState.cs',['DescriptorGeneration','CycleGeneration']),('DeviceOperations.cs',['CycleGeneration','DescriptorGeneration'])]:
    src=root/'Contracts'/name; data=src.read_text(encoding='utf-8')
    for field in fields:
        data=re.sub(r'    public (?:required )?[\w?<>]+ '+field+r' \{ get; init; \}(?: = [^;]+;)?\n', '',data)
    src.write_text(data,encoding='utf-8',newline='\r\n')

src=root/'Internal/Plugin/PluginContracts.cs'; data=src.read_text(encoding='utf-8')
data=re.sub(r'    public required long CycleGeneration \{ get; init; \}\n','    public required DeviceIdentitySnapshot Identity { get; init; }\n',data)
data=data.replace('PluginResumeContext(long CycleGeneration, Deadline Deadline)','PluginResumeContext(Deadline Deadline)')
data=data.replace('PluginControllerReleaseContext(HandoffScope Scope, Deadline Deadline)','PluginControllerReleaseContext(Deadline Deadline)')
data=data.replace('    PluginStopReason Reason,\n    Deadline Deadline','    Deadline Deadline')
data=remove_block(data,'    ValueTask ApplySettingsAsync(')
data=data.replace('using LibHandheld.Internal.Settings;','')
src.write_text(data,encoding='utf-8',newline='\r\n')
src=root/'Internal/Plugin/PluginHostAdapter.cs'; data=src.read_text(encoding='utf-8')
start=data.index('    /// <summary>Declares the settings'); end=data.index('    /// <summary>Writes one diagnostic',start)
data=data[:start]+data[end:]
data=data.replace('    long CycleGeneration { get; }','')
data=data.replace('using LibHandheld.Internal.Settings;','')
src.write_text(data,encoding='utf-8',newline='\r\n')
for src in (root/'Internal/Settings').glob('*.cs'): src.unlink()
(root/'Internal/Devices/AsusAuraDeviceEngine.cs').unlink()

# Remove the broad vendor/CPU ranges entirely.
src=root/'Families/Generic/GenericModels.cs'; data=src.read_text(encoding='utf-8')
start=data.index('    private static readonly HandheldDefinition[] Fallbacks')
end=data.index('    internal static IReadOnlyList<ProfiledDeviceModel> All',start)
data=data[:start]+data[end:]
start=data.index('    internal static ProfiledDeviceModel? MatchFallback')
end=data.index('    internal static IDeviceEngine CreateEngine',start)
data=data[:start]+data[end:]
data=data.replace('.Concat(Fallbacks).ToArray()', '.ToArray()')
data=data.replace('return MatchFallback(identity);','return null;')
data=data.replace('return model.Manufacturers.Count == 0 ? Bind(model, identity, ProfileIdentitySource.System) : model;', 'return model;')
data=data.replace('return new GenericEngine(definition);','return new ProfiledDeviceEngine(All.Single(model => model.Definition == definition));')
start=data.index('    private static ProfiledDeviceModel Bind'); data=data[:start]+'}\n'
src.write_text(data,encoding='utf-8',newline='\r\n')

# Adapt the direct semantic API to the single DTO and owner gate.
src=root/'HandheldDevice.Controls.cs'; data=src.read_text(encoding='utf-8')
data='using LibHandheld.Contracts;\nusing LibHandheld.Internal.Capabilities;\n'+data[data.index('namespace LibHandheld;'):]
data=data.replace('    private readonly SemaphoreSlim _commands = new(1, 1);\n','').replace('        using var operation = Enter();\n','')
data=data.replace('_commands.WaitAsync', '_owner.WaitAsync').replace('_commands.Release()', '_owner.Release()')
data=data.replace('snapshot.DescriptorGeneration == 0', 'snapshot.Controls.Count == 0')
data=remove_assignments(data,['ExpectedCycleGeneration','ExpectedDescriptorGeneration','CycleGeneration','DescriptorGeneration'])
start=data.index('        var native = new Internal.Capabilities.CapabilityDescriptor'); end=data.index('\n    private DeviceControlSnapshot Snapshot()',start)
data=data[:start]+'        return CapabilityValueValidation.ValueMatches(value, descriptor, out _);\n    }\n'+data[end:]
data=data.replace('Outcome = result.Outcome, Reason = result.Reason, Results = results','Outcome = results.Count > 1 ? CommandOutcome.Indeterminate : result.Outcome, Reason = result.Reason, Results = results')
data=data.replace('Outcome = results.All(result => result.Result.Outcome == CommandOutcome.Applied)\n                    ? CommandOutcome.Applied\n                    : CommandOutcome.Applied,','Outcome = CommandOutcome.Applied,')
src.write_text(data,encoding='utf-8',newline='\r\n')
(root/'HandheldDevice.Identity.cs').unlink()

# The engine receives the already matched snapshot; no second detection/read is needed.
src=root/'Internal/Devices/ProfiledDeviceEngine.cs'; data=src.read_text(encoding='utf-8')
data=data.replace('            if (_identity is null || context.DeviceDefinitionId != _profile.Definition.Id)', '            _identity = context.Identity;\n            if (context.DeviceDefinitionId != _profile.Definition.Id)')
data=data.replace('            _cycle = context.CycleGeneration;','')
data=data.replace('            _sink = context.Host;', '            _sink = context.Host;\n            _services.Clear();')
src.write_text(data,encoding='utf-8',newline='\r\n')
for family in ['MsiClaw','RogAlly','LegionGo']:
    name={'MsiClaw':'Claw','RogAlly':'Ally','LegionGo':'LegionGo'}[family]
    src=root/'Families'/family/(name+'DeviceEngine.cs'); data=src.read_text(encoding='utf-8')
    marker='        if (context.CycleGeneration != context.Host.CycleGeneration)'
    if marker in data: data=remove_block(data,marker)
    data=data.replace('        _cycleGeneration = context.CycleGeneration;','').replace('        _cycle = context.CycleGeneration;','')
    data=re.sub(r', cycle=\{context.CycleGeneration\}', '', data)
    if family=='MsiClaw':
        data=data.replace('var identity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);', 'var identity = new ClawIdentityState { Snapshot = context.Identity, Model = definedModel, ExactMachineMatch = true, WmiAvailable = true, OnAcPower = _services.Identity.ReadOnAcPower() };')
        data=data.replace('_cycleIdentity = await _services.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);','_cycleIdentity = _cycleIdentity! with { OnAcPower = _services.Identity.ReadOnAcPower() };')
    elif family=='RogAlly':
        data=data.replace('var identity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);', 'var identity = new AllyIdentityState { Snapshot = context.Identity, Model = definition, OnAcPower = LibHandheld.Internal.Windows.WindowsMachineIdentity.OnAcPower() };')
        data=data.replace('_cycleIdentity = await _hardware.Identity.ReadAsync(cancellationToken).ConfigureAwait(false);','_cycleIdentity = _cycleIdentity! with { OnAcPower = LibHandheld.Internal.Windows.WindowsMachineIdentity.OnAcPower() };')
    else: data=data.replace('_identity = _hardware.Identity();','_identity = context.Identity;')
    src.write_text(data,encoding='utf-8',newline='\r\n')

# Plain context names and one public contract namespace.
renames={'PluginStartContext':'EngineStartContext','PluginStartResult':'EngineStartResult','PluginOperationalState':'EngineOperationalState','PluginStopContext':'EngineStopContext','PluginStopResult':'EngineStopResult','PluginStopStatus':'EngineStopStatus','PluginStopReason':'EngineStopReason','PluginQuiesceContext':'EngineQuiesceContext','PluginResumeContext':'EngineResumeContext','PluginControllerReleaseContext':'EngineControllerReleaseContext','PluginControllerManagementContext':'EngineControllerManagementContext','PluginDiagnostics':'EngineDiagnostics','PluginDetectionContext':'EngineDetectionContext','PluginDetectionResult':'EngineDetectionResult'}
for src in root.rglob('*.cs'):
    data=src.read_text(encoding='utf-8')
    for old,new in renames.items(): data=data.replace(old,new)
    data=data.replace('LibHandheld.Internal.Plugin','LibHandheld.Internal.Runtime')
    data=re.sub(r'new EngineStopContext\(EngineStopReason\.\w+,\s*','new EngineStopContext(',data)
    data=re.sub(r'new EngineControllerReleaseContext\(HandoffScope\.\w+,\s*','new EngineControllerReleaseContext(',data)
    data=re.sub(r'new EngineResumeContext\([^,\n]+,\s*','new EngineResumeContext(',data)
    src.write_text(data,encoding='utf-8',newline='\r\n')
old=root/'Internal/Plugin'; new=root/'Internal/Runtime'; old.rename(new)
for src in new.glob('Plugin*.cs'): src.rename(src.with_name(src.name.replace('Plugin','Device')))
