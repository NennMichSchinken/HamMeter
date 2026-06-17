using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace HamMeter;

public class Config : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    // --- Display ---
    public bool OnlyInCombat = false;
    public bool AutoResetInDuty = true;
    public bool ConfirmReset = true;
    public bool Locked = false;
    public bool EndEncounterOnReset = false;
    public bool AutoEndCombat = false;
    public float BackgroundOpacity = 0.8f;
    public Vector4 BackgroundColor = new(0.086f, 0.086f, 0.102f, 1f); // #16161A (settings window bg)

    // --- Header ---
    public float HeaderHeight = 40f;
    public float HeaderOpacity = 1f;
    public Vector4 HeaderColor = new(0.133f, 0.133f, 0.165f, 1f); // #22222A (settings frame)
    public int TopTextSize = 16;
    public float IconSize = 16f;
    public float IconSpacing = 8f;

    // --- Bars ---
    public float BarHeight = 30f;
    public float BarSpacing = 2f;
    public float BarOpacity = 1f;
    public Vector4 BarTrackColor = new(0.173f, 0.173f, 0.212f, 0.6f); // empty-bar background, fits the UI
    public bool ShowRankNumbers = true;
    public bool RoundedBars = true;
    public bool SmoothBars = true;

    // Job indicator on the bar: 0 = off, 1 = text tag (GNB), 2 = job icon.
    public int JobIndicator = 2;

    // Bar color: 0 = by role, 1 = by job.
    public int BarColorMode = 1;

    // --- Bar text ---
    public int LeftTextSize = 16;
    public int RightTextSize = 16;
    public bool ShortNumbers = true;

    // --- Colors (by role) ---
    public Vector4 TankColor = new(0.30f, 0.50f, 0.90f, 1f);
    public Vector4 HealerColor = new(0.30f, 0.80f, 0.40f, 1f);
    public Vector4 DpsColor = new(0.85f, 0.35f, 0.35f, 1f);

    // --- Colors (by job) ---
    public Dictionary<string, Vector4> JobColors = new();

    // --- Testing ---
    public bool TestMode = false;

    [JsonIgnore]
    private IDalamudPluginInterface? m_pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        m_pluginInterface = pluginInterface;

        // Migrate older configs to the new default job palette once.
        if (this.Version < 2)
        {
            this.JobColors = JobInfo.DefaultColors();
            this.Version = 2;
        }

        // Align the meter colours/header with the settings palette once.
        if (this.Version < 3)
        {
            this.BackgroundColor = new Vector4(0.086f, 0.086f, 0.102f, 1f);
            this.HeaderColor = new Vector4(0.133f, 0.133f, 0.165f, 1f);
            this.HeaderHeight = 40f;
            this.Version = 3;
        }

        this.Save();

        this.EnsureJobColors();
    }

    public void EnsureJobColors()
    {
        // Rebuild with a case-insensitive comparer: a config loaded from JSON comes back
        // with the default (case-sensitive) comparer, which is why lookups previously
        // needed ToUpperInvariant. Normalising here lets callers use the raw job string.
        if (!ReferenceEquals(this.JobColors.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string, Vector4> ci = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Vector4> kv in this.JobColors)
            {
                ci[kv.Key] = kv.Value;
            }

            this.JobColors = ci;
        }

        foreach (KeyValuePair<string, Vector4> kv in JobInfo.DefaultColors())
        {
            this.JobColors.TryAdd(kv.Key, kv.Value);
        }
    }

    public void ResetJobColors()
    {
        this.JobColors = JobInfo.DefaultColors();
        this.Save();
    }

    public void Save()
    {
        m_pluginInterface?.SavePluginConfig(this);
    }
}
