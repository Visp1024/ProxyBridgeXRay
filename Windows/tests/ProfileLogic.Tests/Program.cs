using System;
using System.Collections.Generic;
using System.Linq;
using ProxyBridge.GUI.Services;

// Plain-assert test runner for the pure profile logic in ProfileManager.
// Run with: dotnet run --project Windows/tests/ProfileLogic.Tests

int failed = 0;

void Check(bool condition, string name)
{
    if (condition)
    {
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        Console.WriteLine($"  FAIL  {name}");
        failed++;
    }
}

ProxyProfile MakeProfile(params (uint id, string type)[] configs) => new()
{
    ProxyConfigs = configs.Select(c => new ProxyConfigEntry
    {
        Id = c.id, Type = c.type, Host = "127.0.0.1", Port = "1080"
    }).ToList()
};

Console.WriteLine("NormalizeIds");
{
    // Session native ids (7, 9) must become stable sequential ids (1, 2),
    // and rules must be remapped to follow the config they pointed at.
    var profile = MakeProfile((7, "HTTP"), (9, "SOCKS5"));
    profile.ProxyRules.Add(new ProxyRuleConfig { ProcessName = "game.exe", Action = "PROXY", ProxyConfigId = 9 });
    ProfileManager.NormalizeIds(profile);

    Check(profile.ProxyConfigs[0].Id == 1, "first config renumbered to 1");
    Check(profile.ProxyConfigs[1].Id == 2, "second config renumbered to 2");
    Check(profile.ProxyRules[0].ProxyConfigId == 2, "rule follows its config to the new id");
}
{
    // A dangling reference cannot survive a save: it becomes 0 (visible), never
    // an id that accidentally collides with another config after renumbering.
    var profile = MakeProfile((1, "HTTP"), (3, "SOCKS5"));
    profile.ProxyRules.Add(new ProxyRuleConfig { ProcessName = "game.exe", Action = "PROXY", ProxyConfigId = 2 });
    ProfileManager.NormalizeIds(profile);

    Check(profile.ProxyRules[0].ProxyConfigId == 0, "dangling rule reference saved as 0");
}

Console.WriteLine("ResolveRuleProxyConfigId");
var idMap = new Dictionary<uint, uint> { [1] = 10, [3] = 11 };
var configs = new (uint Id, string Type)[] { (10, "HTTP"), (11, "SOCKS5") };
{
    uint resolved = ProfileManager.ResolveRuleProxyConfigId(
        savedId: 3, fullConeUdp: false, action: "PROXY", idMap, configs, out var warning);
    Check(resolved == 11, "valid reference maps straight through");
    Check(warning == null, "no warning for a valid reference");
}
{
    // The MK1 case: rule saved with an id that no longer exists, FullConeUdp on.
    // Full cone only works over SOCKS5, so a single SOCKS5 config is an
    // unambiguous repair target.
    uint resolved = ProfileManager.ResolveRuleProxyConfigId(
        savedId: 2, fullConeUdp: true, action: "PROXY", idMap, configs, out var warning);
    Check(resolved == 11, "dangling full-cone rule re-linked to the only SOCKS5 config");
    Check(warning != null, "repair is reported, not silent");
}
{
    // Dangling, not full cone, two candidate configs: ambiguous — must fail
    // loudly with 0 rather than guess.
    uint resolved = ProfileManager.ResolveRuleProxyConfigId(
        savedId: 2, fullConeUdp: false, action: "PROXY", idMap, configs, out var warning);
    Check(resolved == 0, "ambiguous dangling reference resolves to 0");
    Check(warning != null, "ambiguous dangling reference is reported");
}
{
    // Dangling with exactly one config overall: unambiguous, take it.
    var oneMap = new Dictionary<uint, uint> { [1] = 10 };
    var oneConfig = new (uint Id, string Type)[] { (10, "HTTP") };
    uint resolved = ProfileManager.ResolveRuleProxyConfigId(
        savedId: 5, fullConeUdp: false, action: "PROXY", oneMap, oneConfig, out var warning);
    Check(resolved == 10, "dangling reference re-linked to the only config");
    Check(warning != null, "repair is reported, not silent");
}
{
    // Non-PROXY rules never reference a config.
    uint resolved = ProfileManager.ResolveRuleProxyConfigId(
        savedId: 2, fullConeUdp: false, action: "BLOCK", idMap, configs, out var warning);
    Check(resolved == 0, "non-PROXY rule resolves to 0");
    Check(warning == null, "non-PROXY rule produces no warning");
}
{
    // PROXY rule saved with 0 (legacy profiles): treat as dangling and repair.
    uint resolved = ProfileManager.ResolveRuleProxyConfigId(
        savedId: 0, fullConeUdp: true, action: "PROXY", idMap, configs, out var warning);
    Check(resolved == 11, "legacy PROXY rule with id 0 re-linked via full-cone repair");
}

Console.WriteLine(failed == 0 ? "\nAll tests passed." : $"\n{failed} test(s) FAILED.");
return failed == 0 ? 0 : 1;
