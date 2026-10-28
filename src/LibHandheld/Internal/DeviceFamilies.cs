using LibHandheld.Contracts;
using LibHandheld.Families.Acer;
using LibHandheld.Families.Ayaneo;
using LibHandheld.Families.Generic;
using LibHandheld.Families.Gpd;
using LibHandheld.Families.LegionGo;
using LibHandheld.Families.MsiClaw;
using LibHandheld.Families.OneXPlayer;
using LibHandheld.Families.RogAlly;
using LibHandheld.Families.SteamDeck;
using LibHandheld.Families.Zotac;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using NativeIdentity = LibHandheld.Contracts.DeviceIdentitySnapshot;

namespace LibHandheld.Internal;

/// <summary>One protocol implementation with its model profiles and exact identity predicates.</summary>
internal sealed record DeviceFamily(
    string Id,
    IReadOnlyList<HandheldDefinition> Models,
    Func<NativeIdentity, string?> MatchModel,
    Func<HandheldDefinition, NativeEngine> CreateEngine);

/// <summary>Routes model selection to a family without exposing protocols to consumers.</summary>
internal static class DeviceFamilies
{
    // Add a profile to its existing family for variants sharing a protocol. Only a new protocol
    // family needs another registration; consumer operations never switch on model names.
    private static readonly IReadOnlyList<DeviceFamily> Families = Array.AsReadOnly<DeviceFamily>(
    [
        new DeviceFamily(ClawHardwareFacts.PackageId,
            Array.AsReadOnly(ClawModels.All.Select(model => new HandheldDefinition(model.DefinitionId,
                ClawHardwareFacts.PackageId, model.DisplayName, "msi-claw")
            {
                HardwareVerified = model.HardwareVerified,
                DeclaredRoles = Array.AsReadOnly([
                    CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit, CapabilityRole.ChargeLimit,
                    CapabilityRole.ChargeProtectionMode,
                    CapabilityRole.ScenarioMode, CapabilityRole.FanMode, CapabilityRole.FanDuty,
                    CapabilityRole.LightingBrightness, CapabilityRole.ControllerSource, CapabilityRole.MotionSource,
                    CapabilityRole.HapticSink, CapabilityRole.Telemetry, CapabilityRole.FanMeasuredRpm,
                    CapabilityRole.FanCurve, CapabilityRole.LightingZoneColor
                ])
            }).ToArray()),
            identity => ClawModels.Find(identity)?.DefinitionId,
            definition => new ProfiledDeviceEngine(ProfiledDeviceModel.ForServices(definition,
                [context => new ClawDeviceEngine(context)]))),
        new DeviceFamily(AllyModels.PackageId,
            Array.AsReadOnly(AllyModels.All.Select(model => new HandheldDefinition(model.DefinitionId,
                AllyModels.PackageId, model.DisplayName, model.GlyphProfileId)
            {
                DeclaredRoles = Array.AsReadOnly([
                    CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit, CapabilityRole.ScenarioMode,
                    CapabilityRole.ChargeLimit, CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.FanCurve,
                    CapabilityRole.Telemetry, CapabilityRole.LightingBrightness, CapabilityRole.LightingEffect,
                    CapabilityRole.LightingEffectSpeed, CapabilityRole.ControllerSource, CapabilityRole.MotionSource,
                    CapabilityRole.HapticSink, CapabilityRole.LightingZoneColor
                ])
            }).ToArray()),
            identity => AllyModels.Match(identity)?.DefinitionId,
            definition => new ProfiledDeviceEngine(ProfiledDeviceModel.ForServices(definition,
                [context => new AllyDeviceEngine(context)]))),
        new DeviceFamily(LegionGoModels.PackageId,
            Array.AsReadOnly(LegionGoModels.All.Select(model => new HandheldDefinition(model.DefinitionId,
                LegionGoModels.PackageId, model.DisplayName, model.GlyphProfileId)
            {
                DeclaredRoles =
                [
                    CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit, CapabilityRole.ScenarioMode,
                    CapabilityRole.FanMode, CapabilityRole.FanDuty, CapabilityRole.ChargeProtectionMode,
                    CapabilityRole.FanMeasuredRpm, CapabilityRole.Telemetry, CapabilityRole.LightingPower,
                    CapabilityRole.LightingBrightness, CapabilityRole.LightingEffectSpeed,
                    CapabilityRole.LightingEffect, CapabilityRole.ControllerSource, CapabilityRole.MotionSource,
                    CapabilityRole.HapticSink, CapabilityRole.GenericToggle, CapabilityRole.GenericAction,
                    CapabilityRole.LightingZoneColor, .. !model.EcFan ? new[] { CapabilityRole.FanCurve } : [],
                    .. !model.Slim ? new[] { CapabilityRole.GenericChoice } : []
                ],
                HardwareAccess = model.EcFan ? DeviceHardwareAccess.PawnIo : DeviceHardwareAccess.None
            }).ToArray()),
            identity => LegionGoModels.Match(identity)?.DefinitionId,
            definition => new ProfiledDeviceEngine(ProfiledDeviceModel.ForServices(definition,
                [context => new LegionGoDeviceEngine(context)]))),
        new DeviceFamily(GpdModels.FamilyId, GpdModels.GetDefinitions(),
            identity => GpdModels.Match(identity)?.Definition.Id, GpdModels.CreateEngine),
        new DeviceFamily(AyaneoModels.FamilyId, AyaneoModels.Definitions, AyaneoModels.Match,
            AyaneoModels.CreateEngine),
        new DeviceFamily(OneXPlayerModels.FamilyId, OneXPlayerModels.Definitions,
            identity => OneXPlayerModels.Match(identity)?.Definition.Id, OneXPlayerModels.CreateEngine),
        new DeviceFamily(ZotacModels.FamilyId, ZotacModels.GetDefinitions(),
            identity => ZotacModels.Match(identity)?.Definition.Id, ZotacModels.CreateEngine),
        new DeviceFamily(SteamDeckModels.FamilyId, SteamDeckModels.Definitions,
            SteamDeckModels.Match, SteamDeckModels.CreateEngine),
        new DeviceFamily(AcerModels.FamilyId, AcerModels.Definitions, AcerModels.Match, AcerModels.CreateEngine),
        new DeviceFamily(GenericModels.FamilyId, GenericModels.GetDefinitions(),
            identity => GenericModels.Match(identity)?.Definition.Id, GenericModels.CreateEngine)
    ]).Select(family => family with
    {
        Models = Array.AsReadOnly(family.Models.Select(model => model with
        {
            // Processor settings join the exact device owner and use the signed RyzenSMU or IntelMSR module.
            HardwareAccess = model.HardwareAccess | DeviceHardwareAccess.PawnIo,
            // An approved USB accessory may add these controls to any exact supported model.
            DeclaredRoles = model.DeclaredRoles.Concat(GenericAsusService.AccessoryRoles)
                .Append(CapabilityRole.GenericRange).Append(CapabilityRole.GenericAction).Distinct().ToArray()
        }).ToArray())
    }).ToArray();

    internal static IReadOnlyList<HandheldDefinition> Models { get; } = Array.AsReadOnly(
        Families.SelectMany(family => family.Models).ToArray());

    internal static HandheldDefinition? Detect(NativeIdentity identity)
    {
        foreach (var family in Families)
        {
            if (family.MatchModel(identity) is { } id)
            {
                return family.Models.Single(model => model.Id == id);
            }
        }

        return null;
    }

    internal static NativeEngine Create(HandheldDefinition model)
    {
        var family = Families.SingleOrDefault(candidate => candidate.Models.Contains(model))
                     ?? throw new ArgumentException("Unknown handheld protocol family.", nameof(model));
        return family.CreateEngine(model);
    }
}
