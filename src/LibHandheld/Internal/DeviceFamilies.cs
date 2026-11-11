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
using LibHandheld.Internal.Windows;
using NativeIdentity = LibHandheld.Contracts.DeviceIdentitySnapshot;

namespace LibHandheld.Internal;

/// <summary>One protocol implementation with its model profiles and exact identity predicates.</summary>
internal sealed record DeviceFamily(
    string Id,
    IReadOnlyList<HandheldDefinition> Models,
    Func<NativeIdentity, string?> MatchModel,
    Func<HandheldDefinition, NativeEngine> CreateEngine)
{
    /// <summary>The detected model's built-in pad nodes to hide, found without starting or opening anything.</summary>
    internal Func<HandheldDefinition, NativeIdentity, IReadOnlyList<PhysicalDeviceIdentity>> PadsToHide { get; init; } =
        static (_, _) => [];
}

/// <summary>Routes model selection to a family without exposing protocols to consumers.</summary>
internal static class DeviceFamilies
{
    // Add a profile to its existing family for variants sharing a protocol. Only a new protocol
    // family needs another registration; consumer operations never switch on model names.
    private static readonly IReadOnlyList<DeviceFamily> Families = Array.AsReadOnly<DeviceFamily>(
    [
        Native(ClawHardwareFacts.PackageId, ClawModels.Definitions,
            identity => ClawModels.Find(identity)?.DefinitionId, context => new ClawDeviceEngine(context)) with
        {
            PadsToHide = static (_, _) => HidEndpointEnumerator.DiscoverControllerTopology()?.PhysicalDevices ?? []
        },
        Native(AllyModels.PackageId, AllyModels.Definitions,
            identity => AllyModels.Match(identity)?.DefinitionId, context => new AllyDeviceEngine(context)) with
        {
            PadsToHide = static (_, identity) => AllyModels.Match(identity) is { } model
                ? StandardPads(new StandardControllerProfile(model.ControllerProductIds
                    .Select(product => new ControllerUsbId(AllyModels.AsusVendorId, product)).ToArray()))
                : []
        },
        Native(LegionGoModels.PackageId, LegionGoModels.Definitions,
            identity => LegionGoModels.Match(identity)?.DefinitionId, context => new LegionGoDeviceEngine(context)) with
        {
            PadsToHide = static (_, identity) => LegionGoModels.Match(identity) is { } model
                ? WindowsLegionHid.PadsToHide(model)
                : []
        },
        Profiled(GpdModels.FamilyId, GpdModels.All, identity => GpdModels.Match(identity)?.Definition.Id),
        Profiled(AyaneoModels.FamilyId, AyaneoModels.All.Select(model => model.Profile), AyaneoModels.Match),
        Profiled(OneXPlayerModels.FamilyId, OneXPlayerModels.All,
            identity => OneXPlayerModels.Match(identity)?.Definition.Id),
        Profiled(ZotacModels.FamilyId, [ZotacModels.Zone], identity => ZotacModels.Match(identity)?.Definition.Id),
        new DeviceFamily(SteamDeckModels.FamilyId, SteamDeckModels.Definitions,
            SteamDeckModels.Match, SteamDeckModels.CreateEngine),
        new DeviceFamily(AcerModels.FamilyId, AcerModels.Definitions, AcerModels.Match, AcerModels.CreateEngine)
        {
            PadsToHide = static (definition, _) => AcerModels.Profile(definition).Controller is { } controller ? StandardPads(controller) : []
        },
        Profiled(GenericModels.FamilyId, GenericModels.All, identity => GenericModels.Match(identity)?.Definition.Id)
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

    // Each family's own manager apps, in family order.
    internal static IReadOnlyList<OemManagerApp> ManagerApps { get; } = Array.AsReadOnly<OemManagerApp>([
        .. ClawModels.ManagerApps, .. AllyModels.ManagerApps, .. LegionGoModels.ManagerApps,
        .. ZotacModels.ManagerApps, .. AcerModels.ManagerApps
    ]);

    /// <summary>A family of standard-transport profiles; the engine runs the profile with the selected id.</summary>
    private static DeviceFamily Profiled(string id, IEnumerable<ProfiledDeviceModel> models,
        Func<NativeIdentity, string?> match)
    {
        var profiles = models.ToArray();
        return new DeviceFamily(id, profiles.Select(model => model.DeclaredDefinition).ToArray(), match,
            definition => new ProfiledDeviceEngine(profiles.Single(model => model.Definition.Id == definition.Id)))
        {
            PadsToHide = (definition, _) =>
                profiles.Single(model => model.Definition.Id == definition.Id).Controller is { } controller
                    ? StandardPads(controller)
                    : []
        };
    }

    /// <summary>The nodes a standard pad's controller start would hide, from the same discovery.</summary>
    private static IReadOnlyList<PhysicalDeviceIdentity> StandardPads(StandardControllerProfile profile)
    {
        var source = new StandardControllerSource(profile);
        try
        {
            return source.Discover()?.Devices ?? [];
        }
        finally
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Detects this handheld and returns its built-in pad's nodes to hide.</summary>
    /// <param name="identity">The machine identity.</param>
    /// <returns>The pad nodes; empty for an unsupported machine, a pad Steam drives natively, or no pad.</returns>
    internal static IReadOnlyList<PhysicalDeviceIdentity> PadsToHide(NativeIdentity identity)
    {
        foreach (var family in Families)
        {
            if (family.MatchModel(identity) is not { } id)
            {
                continue;
            }

            var definition = family.Models.Single(model => model.Id == id);
            return definition is { HasController: true, NativeSteamController: false }
                ? family.PadsToHide(definition, identity)
                : [];
        }

        return [];
    }

    /// <summary>A family whose own protocol engine supplies every declared role.</summary>
    private static DeviceFamily Native(string id, IReadOnlyList<HandheldDefinition> definitions,
        Func<NativeIdentity, string?> match, Func<ProfiledFeatureContext, ProfiledFeatureService> engine)
    {
        return new DeviceFamily(id, definitions, match,
            definition => new ProfiledDeviceEngine(ProfiledDeviceModel.ForServices(definition, [engine])));
    }

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
