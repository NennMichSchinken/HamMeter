using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using HamMeter.Data;

namespace HamMeter;

// Connects to IINACT over Dalamud IPC, receives "CombatData" events,
// keeps the current encounter and a history of finished encounters.
public sealed class IinactClient : IDisposable
{
    private const string SubscriptionEndpoint = "HamMeter.SubscriptionReceiver";
    private const string ListeningEndpoint = "IINACT.Server.Listening";
    private const string SubscribeEndpoint = "IINACT.CreateSubscriber";
    private const string UnsubscribeEndpoint = "IINACT.Unsubscribe";
    private const string ProviderEditEndpoint = "IINACT.IpcProvider." + SubscriptionEndpoint;
    private const string SubscriptionMessage = "{\"call\":\"subscribe\",\"events\":[\"CombatData\"]}";
    private const int MaxHistory = 50;

    private readonly IDalamudPluginInterface m_pluginInterface;
    private readonly IPluginLog m_log;
    private readonly ICallGateProvider<JObject, bool> m_receiver;

    private CombatEvent? m_lastCommitted;
    private bool m_suppressing;
    private float m_suppressRefDamage;
    private CombatEvent? m_combined;
    private int m_combinedCount = -1;

    // IINACT pushes data on a background thread while the UI reads it on the render
    // thread, so every access to the fields below must go through this lock.
    private readonly object m_sync = new();
    private CombatEvent? m_current;
    private readonly List<CombatEvent> m_past = new();

    public bool Connected { get; private set; }

    public CombatEvent? Current
    {
        get
        {
            lock (m_sync)
            {
                return m_current;
            }
        }
    }

    public int PastCount
    {
        get
        {
            lock (m_sync)
            {
                return m_past.Count;
            }
        }
    }

    public CombatEvent? GetPast(int index)
    {
        lock (m_sync)
        {
            return index >= 0 && index < m_past.Count ? m_past[index] : null;
        }
    }

    public List<CombatEvent> SnapshotPast()
    {
        lock (m_sync)
        {
            return new List<CombatEvent>(m_past);
        }
    }

    public IinactClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        m_pluginInterface = pluginInterface;
        m_log = log;

        m_receiver = m_pluginInterface.GetIpcProvider<JObject, bool>(SubscriptionEndpoint);
        m_receiver.RegisterFunc(this.ReceiveMessage);
    }

    public void Connect()
    {
        if (this.Connected)
        {
            return;
        }

        try
        {
            bool listening = m_pluginInterface.GetIpcSubscriber<bool>(ListeningEndpoint).InvokeFunc();
            if (!listening)
            {
                return;
            }

            m_pluginInterface.GetIpcSubscriber<string, bool>(SubscribeEndpoint).InvokeFunc(SubscriptionEndpoint);
            m_pluginInterface
                .GetIpcSubscriber<JObject, bool>(ProviderEditEndpoint)
                .InvokeAction(JObject.Parse(SubscriptionMessage));

            this.Connected = true;
            m_log.Information("HamMeter: connected to IINACT.");
        }
        catch (Exception)
        {
            this.Connected = false;
        }
    }

    private bool ReceiveMessage(JObject data)
    {
        try
        {
            CombatEvent? ev = data.ToObject<CombatEvent>();
            if (ev?.Encounter is null || ev.Combatants is null || ev.Combatants.Count == 0)
            {
                return true;
            }

            // All shared-state access happens under the lock (the UI reads these
            // same fields on the render thread).
            lock (m_sync)
            {
                if (m_suppressing)
                {
                    float dmg = Num.Parse(ev.Encounter.DamageRaw);
                    if (dmg + 1f < m_suppressRefDamage)
                    {
                        m_suppressing = false;
                    }
                    else
                    {
                        if (dmg > m_suppressRefDamage)
                        {
                            m_suppressRefDamage = dmg;
                        }

                        return true;
                    }
                }

                // Commit a finished encounter to history once (de-duped against the last one).
                if (!ev.Active && !ev.SameAs(m_lastCommitted))
                {
                    m_past.Add(ev);
                    m_lastCommitted = ev;
                    m_combined = null;

                    while (m_past.Count > MaxHistory)
                    {
                        m_past.RemoveAt(0);
                    }
                }

                m_current = ev;
            }

            return true;
        }
        catch (Exception ex)
        {
            m_log.Error(ex, "HamMeter: failed to parse combat data.");
            return false;
        }
    }

    public CombatEvent? GetOverall()
    {
        lock (m_sync)
        {
            if (m_past.Count == 0)
            {
                return m_current;
            }

            if (m_combined is null || m_combinedCount != m_past.Count)
            {
                m_combined = CombatEvent.BuildOverall(m_past);
                m_combinedCount = m_past.Count;
            }

            return m_combined;
        }
    }

    public void ClearData()
    {
        lock (m_sync)
        {
            // Start (or refresh) suppression based on what's currently showing. If nothing
            // is showing (e.g. a second reset while already cleared), keep the existing
            // suppression instead of turning it off.
            if (m_current?.Encounter is not null)
            {
                float refDamage = Num.Parse(m_current.Encounter.DamageRaw);
                if (refDamage > 0f)
                {
                    m_suppressing = true;
                    m_suppressRefDamage = refDamage;
                }
            }

            m_current = null;
            m_past.Clear();
            m_lastCommitted = null;
            m_combined = null;
            m_combinedCount = -1;
        }
    }

    public void Dispose()
    {
        try
        {
            m_pluginInterface.GetIpcSubscriber<string, bool>(UnsubscribeEndpoint).InvokeFunc(SubscriptionEndpoint);
        }
        catch (Exception)
        {
            // ignore on shutdown
        }
    }
}
