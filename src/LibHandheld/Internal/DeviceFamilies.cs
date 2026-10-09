using LibHandheld.Contracts;
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
using LibHandheld.Internal.Plugin;
using NativeIdentity = LibHandheld.Internal.Identity.DeviceIdentitySnapshot;

namespace LibHandheld.Internal;

/// <summary>One protocol implementation with its model profiles and exact identity predicates.</summary>
internal sealed record DeviceFamily(
    string Id,
    IReadOnlyList<HandheldDefinition> Models,
    Func<NativeIdentity, string?> MatchModel,
    Func<HandheldDefinition, IDeviceEngine> CreateEngine);

/// <summary>Routes model selection to a family without exposing protocols to consumers.</summary>
internal static class DeviceFamilies
{
    // Add a profile to its existing family for variants sharing a protocol. Only a new protocol
    // family needs another registration; consumer operations never switch on model names.
    private static readonly IReadOnlyList<DeviceFamily> Families = Array.AsReadOnly<DeviceFamily>(
    [
        new DeviceFamily(ClawHardwareFacts.PackageId,
            Array.AsReadOnly(ClawModels.All.Select(model => new HandheldDefinition(model.DefinitionId,
                ClawHardwareFacts.PackageId, model.DisplayName, "msi-claw")).ToArray()),
            identity => ClawModels.Find(identity)?.DefinitionId,
            _ => new ClawDeviceEngine()),
        new DeviceFamily(AllyModels.PackageId,
            Array.AsReadOnly(AllyModels.All.Select(model => new HandheldDefinition(model.DefinitionId,
                AllyModels.PackageId, model.DisplayName, model.GlyphProfileId)).ToArray()),
            identity => AllyModels.Match(identity)?.DefinitionId,
            _ => new AllyDeviceEngine()),
        new DeviceFamily(LegionGoModels.PackageId,
            Array.AsReadOnly(LegionGoModels.All.Select(model => new HandheldDefinition(model.DefinitionId,
                LegionGoModels.PackageId, model.DisplayName, model.GlyphProfileId)
            {
                HardwareAccess = model.EcFan ? DeviceHardwareAccess.PawnIo : DeviceHardwareAccess.None
            }).ToArray()),
            identity => LegionGoModels.Match(identity)?.DefinitionId,
            _ => new LegionGoDeviceEngine()),
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
        new DeviceFamily(GenericModels.FamilyId, GenericModels.GetDefinitions(),
            identity => GenericModels.Match(identity)?.Definition.Id, GenericModels.CreateEngine)
    ]);

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

    internal static IDeviceEngine Create(HandheldDefinition model)
    {
        var family = Families.SingleOrDefault(candidate => candidate.Id == model.FamilyId)
                     ?? throw new ArgumentException("Unknown handheld protocol family.", nameof(model));
        if (!family.Models.Contains(model))
        {
            throw new ArgumentException("The model is not declared by its protocol family.", nameof(model));
        }

        var engine = family.CreateEngine(model);
        // ASUS firmware profiles already own their USB lighting module. Other machines may also
        // attach an XG Mobile or Aura keyboard, without changing their CPU or physical input backend.
        return model.FamilyId == GenericModels.FamilyId && model.Id.StartsWith("asus-", StringComparison.Ordinal)
            ? engine
            : new AsusAuraDeviceEngine(engine);
    }
}
