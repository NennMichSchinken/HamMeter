using System.Collections.Generic;
using HamMeter.Data;

namespace HamMeter;

public static class TestData
{
    public static CombatEvent Build()
    {
        Combatant Make(string name, string job, float dmg, float dps, float taken, float healed, float hps, int deaths)
        {
            return new Combatant
            {
                Name = name,
                Job = job,
                DamageRaw = dmg.ToString("0"),
                DpsRaw = dps.ToString("0"),
                DamageTakenRaw = taken.ToString("0"),
                HealedRaw = healed.ToString("0"),
                HpsRaw = (healed / 90f).ToString("0"),
                HealsTakenRaw = (taken * 0.8f).ToString("0"),
                Deaths = deaths.ToString(),
            };
        }

        Dictionary<string, Combatant> combatants = new()
        {
            ["Test Warrior"] = Make("Test Warrior", "WAR", 1_240_000, 13_800, 540_000, 90_000, 1_000, 0),
            ["Test Black Mage"] = Make("Test Black Mage", "BLM", 1_980_000, 22_000, 210_000, 0, 0, 1),
            ["Test Dancer"] = Make("Test Dancer", "DNC", 1_410_000, 15_600, 180_000, 60_000, 600, 0),
            ["Test White Mage"] = Make("Test White Mage", "WHM", 620_000, 6_900, 150_000, 1_350_000, 15_000, 0),
            ["Test Sage"] = Make("Test Sage", "SGE", 540_000, 6_000, 130_000, 1_120_000, 12_400, 0),
        };

        return new CombatEvent
        {
            Type = "CombatData",
            IsActive = "true",
            Encounter = new Encounter
            {
                Title = "Test Encounter",
                Duration = "01:30",
                DamageRaw = "5790000",
                DpsRaw = "64300",
                HealedRaw = "2530000",
                HpsRaw = "28100",
            },
            Combatants = combatants,
        };
    }
}
