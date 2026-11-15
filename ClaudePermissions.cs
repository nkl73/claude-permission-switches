using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program {
    [STAThread]
    static void Main(string[] args) {
        if (args.Length == 2 && args[0] == "--test-settings") {
            Environment.ExitCode = Settings.Test(args[1]);
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainWindow(Settings.UserPath));
    }
}

internal sealed class Settings {
    internal static string UserPath {
        get {
            var custom = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var directory = String.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : Path.GetFullPath(custom);
            return Path.Combine(directory, "settings.json");
        }
    }

    internal static readonly string[] Shell = { "Bash", "PowerShell", "Monitor" };
    internal static readonly string[] Web = { "WebFetch", "WebSearch" };
    internal static readonly string[] Secrets = { "Read(//**/.env)", "Read(//**/.env.*)", "Read(//**/.npmrc)" };
    private const string McpBlock = "mcp__*";
    private const string ComputerServer = "computer-use";
    private const string ToolNameCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
    private static readonly string[] OtherMcpDeny = BuildMcpExclusions();
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
    internal readonly Dictionary<string, object> Root;
    private readonly Dictionary<string, object> permissions;
    private readonly Dictionary<string, object> env;
    private readonly string path;
    private readonly string original;
    private readonly List<string> deny;

    internal Settings(string path) {
        this.path = path;
        if (!File.Exists(path)) throw new IOException("找不到现有 Claude Code 设置");
        original = File.ReadAllText(path);
        Root = serializer.DeserializeObject(original) as Dictionary<string, object>;
        if (Root == null) throw new FormatException("Claude Code 设置必须是 JSON 对象");
        permissions = Map(Root, "permissions");
        env = Map(Root, "env");
        deny = List(permissions, "deny").Select(x => {
            var text = x as string;
            if (text == null) throw new FormatException("工具规则中有无效条目");
            return text;
        }).ToList();
    }

    private static Dictionary<string, object> Map(Dictionary<string, object> parent, string key) {
        object value;
        if (!parent.TryGetValue(key, out value)) {
            var map = new Dictionary<string, object>();
            parent[key] = map;
            return map;
        }
        var existing = value as Dictionary<string, object>;
        if (existing == null) throw new FormatException("设置项格式无效：" + key);
        return existing;
    }

    private static object[] List(Dictionary<string, object> parent, string key) {
        object value;
        if (!parent.TryGetValue(key, out value)) return new object[0];
        var array = value as object[];
        if (array == null) throw new FormatException("设置项格式无效：" + key);
        return array;
    }

    private static bool Is(Dictionary<string, object> map, string key, object value) {
        object present;
        return map.TryGetValue(key, out present) && Object.Equals(present, value);
    }

    private static bool? Group(IEnumerable<bool> checks) {
        bool[] values = checks.ToArray();
        return values.All(v => v) ? true : values.All(v => !v) ? (bool?)false : null;
    }

    internal bool? ReadState(string key) {
        switch (key) {
            case "computer":
                if (deny.Contains(McpBlock)) return false;
                return OtherMcpDeny.All(deny.Contains) && !deny.Contains("mcp__computer-use") &&
                    !deny.Contains("mcp__computer-use__*") ? true : (bool?)null;
            case "files": return Group(new[] {
                Is(permissions, "blockReadsOutsideWorkingDirectories", true),
                Is(permissions, "blockWritesOutsideWorkingDirectories", true)
            });
            case "secrets": return Group(Secrets.Select(deny.Contains));
            case "shell": return Group(Shell.Select(deny.Contains));
            case "web": return Group(Web.Select(deny.Contains));
            case "telemetry": return NonessentialTrafficIsDisabled();
            case "memory": return Is(Root, "autoMemoryEnabled", false);
            default: throw new ArgumentException(key);
        }
    }

    internal string ProxyAddress() {
        object proxy;
        return env.TryGetValue("HTTPS_PROXY", out proxy) ? proxy as string : null;
    }

    private bool NonessentialTrafficIsDisabled() {
        object value;
        return env.TryGetValue("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", out value)
            && !String.IsNullOrEmpty(value as string);
    }

    private void ChangeRules(string[] rules, bool block) {
        foreach (var rule in rules) {
            if (block && !deny.Contains(rule)) deny.Add(rule);
            if (!block) deny.RemoveAll(x => x == rule);
        }
    }

    private static string[] BuildMcpExclusions() {
        // A tool that differs from the permitted MCP server prefix matches at its first differing character.
        var allowPrefix = ComputerServer + "__";
        var exclusions = new List<string>();
        for (int i = 0; i < allowPrefix.Length; i++) {
            foreach (char candidate in ToolNameCharacters) {
                if (candidate != allowPrefix[i])
                    exclusions.Add("mcp__" + allowPrefix.Substring(0, i) + candidate + "*");
            }
        }
        return exclusions.ToArray();
    }

    internal void Change(string key, bool value) {
        switch (key) {
            case "computer":
                if (value) {
                    if (!Is(Root, "disableClaudeAiConnectors", true) ||
                        !Root.ContainsKey("allowedMcpServers") || List(Root, "allowedMcpServers").Length != 0)
                        throw new InvalidOperationException("连接器配置已改动，无法只放行电脑操作");
                    ChangeRules(OtherMcpDeny, true);
                    ChangeRules(new[] { McpBlock, "mcp__computer-use", "mcp__computer-use__*" }, false);
                } else {
                    ChangeRules(OtherMcpDeny, false);
                    ChangeRules(new[] { McpBlock }, true);
                }
                break;
            case "files":
                permissions["blockReadsOutsideWorkingDirectories"] = value;
                permissions["blockWritesOutsideWorkingDirectories"] = value;
                break;
            case "secrets": ChangeRules(Secrets, value); break;
            case "shell": ChangeRules(Shell, value); break;
            case "web": ChangeRules(Web, value); break;
            case "telemetry":
                if (value) env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
                else env.Remove("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC");
                break;
            case "memory": Root["autoMemoryEnabled"] = !value; break;
            default: throw new ArgumentException(key);
        }
        permissions["deny"] = deny.ToArray();
    }

    internal void Save() {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, Indent(serializer.Serialize(Root)), new UTF8Encoding(false));
            if (File.ReadAllText(path) != original) throw new IOException("配置已在其他窗口更新，请重试");
            var backup = path + ".switchboard-backup";
            if (File.Exists(backup)) {
                File.Replace(temporary, path, null);
            } else {
                File.Replace(temporary, path, backup);
            }
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Indent(string json) {
        var result = new StringBuilder();
        bool quoted = false, escape = false;
        int depth = 0;
        for (int i = 0; i < json.Length; i++) {
            char c = json[i];
            if (quoted) {
                result.Append(c);
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; result.Append(c); }
            else if (c == '{' || c == '[') {
                result.Append(c);
                char close = c == '{' ? '}' : ']';
                if (i + 1 < json.Length && json[i + 1] == close) result.Append(json[++i]);
                else result.Append('\n').Append(' ', ++depth * 2);
            } else if (c == '}' || c == ']') {
                result.Append('\n').Append(' ', --depth * 2).Append(c);
            } else if (c == ',') result.Append(",\n").Append(' ', depth * 2);
            else if (c == ':') result.Append(": ");
            else result.Append(c);
        }
        return result.Append('\n').ToString();
    }

    internal static int Test(string path) {
        var initial = new Settings(path);
        string proxy = initial.ProxyAddress();
        var keys = new[] { "computer", "files", "secrets", "shell", "web", "telemetry", "memory" };
        foreach (string key in keys) {
            var before = new Settings(path);
            var saved = before.ReadState(key);
            if (!saved.HasValue) return 1;
            before.Change(key, !saved.Value);
            before.Save();
            var after = new Settings(path);
            if (after.ReadState(key) != !saved.Value || after.ProxyAddress() != proxy ||
                !Is(after.Root, "disableClaudeAiConnectors", true) || !Is(after.Root, "disableAllHooks", true) ||
                !Is(after.Root, "disableRemoteControl", true) || !File.Exists(path + ".switchboard-backup")) return 2;
            foreach (string other in keys.Where(x => x != key))
                if (initial.ReadState(other) != after.ReadState(other)) return 3;
            if (key == "computer") {
                var otherTools = new[] { "mcp__ccd_session__read", "mcp__ide__exec", "mcp__claude-in-chrome__computer", "mcp__computer-use-extra__click" };
                if (otherTools.Any(t => !after.deny.Any(d => Glob(d, t))) ||
                    after.deny.Any(d => Glob(d, "mcp__computer-use__click"))) return 4;
            }
            after.Change(key, saved.Value);
            after.Save();
        }
        return 0;
    }

    private static bool Glob(string pattern, string value) {
        if (pattern.EndsWith("*")) return value.StartsWith(pattern.TrimEnd('*'), StringComparison.Ordinal);
        return pattern == value;
    }
}

internal sealed class MainWindow : Form {
    private static readonly Color Background = Color.FromArgb(246, 247, 244);
    private static readonly Color White = Color.FromArgb(255, 255, 253);
    private static readonly Color Ink = Color.FromArgb(22, 31, 28);
    private static readonly Color Muted = Color.FromArgb(80, 96, 88);
    private static readonly Color Accent = Color.FromArgb(35, 102, 77);
    private static readonly Color Border = Color.FromArgb(222, 228, 221);
    private static readonly Color Amber = Color.FromArgb(154, 77, 35);
    private readonly string path;
    private readonly Dictionary<string, Toggle> toggles = new Dictionary<string, Toggle>();
    private readonly Label connection;
    private readonly Label firewall;
    private readonly Label footer;
    private readonly Panel card;
    private readonly Timer refresh;
    private string snapshotProblem;
    private string notice = "改动后，请新建一个 Code 会话。";

    internal MainWindow(string path) {
        this.path = path;
        Text = "Claude 权限";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(454, 627);
        BackColor = Background;
        ForeColor = Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        AddText(this, "Claude 权限", 29, 22, 398, 42, 24, true, Ink);
        connection = AddText(this, "", 29, 74, 398, 30, 10, true, Accent);
        firewall = AddText(this, "", 29, 104, 398, 20, 8.5f, false, Muted);
        card = new Panel { Location = new Point(23, 139), Size = new Size(408, 421), BackColor = White };
        card.Paint += (sender, e) => {
            using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        Controls.Add(card);

        AddRow(0, "computer", "放行电脑操作工具", "在 Claude 内启用电脑操作并按应用授权");
        AddRow(1, "files", "限制项目外读写", "仅限选中的工作目录及手动添加的目录");
        AddRow(2, "secrets", "保护凭据文件", "屏蔽 .env、.env.* 和 .npmrc");
        AddRow(3, "shell", "禁止运行命令", "PowerShell、Bash 和后台监控");
        AddRow(4, "web", "禁止网页工具", "屏蔽内置网页搜索与读取");
        AddRow(5, "telemetry", "关闭非必要遥测", "减少使用统计、错误报告和反馈请求");
        AddRow(6, "memory", "关闭自动记忆", "关闭 Code 的自动记忆读写");
        footer = AddText(this, notice, 29, 575, 398, 34, 9, false, Muted);
        AccessibleDescription = "管理 Claude Code 的本地权限";
        refresh = new Timer { Interval = 2500 };
        refresh.Tick += (sender, e) => Reload();
        Shown += (sender, e) => { Reload(); refresh.Start(); };
        Activated += (sender, e) => Reload();
        FormClosed += (sender, e) => refresh.Dispose();
    }

    private static Label AddText(Control parent, string text, int x, int y, int width, int height,
        float size, bool bold, Color color) {
        var label = new Label { Location = new Point(x, y), Size = new Size(width, height),
            Text = text, ForeColor = color, AutoEllipsis = true,
            Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft };
        parent.Controls.Add(label);
        return label;
    }

    private void AddRow(int index, string key, string name, string details) {
        int top = 1 + index * 60;
        if (index > 0) card.Controls.Add(new Panel {
            Location = new Point(17, top - 1), Size = new Size(374, 1), BackColor = Border
        });
        AddText(card, name, 19, top + 7, 300, 24, 11, true, Ink);
        AddText(card, details, 19, top + 30, 305, 21, 8.5f, false, Muted);
        var toggle = new Toggle { AccessibleName = name, Location = new Point(337, top + 13), TabIndex = index };
        toggle.Click += (sender, e) => Set(key, toggle.State != true);
        toggles.Add(key, toggle);
        card.Controls.Add(toggle);
    }

    private void Reload() {
        try {
            var settings = new Settings(path);
            foreach (var row in toggles) {
                row.Value.Enabled = true;
                row.Value.State = settings.ReadState(row.Key);
            }
            var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(address => address.Port == 7897 && (address.Address.Equals(IPAddress.Loopback) || address.Address.Equals(IPAddress.Any)));
            bool correctProxy = settings.ProxyAddress() == "http://127.0.0.1:7897";
            connection.Text = !correctProxy ? "●  Code 的代理地址已变更"
                : listening ? "●  Clash 本机端口已监听 · 127.0.0.1:7897"
                : "●  Clash 本机端口未监听 · 127.0.0.1:7897";
            connection.ForeColor = listening && correctProxy ? Accent : Amber;
            CheckFirewall();
            snapshotProblem = null;
        } catch (Exception ex) {
            foreach (var row in toggles.Values) {
                row.State = null;
                row.Enabled = false;
            }
            snapshotProblem = ex.Message;
            connection.Text = "●  无法读取当前配置";
            connection.ForeColor = Amber;
            firewall.Text = "";
        }
        footer.Text = snapshotProblem ?? notice;
        footer.ForeColor = snapshotProblem != null ? Amber : Muted;
    }

    private static object Property(object target, string name, params object[] args) {
        return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);
    }

    private void CheckFirewall() {
        try {
            object policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
            int profiles = (int)Property(policy, "CurrentProfileTypes");
            bool active = profiles != 0 && new[] { 1, 2, 4 }.Where(profile => (profiles & profile) != 0)
                .All(profile => (bool)Property(policy, "FirewallEnabled", profile));
            int blocks = 0;
            foreach (object rule in (System.Collections.IEnumerable)Property(policy, "Rules")) {
                if ((string)Property(rule, "Grouping") == "Claude proxy-only outbound" &&
                    (bool)Property(rule, "Enabled") && (int)Property(rule, "Direction") == 2 &&
                    (int)Property(rule, "Action") == 0) blocks++;
            }
            firewall.Text = active && blocks > 0 ? "断线保护 · 已检测到 Windows 出站规则"
                : "断线保护 · 未检测到有效的 Windows 出站规则";
            firewall.ForeColor = active && blocks > 0 ? Muted : Amber;
        } catch {
            firewall.Text = "断线保护 · 无法读取规则状态";
            firewall.ForeColor = Amber;
        }
    }

    private void Set(string key, bool value) {
        try {
            var settings = new Settings(path);
            if (key == "shell" && !value &&
                MessageBox.Show(this, "允许命令后，命令及其子进程会以你的 Windows 用户身份运行。要继续吗？",
                    "允许 Claude 运行命令", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            settings.Change(key, value);
            settings.Save();
            notice = key == "computer" && value
                ? "已保存。请在 Claude 启用电脑操作，然后新建 Code 会话。"
                : "已保存。请新建一个 Code 会话。";
            Reload();
        } catch (Exception ex) {
            Reload();
            MessageBox.Show(this, ex.Message, "没有保存改动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed class Toggle : Button {
        private bool? state;
        internal bool? State {
            get { return state; }
            set {
                if (state == value) return;
                state = value;
                AccessibleDescription = value == true ? "开" : value == false ? "关" : "部分开启";
                Invalidate();
            }
        }

        internal Toggle() {
            Size = new Size(57, 33);
            BackColor = White;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        protected override AccessibleObject CreateAccessibilityInstance() {
            return new SwitchAccessibility(this);
        }

        private sealed class SwitchAccessibility : ControlAccessibleObject {
            private readonly Toggle toggle;
            internal SwitchAccessibility(Toggle toggle) : base(toggle) { this.toggle = toggle; }
            public override string DefaultAction { get { return "切换"; } }
            public override void DoDefaultAction() { toggle.PerformClick(); }
            public override AccessibleStates State {
                get {
                    var states = base.State;
                    if (toggle.State == true) states |= AccessibleStates.Checked;
                    if (!toggle.State.HasValue) states |= AccessibleStates.Mixed;
                    return states;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(White);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int x = 3, y = (Height - 24) / 2, w = Width - 6, h = 24;
            using (var path = new GraphicsPath()) {
                path.AddArc(x, y, h, h, 90, 180);
                path.AddArc(x + w - h, y, h, h, 270, 180);
                path.CloseFigure();
                var fill = !Enabled ? Border : State == true ? Accent :
                    State == false ? Color.FromArgb(208, 219, 210) : Color.FromArgb(222, 204, 179);
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            }
            int knobX = State == true ? x + w - 20 : State == false ? x + 4 : x + (w - 16) / 2;
            using (var brush = new SolidBrush(White)) e.Graphics.FillEllipse(brush, knobX, y + 4, 16, 16);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        }
    }
}
