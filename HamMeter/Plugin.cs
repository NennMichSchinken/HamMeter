using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SimpleMeter.Data;

namespace SimpleMeter;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/hammeter";
    private const string CommandAlias = "/ham";

    private static readonly HashSet<string> Tanks = new(StringComparer.OrdinalIgnoreCase)
    { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };

    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
    { "WHM", "SCH", "AST", "SGE", "CNJ" };

    // Precomputed "1." .. "24." so the render loop doesn't allocate a rank string per bar.
    private static readonly string[] RankLabels = BuildRankLabels();

    private static string[] BuildRankLabels()
    {
        string[] labels = new string[24];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = (i + 1) + ".";
        }

        return labels;
    }

    private readonly IDalamudPluginInterface m_pluginInterface;
    private readonly ICommandManager m_commandManager;
    private readonly IPluginLog m_log;
    private readonly IFramework m_framework;
    private readonly IClientState m_clientState;
    private readonly IObjectTable m_objectTable;
    private readonly IChatGui m_chatGui;
    private readonly ICondition m_condition;
    private readonly IinactClient m_client;
    private readonly Config m_config;
    private readonly SettingsWindow m_settings;
    private readonly ITextureProvider m_textureProvider;
    private readonly Dictionary<string, float> m_animFractions = new();

    private bool m_visible = true;
    private bool m_wasInDuty;
    private bool m_pendingDutyCheck;
    private DateTime m_pendingSince = DateTime.MinValue;
    private Metric m_metric = Metric.DamageDone;
    private int m_view = -1; // -1 = Current, -2 = Overall, >=0 = past index
    private DateTime m_lastConnectAttempt = DateTime.MinValue;
    private bool m_wasInCombat;
    private DateTime m_pendingEndAt = DateTime.MaxValue;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IChatGui chatGui,
        ICondition condition,
        ITextureProvider textureProvider
    )
    {
        m_pluginInterface = pluginInterface;
        m_commandManager = commandManager;
        m_log = log;
        m_framework = framework;
        m_clientState = clientState;
        m_objectTable = objectTable;
        m_chatGui = chatGui;
        m_condition = condition;
        m_textureProvider = textureProvider;

        m_config = pluginInterface.GetPluginConfig() as Config ?? new Config();
        m_config.Initialize(pluginInterface);

        m_wasInDuty = this.IsInDuty();

        m_client = new IinactClient(pluginInterface, log);
        m_client.Connect();

        m_settings = new SettingsWindow(m_config);

        m_commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle HamMeter. '/hammeter config' settings, '/hammeter test' test mode, '/hammeter clear' clear, '/hammeter end' end encounter.",
        });

        m_commandManager.AddHandler(CommandAlias, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Alias for /hammeter.",
        });

        m_pluginInterface.UiBuilder.Draw += this.Draw;
        m_pluginInterface.UiBuilder.OpenMainUi += this.OpenMain;
        m_pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        m_framework.Update += this.OnUpdate;
        m_clientState.TerritoryChanged += this.OnTerritoryChanged;
    }

    // A zone change arms a check: if we become bound by duty shortly after, we
    // entered an instance (dungeon/raid/trial) and reset. This also covers going
    // straight from one duty into another, where the duty flag never drops.
    private void OnTerritoryChanged(uint territoryId)
    {
        // Drop per-player animation state so it can't grow unbounded across long
        // sessions with many distinct players (Eureka/Bozja/alliance raids).
        m_animFractions.Clear();

        m_pendingDutyCheck = true;
        m_pendingSince = DateTime.Now;
    }

    private void OpenMain() => m_visible = true;

    private void OpenConfig() => m_settings.Visible = true;

    private void OnUpdate(IFramework framework)
    {
        if (!m_client.Connected && (DateTime.Now - m_lastConnectAttempt).TotalSeconds > 3)
        {
            m_lastConnectAttempt = DateTime.Now;
            m_client.Connect();
        }

        // Auto-reset on entering instanced content (duty), never on leaving.
        bool inDuty = this.IsInDuty();
        if (m_config.AutoResetInDuty && inDuty && !m_wasInDuty)
        {
            this.ClearAll();
        }

        m_wasInDuty = inDuty;

        // Optionally finalise the IINACT encounter a few seconds after combat ends,
        // so the next pull starts as a cleanly separated encounter.
        bool inCombat = m_condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            m_pendingEndAt = DateTime.MaxValue;
        }
        else if (m_wasInCombat && m_config.AutoEndCombat)
        {
            m_pendingEndAt = DateTime.Now.AddSeconds(3);
        }

        if (DateTime.Now >= m_pendingEndAt)
        {
            m_pendingEndAt = DateTime.MaxValue;
            this.SendActCommand("end");
        }

        m_wasInCombat = inCombat;

        // Zone change armed a check: reset once we're confirmed bound by duty.
        if (m_pendingDutyCheck)
        {
            if (inDuty)
            {
                if (m_config.AutoResetInDuty)
                {
                    this.ClearAll();
                }

                m_pendingDutyCheck = false;
            }
            else if ((DateTime.Now - m_pendingSince).TotalSeconds > 8)
            {
                // Not a duty (city/overworld) - drop the pending check.
                m_pendingDutyCheck = false;
            }
        }
    }

    private bool IsInDuty() =>
        m_condition[ConditionFlag.BoundByDuty]
        || m_condition[ConditionFlag.BoundByDuty56]
        || m_condition[ConditionFlag.BoundByDuty95];

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "cfg":
            case "settings":
                m_settings.Visible = !m_settings.Visible;
                break;
            case "test":
                m_config.TestMode = !m_config.TestMode;
                m_config.Save();
                break;
            case "clear":
                this.ClearAll();
                break;
            case "end":
                this.SendActCommand("end");
                break;
            default:
                m_visible = !m_visible;
                break;
        }
    }

    // IINACT understands the ACT "end" echo command (it finalises the current
    // encounter). Note: IINACT does NOT handle "clear", so we only use "end".
    private void SendActCommand(string cmd)
    {
        try
        {
            m_chatGui.Print(new XivChatEntry { Message = cmd, Type = XivChatType.Echo });
        }
        catch (Exception ex)
        {
            m_log.Error(ex, "Failed to send ACT command");
        }
    }

    private void Draw()
    {
        m_settings.Draw();
        this.DrawMeter();
    }

    private void ClearAll()
    {
        if (m_config.EndEncounterOnReset)
        {
            this.SendActCommand("end");
        }

        m_client.ClearData();
        m_animFractions.Clear();
        m_view = -1;
    }

    private CombatEvent? GetDisplayedEvent()
    {
        if (m_config.TestMode)
        {
            return TestData.Build();
        }

        if (m_view == -2)
        {
            return m_client.GetOverall();
        }

        if (m_view >= 0 && m_view < m_client.PastCount)
        {
            return m_client.GetPast(m_view);
        }

        return m_client.Current;
    }

    private void DrawMeter()
    {
        if (!m_visible)
        {
            return;
        }

        if (!m_clientState.IsLoggedIn)
        {
            return;
        }

        CombatEvent? ev = this.GetDisplayedEvent();

        if (m_config.OnlyInCombat && !m_config.TestMode && (ev is null || !ev.Active))
        {
            return;
        }

        Vector4 bg = m_config.BackgroundColor;
        bg.W = m_config.BackgroundOpacity;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, m_config.BarSpacing));

        ImGui.SetNextWindowSize(new Vector2(360f, 300f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(340f, 110f), new Vector2(2000f, 2000f));
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse;
        if (m_config.Locked)
        {
            flags |= ImGuiWindowFlags.NoMove;
        }

        // Title is hidden (NoTitleBar). The "###main" suffix fixes the ImGui id to a
        // stable value regardless of the visible label, so the saved position sticks.
        if (ImGui.Begin("HamMeter###main", ref m_visible, flags))
        {
            Vector2 winPos = ImGui.GetWindowPos();
            Vector2 winSize = ImGui.GetWindowSize();
            m_settings.SetMeterRect(winPos, winSize);

            // Check this in the main-window scope: the popup id is created here, and
            // a child window has a different id seed (so IsPopupOpen would miss it).
            bool resetOpen = ImGui.IsPopupOpen("reset_confirm");

            // Self-drawn rounded background so all four corners match the settings look.
            ImGui.GetWindowDrawList().AddRectFilled(
                winPos,
                new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y),
                Col(bg),
                10f,
                ImDrawFlags.RoundCornersAll);

            // Matching rounded border (same colour as the settings window).
            ImGui.GetWindowDrawList().AddRect(
                winPos,
                new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y),
                Col(new Vector4(0.173f, 0.173f, 0.18f, 1f)),
                10f,
                ImDrawFlags.RoundCornersAll,
                1f);

            this.DrawHeader(ev);

            // Bars live in a scrollable child so the header stays fixed and the
            // list can scroll when there are more players than fit the window.
            const float pad = 6f;
            float headerH = m_config.HeaderHeight;
            ImGui.SetCursorScreenPos(new Vector2(winPos.X + pad, winPos.Y + headerH + m_config.BarSpacing));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
            float barsH = Math.Max(1f, winSize.Y - headerH - m_config.BarSpacing - pad);
            if (ImGui.BeginChild("##bars", new Vector2(winSize.X - (pad * 2f), barsH)))
            {
                this.DrawBars(ev);

                // When the reset confirmation is open, dim the whole meter window
                // (header + bars) so the popup looks like it sits on top. Drawn on
                // the child's draw list with a full-screen clip so it covers the
                // header too, and stays below the popup window.
                if (resetOpen)
                {
                    ImDrawListPtr ddl = ImGui.GetWindowDrawList();
                    ddl.PushClipRectFullScreen();
                    ddl.AddRectFilled(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y), Col(new Vector4(0f, 0f, 0f, 0.5f)), 10f, ImDrawFlags.RoundCornersAll);
                    ddl.PopClipRect();
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleVar();
            this.DrawPopups();
        }

        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);
    }

    private void DrawHeader(CombatEvent? ev)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 wp = ImGui.GetWindowPos();
        float width = ImGui.GetWindowSize().X;
        float h = m_config.HeaderHeight;

        Vector4 headerColor = m_config.HeaderColor;
        headerColor.W = m_config.HeaderOpacity;
        dl.AddRectFilled(wp, new Vector2(wp.X + width, wp.Y + h), Col(headerColor), 10f, ImDrawFlags.RoundCornersTop);

        string viewLabel = m_view == -2 ? "Overall" : m_view >= 0 ? "History" : "Current";
        string duration = ev?.Encounter?.Duration ?? "00:00";
        string main = $"{Metrics.Name(m_metric)}  -  {viewLabel}   ";
        string paren = $"({duration})";
        float ts = m_config.TopTextSize;
        float ty = wp.Y + ((h - ts) / 2f);
        float tx = wp.X + 12f;
        uint white = Col(new Vector4(1f, 1f, 1f, 1f));
        uint muted = Col(new Vector4(0.557f, 0.557f, 0.576f, 1f));
        this.Text(dl, new Vector2(tx, ty), main, ts, white);
        this.Text(dl, new Vector2(tx + this.TextW(main, ts), ty), paren, ts, muted);

        // Icon row, right-aligned. Drawn from the right, so the visible left-to-right
        // order is: Reset, History, Metric, Settings.
        float bsize = m_config.IconSize;
        float gap = m_config.IconSpacing;
        float iconY = wp.Y + ((h - bsize) / 2f);
        float x = wp.X + width - bsize - 10f;

        if (this.IconButton("ham_cfg", FontAwesomeIcon.Cog, new Vector2(x, iconY), bsize))
        {
            m_settings.Visible = !m_settings.Visible;
        }

        x -= bsize + gap;
        if (this.IconButton("ham_metric", FontAwesomeIcon.ExchangeAlt, new Vector2(x, iconY), bsize))
        {
            ImGui.OpenPopup("metric_popup");
        }

        x -= bsize + gap;
        if (this.IconButton("ham_hist", FontAwesomeIcon.ClipboardList, new Vector2(x, iconY), bsize))
        {
            ImGui.OpenPopup("hist_popup");
        }

        x -= bsize + gap;
        if (this.IconButton("ham_clear", FontAwesomeIcon.Sync, new Vector2(x, iconY), bsize))
        {
            if (m_config.ConfirmReset)
            {
                ImGui.OpenPopup("reset_confirm");
            }
            else
            {
                this.ClearAll();
            }
        }
    }

    // Limit Break has no real job; IINACT labels it "Limit Break".
    private static bool IsLimitBreak(Combatant c) =>
        string.Equals(c.Job, "Limit Break", StringComparison.OrdinalIgnoreCase)
        || string.Equals(c.Name, "Limit Break", StringComparison.OrdinalIgnoreCase);

    // Only show real players (recognized job) plus Limit Break; drop enemies and pets.
    private static bool IsShown(Combatant c) =>
        !string.IsNullOrEmpty(c.Name) && (JobInfo.RowIds.ContainsKey(c.Job) || IsLimitBreak(c));

    private void DrawBars(CombatEvent? ev)
    {        if (!m_config.TestMode && !m_client.Connected)
        {
            ImGui.TextUnformatted("Not connected to IINACT.");
            return;
        }

        if (ev?.Combatants is null || ev.Combatants.Count == 0)
        {
            ImGui.TextUnformatted("Waiting for combat data...");
            return;
        }

        List<Combatant> sorted = ev.Combatants.Values
            .Where(IsShown)
            .OrderByDescending(c => Metrics.Value(c, m_metric))
            .ToList();

        if (sorted.Count == 0)
        {
            return;
        }

        float max = Math.Max(1f, Metrics.Value(sorted[0], m_metric));
        int rank = 1;
        float width = ImGui.GetContentRegionAvail().X;
        foreach (Combatant c in sorted)
        {
            this.DrawBar(c, max, rank, width);
            rank++;
        }
    }

    private void DrawBar(Combatant c, float max, int rank, float width)
    {
        float value = Metrics.Value(c, m_metric);
        float target = max > 0 ? Math.Clamp(value / max, 0f, 1f) : 0f;
        float fraction = this.AnimateFraction(c.Name, target);

        float rowH = m_config.BarHeight;
        float barH = rowH - 2f;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        if (width < 1f)
        {
            width = 1f;
        }

        bool known = JobInfo.RowIds.ContainsKey(c.Job);
        Vector4 baseCol = this.BarColor(c.Job, known);
        Vector4 fill = baseCol;
        fill.W = m_config.BarOpacity;
        Vector4 trackCol = m_config.BarTrackColor;
        trackCol.W *= m_config.BarOpacity;
        uint track = Col(trackCol);
        float round = m_config.RoundedBars ? 6f : 0f;
        dl.AddRectFilled(pos, new Vector2(pos.X + width, pos.Y + barH), track, round);

        // Filled portion: rounded base colour, then a vertical depth gradient over the
        // FULL width (darker toward the bottom) so the rounded caps get depth too. The
        // darkening sits transparent at the top, so the top rounded corners stay clean.
        float fillW = width * fraction;
        Vector2 fa = pos;
        Vector2 fb = new(pos.X + fillW, pos.Y + barH);
        dl.AddRectFilled(fa, fb, Col(fill), round, ImDrawFlags.RoundCornersAll);

        if (fillW > 2f)
        {
            uint clearC = Col(new Vector4(0f, 0f, 0f, 0f));
            uint darkC = Col(new Vector4(0f, 0f, 0f, 0.24f * m_config.BarOpacity));
            dl.AddRectFilledMultiColor(fa, fb, clearC, clearC, darkC, darkC);

            // Soft top sheen in the centre (inset so it never pokes past the corners).
            if (fillW > (round * 2f) + 4f)
            {
                uint sheen = Col(new Vector4(1f, 1f, 1f, 0.12f * m_config.BarOpacity));
                uint sheenT = Col(new Vector4(1f, 1f, 1f, 0f));
                dl.AddRectFilledMultiColor(
                    new Vector2(fa.X + round, fa.Y + 1f),
                    new Vector2(fb.X - round, fa.Y + (barH * 0.5f)),
                    sheen, sheen, sheenT, sheenT);
            }
        }

        uint white = Col(new Vector4(1f, 1f, 1f, 1f));
        float leftSize = m_config.LeftTextSize;
        float rightSize = m_config.RightTextSize;
        float textY = pos.Y + ((barH - leftSize) / 2f);
        float x = pos.X + 6f;

        if (m_config.ShowRankNumbers)
        {
            string r = rank >= 1 && rank <= RankLabels.Length ? RankLabels[rank - 1] : rank + ".";
            this.Text(dl, new Vector2(x, textY), r, leftSize, white);
            x += this.TextW(r, leftSize) + 5f;
        }

        x = this.DrawJobIndicator(dl, c.Job, baseCol, pos, x, barH, leftSize, known);

        this.Text(dl, new Vector2(x, textY), this.DisplayName(c), leftSize, white);

        string right = this.FormatValue(c, value);
        float rw = this.TextW(right, rightSize);
        this.Text(dl, new Vector2(pos.X + width - rw - 6f, pos.Y + ((barH - rightSize) / 2f)), right, rightSize, white);

        ImGui.Dummy(new Vector2(width, rowH));
    }

    private float DrawJobIndicator(ImDrawListPtr dl, string job, Vector4 baseCol, Vector2 pos, float x, float barH, float leftSize, bool known)
    {

        // No indicator when it's off, the job is empty, or it's a non-job entry
        // (e.g. Limit Break) — in those cases we just show the name.
        if (m_config.JobIndicator == 0 || string.IsNullOrEmpty(job) || !known)
        {
            return x;
        }

        float boxTop = pos.Y + 2f;
        float boxBottom = pos.Y + barH - 2f;
        float boxH = boxBottom - boxTop;

        // Icon mode: try the real job icon, fall back to the text tag if it isn't ready.
        if (m_config.JobIndicator == 2)
        {
            IDalamudTextureWrap? icon = this.GetJobIcon(job);
            if (icon is not null)
            {
                dl.AddImage(icon.Handle, new Vector2(x, boxTop), new Vector2(x + boxH, boxBottom));
                return x + boxH + 5f;
            }
        }

        // Text tag with uniform width (based on a 3-letter reference so all align).
        float tagW = this.TextW("WWW", leftSize - 1f) + 6f;
        Vector4 tagCol = baseCol;
        tagCol.W = 0.9f;
        dl.AddRectFilled(new Vector2(x, boxTop), new Vector2(x + tagW, boxBottom), Col(tagCol), 2f);

        string label = job.ToUpperInvariant();
        float lw = this.TextW(label, leftSize - 1f);
        float lx = x + ((tagW - lw) / 2f);
        this.Text(dl, new Vector2(lx, pos.Y + ((barH - (leftSize - 1f)) / 2f)), label, leftSize - 1f, Col(new Vector4(1f, 1f, 1f, 1f)), false);
        return x + tagW + 5f;
    }

    private IDalamudTextureWrap? GetJobIcon(string job)
    {
        int iconId = JobInfo.IconId(job);
        if (iconId == 0)
        {
            return null;
        }

        try
        {
            return m_textureProvider.GetFromGameIcon(new GameIconLookup((uint)iconId)).GetWrapOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private float AnimateFraction(string name, float target)
    {
        if (!m_config.SmoothBars)
        {
            m_animFractions[name] = target;
            return target;
        }

        float current = m_animFractions.TryGetValue(name, out float v) ? v : target;
        float dt = ImGui.GetIO().DeltaTime;
        float step = Math.Clamp(dt * 10f, 0f, 1f);
        current += (target - current) * step;
        m_animFractions[name] = current;
        return current;
    }

    private string FormatValue(Combatant c, float value)
    {
        if (Metrics.IsCount(m_metric))
        {
            return ((int)value).ToString();
        }

        string main = this.Fmt(value);
        if (Metrics.HasRate(m_metric))
        {
            return $"{main}  ({this.Fmt(Metrics.Rate(c, m_metric))})";
        }

        return main;
    }

    private void DrawPopups()
    {
        // Uniform minimum width for both dropdowns, based on the widest metric label,
        // so the short History popup never looks narrower than the Metric popup.
        float minW = 120f;
        foreach (Metric m in Metrics.All)
        {
            minW = MathF.Max(minW, ImGui.CalcTextSize(Metrics.Name(m)).X + 36f);
        }

        Vector2 sizeMin = new(minW, 0f);
        Vector2 sizeMax = new(float.MaxValue, float.MaxValue);

        Theme.PushDropdown();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        ImGui.SetNextWindowSizeConstraints(sizeMin, sizeMax);
        if (ImGui.BeginPopup("metric_popup"))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 10f));
            foreach (Metric m in Metrics.All)
            {
                if (ImGui.Selectable(Metrics.Name(m), m == m_metric))
                {
                    m_metric = m;
                }
            }

            ImGui.PopStyleVar();
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        ImGui.SetNextWindowSizeConstraints(sizeMin, sizeMax);
        if (ImGui.BeginPopup("hist_popup"))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 10f));
            if (ImGui.Selectable("Current", m_view == -1))
            {
                m_view = -1;
            }

            if (ImGui.Selectable("Overall", m_view == -2))
            {
                m_view = -2;
            }

            List<CombatEvent> past = m_client.SnapshotPast();
            if (past.Count > 0)
            {
                ImGui.Separator();
                for (int i = past.Count - 1; i >= 0; i--)
                {
                    Encounter? enc = past[i].Encounter;
                    string label = $"{enc?.Title} ({enc?.Duration})##{i}";
                    if (ImGui.Selectable(label, m_view == i))
                    {
                        m_view = i;
                    }
                }
            }

            ImGui.PopStyleVar();
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();

        // Done with the dropdowns.
        Theme.PopDropdown();

        // Confirmation dialog for the reset button.
        Theme.PushDialog();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));

        Vector2 wpos = ImGui.GetWindowPos();
        Vector2 wsize = ImGui.GetWindowSize();
        ImGui.SetNextWindowPos(
            new Vector2(wpos.X + (wsize.X * 0.5f), wpos.Y + (wsize.Y * 0.5f)),
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopup("reset_confirm", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.TextUnformatted("Reset all combat data?");
            ImGui.Dummy(new Vector2(0f, 6f));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f));
            if (ImGui.Button("Reset", new Vector2(100f, 0f)))
            {
                this.ClearAll();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine(0f, 16f);
            if (ImGui.Button("Cancel", new Vector2(100f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleVar();
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();
        Theme.PopDialog();
    }

    private bool IconButton(string id, FontAwesomeIcon icon, Vector2 pos, float size)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        bool hovered = ImGui.IsItemHovered();

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (hovered)
        {
            dl.AddRectFilled(pos, new Vector2(pos.X + size, pos.Y + size), Col(new Vector4(1f, 1f, 1f, 0.15f)), 6f);
        }

        string s = icon.ToIconString();
        float gw = this.IconWidth(s, size);
        Vector2 gpos = new(pos.X + ((size - gw) / 2f), pos.Y);
        dl.AddText(UiBuilder.IconFont, size, new Vector2(gpos.X + 1f, gpos.Y + 1f), Col(new Vector4(0f, 0f, 0f, 0.9f)), s);
        dl.AddText(UiBuilder.IconFont, size, gpos, Col(new Vector4(1f, 1f, 1f, 1f)), s);
        return clicked;
    }

    private float IconWidth(string s, float size)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        float baseW = ImGui.CalcTextSize(s).X;
        float baseSize = ImGui.GetFontSize();
        ImGui.PopFont();
        return baseSize > 0 ? baseW * (size / baseSize) : baseW;
    }

    // IINACT/ACT labels the local player "YOU"; show the real character name instead.
    private string DisplayName(Combatant c)
    {
        if (string.Equals(c.Name, "YOU", StringComparison.OrdinalIgnoreCase))
        {
            string? real = m_objectTable.LocalPlayer?.Name.TextValue;
            if (!string.IsNullOrEmpty(real))
            {
                return real;
            }
        }

        return c.Name;
    }

    private Vector4 BarColor(string job, bool known)
    {
        // Non-job entries (e.g. Limit Break) get a neutral grey.
        if (!known)
        {
            return new Vector4(0.55f, 0.55f, 0.6f, 1f);
        }

        // All job sets/dictionaries are case-insensitive, so the raw job string works.
        if (m_config.BarColorMode == 1)
        {
            if (m_config.JobColors.TryGetValue(job, out Vector4 jc))
            {
                jc.W = 1f;
                return jc;
            }
        }

        Vector4 c = Tanks.Contains(job) ? m_config.TankColor
            : Healers.Contains(job) ? m_config.HealerColor
            : m_config.DpsColor;
        c.W = 1f;
        return c;
    }

    private string Fmt(float v)
    {
        if (m_config.ShortNumbers)
        {
            if (v >= 1_000_000)
            {
                return (v / 1_000_000f).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            }

            if (v >= 1000)
            {
                return (v / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            }

            return v.ToString("0", CultureInfo.InvariantCulture);
        }

        return ((long)v).ToString("N0", CultureInfo.InvariantCulture);
    }

    private void Text(ImDrawListPtr dl, Vector2 pos, string s, float size, uint col, bool shadow = true)
    {
        ImFontPtr font = ImGui.GetFont();
        if (shadow)
        {
            dl.AddText(font, size, new Vector2(pos.X + 1f, pos.Y + 1f), Col(new Vector4(0f, 0f, 0f, 0.9f)), s);
        }

        dl.AddText(font, size, pos, col, s);
    }

    private float TextW(string s, float size)
    {
        float baseSize = ImGui.GetFontSize();
        return baseSize > 0 ? ImGui.CalcTextSize(s).X * (size / baseSize) : ImGui.CalcTextSize(s).X;
    }

    private static uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);

    public void Dispose()
    {
        m_framework.Update -= this.OnUpdate;
        m_clientState.TerritoryChanged -= this.OnTerritoryChanged;
        m_pluginInterface.UiBuilder.Draw -= this.Draw;
        m_pluginInterface.UiBuilder.OpenMainUi -= this.OpenMain;
        m_pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        m_commandManager.RemoveHandler(CommandName);
        m_commandManager.RemoveHandler(CommandAlias);
        m_client.Dispose();
    }
}
