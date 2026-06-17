using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace SimpleMeter.Data;

// One "CombatData" push from IINACT: encounter totals + one entry per combatant.
public class CombatEvent
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("isActive")]
    public string IsActive { get; set; } = string.Empty;

    [JsonProperty("Encounter")]
    public Encounter? Encounter { get; set; }

    [JsonProperty("Combatant")]
    public Dictionary<string, Combatant>? Combatants { get; set; }

    [JsonIgnore]
    public bool Active => bool.TryParse(this.IsActive, out bool a) && a;

    // Two finished snapshots of the same fight look identical here; used to de-dup history.
    public bool SameAs(CombatEvent? other)
    {
        if (other?.Encounter is null || this.Encounter is null)
        {
            return false;
        }

        return this.Encounter.Title == other.Encounter.Title
            && this.Encounter.Duration == other.Encounter.Duration;
    }

    // Builds a synthetic "Overall" event by summing every finished encounter.
    public static CombatEvent BuildOverall(List<CombatEvent> events)
    {
        double totalSeconds = 0;
        foreach (CombatEvent ev in events)
        {
            totalSeconds += FightSeconds(ev.Encounter);
        }

        if (totalSeconds <= 0)
        {
            totalSeconds = 1;
        }

        Dictionary<string, Acc> agg = new();
        foreach (CombatEvent ev in events)
        {
            if (ev.Combatants is null)
            {
                continue;
            }

            foreach (Combatant c in ev.Combatants.Values)
            {
                if (string.IsNullOrEmpty(c.Name))
                {
                    continue;
                }

                if (!agg.TryGetValue(c.Name, out Acc? a))
                {
                    a = new Acc { Name = c.Name, Job = c.Job };
                    agg[c.Name] = a;
                }

                if (string.IsNullOrEmpty(a.Job) && !string.IsNullOrEmpty(c.Job))
                {
                    a.Job = c.Job;
                }

                a.Damage += c.DamageTotal;
                a.DamageTaken += c.DamageTaken;
                a.Healed += c.HealedTotal;
                a.HealingTaken += c.HealingTaken;
                a.Deaths += c.DeathCount;
            }
        }

        string dur = FormatDuration(totalSeconds);
        Dictionary<string, Combatant> combatants = new();
        float encDamage = 0;
        float encHealed = 0;
        foreach (Acc a in agg.Values)
        {
            encDamage += a.Damage;
            encHealed += a.Healed;
            combatants[a.Name] = new Combatant
            {
                Name = a.Name,
                Job = a.Job,
                DamageRaw = Str(a.Damage),
                DamageTakenRaw = Str(a.DamageTaken),
                HealedRaw = Str(a.Healed),
                HealsTakenRaw = Str(a.HealingTaken),
                Deaths = ((int)a.Deaths).ToString(),
                DpsRaw = Str((float)(a.Damage / totalSeconds)),
                HpsRaw = Str((float)(a.Healed / totalSeconds)),
            };
        }

        return new CombatEvent
        {
            Type = "CombatData",
            IsActive = "false",
            Encounter = new Encounter
            {
                Title = "Overall",
                Duration = dur,
                DamageRaw = Str(encDamage),
                HealedRaw = Str(encHealed),
                DpsRaw = Str((float)(encDamage / totalSeconds)),
                HpsRaw = Str((float)(encHealed / totalSeconds)),
            },
            Combatants = combatants,
        };
    }

    private sealed class Acc
    {
        public string Name = string.Empty;
        public string Job = string.Empty;
        public float Damage;
        public float DamageTaken;
        public float Healed;
        public float HealingTaken;
        public float Deaths;
    }

    // Precise fight length: the rounded mm:ss text loses sub-second precision
    // (a <1s burst shows "00:00"), so derive it from damage / encdps instead.
    private static double FightSeconds(Encounter? enc)
    {
        if (enc is null)
        {
            return 0;
        }

        float dmg = Num.Parse(enc.DamageRaw);
        float dps = Num.Parse(enc.DpsRaw);
        if (dps > 0 && dmg > 0)
        {
            return dmg / dps;
        }

        float healed = Num.Parse(enc.HealedRaw);
        float hps = Num.Parse(enc.HpsRaw);
        if (hps > 0 && healed > 0)
        {
            return healed / hps;
        }

        return ParseDuration(enc.Duration);
    }

    private static string Str(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static double ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        string[] p = raw.Split(':');
        try
        {
            if (p.Length == 2)
            {
                return (int.Parse(p[0], CultureInfo.InvariantCulture) * 60)
                    + double.Parse(p[1], CultureInfo.InvariantCulture);
            }

            if (p.Length == 3)
            {
                return (int.Parse(p[0], CultureInfo.InvariantCulture) * 3600)
                    + (int.Parse(p[1], CultureInfo.InvariantCulture) * 60)
                    + double.Parse(p[2], CultureInfo.InvariantCulture);
            }
        }
        catch (FormatException)
        {
            return 0;
        }

        return 0;
    }

    public static string FormatDuration(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}

public class Encounter
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("duration")]
    public string Duration { get; set; } = string.Empty;

    [JsonProperty("encdps")]
    public string DpsRaw { get; set; } = string.Empty;

    [JsonProperty("enchps")]
    public string HpsRaw { get; set; } = string.Empty;

    [JsonProperty("damage")]
    public string DamageRaw { get; set; } = string.Empty;

    [JsonProperty("healed")]
    public string HealedRaw { get; set; } = string.Empty;
}

public class Combatant
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("Job")]
    public string Job { get; set; } = string.Empty;

    [JsonProperty("encdps")]
    public string DpsRaw { get; set; } = string.Empty;

    [JsonProperty("enchps")]
    public string HpsRaw { get; set; } = string.Empty;

    [JsonProperty("damage")]
    public string DamageRaw { get; set; } = string.Empty;

    [JsonProperty("damagetaken")]
    public string DamageTakenRaw { get; set; } = string.Empty;

    [JsonProperty("healed")]
    public string HealedRaw { get; set; } = string.Empty;

    [JsonProperty("healstaken")]
    public string HealsTakenRaw { get; set; } = string.Empty;

    [JsonProperty("deaths")]
    public string Deaths { get; set; } = string.Empty;

    // Parsed values are cached on first access: IINACT sends them as strings, and the
    // render loop reads them many times per frame, so we parse each one only once.
    [JsonIgnore]
    private float? m_dps;
    [JsonIgnore]
    private float? m_hps;
    [JsonIgnore]
    private float? m_damageTotal;
    [JsonIgnore]
    private float? m_damageTaken;
    [JsonIgnore]
    private float? m_healedTotal;
    [JsonIgnore]
    private float? m_healingTaken;
    [JsonIgnore]
    private int? m_deathCount;

    [JsonIgnore]
    public float Dps => m_dps ??= Num.Parse(this.DpsRaw);

    [JsonIgnore]
    public float Hps => m_hps ??= Num.Parse(this.HpsRaw);

    [JsonIgnore]
    public float DamageTotal => m_damageTotal ??= Num.Parse(this.DamageRaw);

    [JsonIgnore]
    public float DamageTaken => m_damageTaken ??= Num.Parse(this.DamageTakenRaw);

    [JsonIgnore]
    public float HealedTotal => m_healedTotal ??= Num.Parse(this.HealedRaw);

    [JsonIgnore]
    public float HealingTaken => m_healingTaken ??= Num.Parse(this.HealsTakenRaw);

    [JsonIgnore]
    public int DeathCount => m_deathCount ??= (int.TryParse(this.Deaths, out int d) ? d : 0);
}

internal static class Num
{
    public static float Parse(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
}
