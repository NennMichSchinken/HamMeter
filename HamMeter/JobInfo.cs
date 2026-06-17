using System;
using System.Collections.Generic;
using System.Numerics;

namespace SimpleMeter;

public static class JobInfo
{
    // Job abbreviation -> ClassJob RowId. Framed job icon = 62100 + RowId.
    public static readonly Dictionary<string, int> RowIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = 19, ["WAR"] = 21, ["DRK"] = 32, ["GNB"] = 37,
        ["WHM"] = 24, ["SCH"] = 28, ["AST"] = 33, ["SGE"] = 40,
        ["MNK"] = 20, ["DRG"] = 22, ["NIN"] = 30, ["SAM"] = 34, ["RPR"] = 39, ["VPR"] = 41,
        ["BRD"] = 23, ["MCH"] = 31, ["DNC"] = 38,
        ["BLM"] = 25, ["SMN"] = 27, ["RDM"] = 35, ["PCT"] = 42, ["BLU"] = 36,
        ["GLA"] = 1, ["MRD"] = 3, ["CNJ"] = 6, ["THM"] = 7, ["ARC"] = 5,
        ["ACN"] = 26, ["PGL"] = 2, ["LNC"] = 4, ["ROG"] = 29,
    };

    // Ordered list for the settings palette (grouped by role).
    public static readonly string[] Order =
    {
        "PLD", "WAR", "DRK", "GNB",
        "WHM", "SCH", "AST", "SGE",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "BRD", "MCH", "DNC",
        "BLM", "SMN", "RDM", "PCT", "BLU",
    };

    // Role groups for a clean, grouped settings layout.
    public static readonly (string Role, string[] Jobs)[] Groups =
    {
        ("Tanks", new[] { "PLD", "WAR", "DRK", "GNB" }),
        ("Healers", new[] { "WHM", "SCH", "AST", "SGE" }),
        ("Melee", new[] { "MNK", "DRG", "NIN", "SAM", "RPR", "VPR" }),
        ("Ranged", new[] { "BRD", "MCH", "DNC" }),        ("Casters", new[] { "BLM", "SMN", "RDM", "PCT", "BLU" }),
    };

    public static int IconId(string job) => RowIds.TryGetValue(job, out int id) ? 62100 + id : 0;

    public static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "Paladin", ["WAR"] = "Warrior", ["DRK"] = "Dark Knight", ["GNB"] = "Gunbreaker",
        ["WHM"] = "White Mage", ["SCH"] = "Scholar", ["AST"] = "Astrologian", ["SGE"] = "Sage",
        ["MNK"] = "Monk", ["DRG"] = "Dragoon", ["NIN"] = "Ninja", ["SAM"] = "Samurai", ["RPR"] = "Reaper", ["VPR"] = "Viper",
        ["BRD"] = "Bard", ["MCH"] = "Machinist", ["DNC"] = "Dancer",
        ["BLM"] = "Black Mage", ["SMN"] = "Summoner", ["RDM"] = "Red Mage", ["PCT"] = "Pictomancer", ["BLU"] = "Blue Mage",
    };

    public static string FullName(string job) => Names.TryGetValue(job, out string? n) ? n : job;

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    public static Dictionary<string, Vector4> DefaultColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Tanks
        ["PLD"] = Rgb(168, 210, 230), ["WAR"] = Rgb(207, 42, 42), ["DRK"] = Rgb(209, 38, 204), ["GNB"] = Rgb(121, 109, 48),
        // Healers
        ["WHM"] = Rgb(255, 253, 245), ["SCH"] = Rgb(134, 87, 255), ["AST"] = Rgb(255, 231, 74), ["SGE"] = Rgb(128, 209, 186),
        // Melee
        ["MNK"] = Rgb(214, 156, 0), ["DRG"] = Rgb(65, 100, 205), ["NIN"] = Rgb(175, 47, 47), ["SAM"] = Rgb(228, 109, 16),
        ["RPR"] = Rgb(150, 90, 150), ["VPR"] = Rgb(61, 140, 97),
        // Physical ranged
        ["BRD"] = Rgb(193, 217, 93), ["MCH"] = Rgb(110, 225, 214), ["DNC"] = Rgb(254, 156, 156),
        // Casters
        ["BLM"] = Rgb(163, 49, 214), ["SMN"] = Rgb(45, 155, 56), ["RDM"] = Rgb(227, 41, 64),
        ["PCT"] = Rgb(242, 121, 166), ["BLU"] = Rgb(0, 95, 255),
    };
}
