using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

/*
 File: MainForm.cs
 Responsibility:
   - WinForms UI for configuring and running the proxy.
   - Provides inputs for Login/Game ports, remote server address, and handler selection.
   - Displays real-time connection status (client and remote) for Login and Game via icons.
   - Starts/stops the proxy and marshals event callbacks to the UI thread.
   - Provides a packet visualization workbench to assist reverse-engineering:
       * Live packet list: time, direction, server type, type id, length, tag, summary.
       * Detail pane: hex dump and parsed fields.
       * Simple filters: direction, server type, tag text filter.
   - Uses a TabControl to keep the UI maintainable and logically separated.
*/

namespace CoProxyApp
{
    public partial class MainForm : Form
    {
        private List<IConquerProtocolHandler> handlers;
        private ConquerProxyLimitedClients? proxy;

        // Configuration controls
        private TextBox LoginPortEntry = new TextBox() { Text = "9958", Width = 120 };
        private TextBox GamePortEntry = new TextBox() { Text = "5816", Width = 120 };
        private TextBox RemoteServerAddressEntry = new TextBox() { Text = "127.0.0.1", Width = 180 };
        private ComboBox HandlerPicker = new ComboBox() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        private Button StartProxyButton = new Button() { Text = "Start Proxy", Width = 120 };
        private Button StopProxyButton = new Button() { Text = "Stop Proxy", Width = 120 };
        private Label StatusLabel = new Label() { AutoSize = true, ForeColor = Color.Green, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };

        // Status indicator icons for connections
        private PictureBox LoginClientStatusIcon = new PictureBox() { Size = new Size(16, 16) };
        private PictureBox LoginServerStatusIcon = new PictureBox() { Size = new Size(16, 16) };
        private PictureBox GameClientStatusIcon = new PictureBox() { Size = new Size(16, 16) };
        private PictureBox GameServerStatusIcon = new PictureBox() { Size = new Size(16, 16) };

        private Dictionary<string, int> activeClientCounts = new Dictionary<string, int> { { "Login", 0 }, { "Game", 0 } };

        private Bitmap greenCircle;
        private Bitmap redCircle;

        // Packet visualization data and controls
        private BindingList<PacketInfo> packetBinding = new BindingList<PacketInfo>();
        private ListView packetListView = new ListView();
        private TextBox packetHexDump = new TextBox();
        private ListView packetFieldsView = new ListView();
        private ComboBox filterDirection = new ComboBox();
        private ComboBox filterServerType = new ComboBox();
        private TextBox filterTagText = new TextBox();
        private Button clearPacketsButton = new Button() { Text = "Clear" };
        private Button exportPacketsButton = new Button() { Text = "Export..." };

        public MainForm()
        {
            InitializeComponent();
            InitializeControls();
            InitializePacketTab();

            handlers = new List<IConquerProtocolHandler>
            {
                new ConquerClassicLordsHandler()
            };

            foreach (var handler in handlers)
            {
                HandlerPicker.Items.Add(handler.GetType().Name);
            }
            if (HandlerPicker.Items.Count > 0)
                HandlerPicker.SelectedIndex = 0;

            UpdateStatusIcons(false, false, false, false);
        }

        private void InitializeComponent()
        {
            this.Text = "Conquer Proxy Workbench";
            this.Size = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
        }

        private void InitializeControls()
        {
            // Build a TabControl with two tabs: Proxy and Packets
            var tabs = new TabControl() { Dock = DockStyle.Fill };
            var proxyTab = new TabPage("Proxy");
            var packetsTab = new TabPage("Packets");

            tabs.TabPages.Add(proxyTab);
            tabs.TabPages.Add(packetsTab);

            this.Controls.Add(tabs);

            // Proxy tab layout using TableLayoutPanel
            var tlp = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 6,
                AutoSize = true
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < tlp.RowCount; i++)
                tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Labels
            var loginLabel = new Label() { Text = "Login Port:", AutoSize = true };
            var gameLabel = new Label() { Text = "Game Port:", AutoSize = true };
            var remoteServerLabel = new Label() { Text = "Remote Server IP:", AutoSize = true };
            var handlerLabel = new Label() { Text = "Select Handler:", AutoSize = true };

            StartProxyButton.Click += OnStartProxyClicked;
            StopProxyButton.Click += OnStopProxyClicked;
            StopProxyButton.Enabled = false;

            // Status panel with icons and descriptors
            var statusPanel = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            statusPanel.Controls.Add(CreateStatusBlock("Login Client", LoginClientStatusIcon));
            statusPanel.Controls.Add(CreateStatusBlock("Login Server", LoginServerStatusIcon));
            statusPanel.Controls.Add(CreateStatusBlock("Game Client", GameClientStatusIcon));
            statusPanel.Controls.Add(CreateStatusBlock("Game Server", GameServerStatusIcon));

            // Add to layout
            tlp.Controls.Add(loginLabel, 0, 0);
            tlp.Controls.Add(LoginPortEntry, 1, 0);
            tlp.Controls.Add(gameLabel, 0, 1);
            tlp.Controls.Add(GamePortEntry, 1, 1);
            tlp.Controls.Add(remoteServerLabel, 0, 2);
            tlp.Controls.Add(RemoteServerAddressEntry, 1, 2);
            tlp.Controls.Add(handlerLabel, 0, 3);
            tlp.Controls.Add(HandlerPicker, 1, 3);
            tlp.Controls.Add(StartProxyButton, 0, 4);
            tlp.Controls.Add(StopProxyButton, 1, 4);
            tlp.Controls.Add(StatusLabel, 0, 5);
            tlp.SetColumnSpan(StatusLabel, 4);
            tlp.Controls.Add(statusPanel, 2, 0);
            tlp.SetRowSpan(statusPanel, 4);

            proxyTab.Controls.Add(tlp);

            // Create green/red circles
            greenCircle = CreateCircleBitmap(Color.Green, 16);
            redCircle = CreateCircleBitmap(Color.Red, 16);
            SetStatusIcon(LoginClientStatusIcon, false);
            SetStatusIcon(LoginServerStatusIcon, false);
            SetStatusIcon(GameClientStatusIcon, false);
            SetStatusIcon(GameServerStatusIcon, false);

            // Prepare Packets tab (assigned later)
            PacketsTab = packetsTab;
        }

        // Keep a reference to the Packets tab to add content later
        private TabPage? PacketsTab;

        private Control CreateStatusBlock(string title, PictureBox icon)
        {
            var panel = new FlowLayoutPanel()
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Margin = new Padding(10, 5, 10, 5)
            };
            var label = new Label() { Text = title, AutoSize = true, Margin = new Padding(0, 0, 5, 0) };
            panel.Controls.Add(label);
            panel.Controls.Add(icon);
            return panel;
        }

        private void InitializePacketTab()
        {
            if (PacketsTab == null) return;

            // Filters bar
            var filtersPanel = new FlowLayoutPanel()
            {
                Dock = DockStyle.Top,
                Height = 32,
                FlowDirection = FlowDirection.LeftToRight
            };

            filterDirection.DropDownStyle = ComboBoxStyle.DropDownList;
            filterDirection.Items.AddRange(new object[] { "All", "ClientToServer", "ServerToClient" });
            filterDirection.SelectedIndex = 0;
            filterDirection.SelectedIndexChanged += (_, __) => RefreshPacketList();

            filterServerType.DropDownStyle = ComboBoxStyle.DropDownList;
            filterServerType.Items.AddRange(new object[] { "All", "Login", "Game" });
            filterServerType.SelectedIndex = 0;
            filterServerType.SelectedIndexChanged += (_, __) => RefreshPacketList();

            filterTagText.Width = 160;
            filterTagText.PlaceholderText = "Tag filter (contains)";
            filterTagText.TextChanged += (_, __) => RefreshPacketList();

            clearPacketsButton.Click += (_, __) =>
            {
                packetBinding.Clear();
                packetListView.Items.Clear();
                packetHexDump.Clear();
                packetFieldsView.Items.Clear();
            };

            exportPacketsButton.Click += OnExportPackets;

            filtersPanel.Controls.Add(new Label() { Text = "Direction:", AutoSize = true, Padding = new Padding(4, 8, 4, 0) });
            filtersPanel.Controls.Add(filterDirection);
            filtersPanel.Controls.Add(new Label() { Text = "Server:", AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
            filtersPanel.Controls.Add(filterServerType);
            filtersPanel.Controls.Add(new Label() { Text = "Tag:", AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
            filtersPanel.Controls.Add(filterTagText);
            filtersPanel.Controls.Add(clearPacketsButton);
            filtersPanel.Controls.Add(exportPacketsButton);

            // Split container: top list, bottom details
            var split = new SplitContainer()
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 300
            };

            // Packet list
            packetListView.View = View.Details;
            packetListView.FullRowSelect = true;
            packetListView.GridLines = true;
            packetListView.HideSelection = false;
            packetListView.Columns.Add("Time", 140);
            packetListView.Columns.Add("Dir", 90);
            packetListView.Columns.Add("Server", 70);
            packetListView.Columns.Add("Type", 80);
            packetListView.Columns.Add("Length", 70);
            packetListView.Columns.Add("Tag", 140);
            packetListView.Columns.Add("Info", 400);
            packetListView.Dock = DockStyle.Fill;
            packetListView.SelectedIndexChanged += OnPacketSelected;

            // Detail area with two panels: hex dump and fields
            var detailsSplit = new SplitContainer()
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 600
            };

            packetHexDump.Multiline = true;
            packetHexDump.ScrollBars = ScrollBars.Both;
            packetHexDump.ReadOnly = true;
            packetHexDump.Font = new Font(FontFamily.GenericMonospace, 9);
            packetHexDump.Dock = DockStyle.Fill;

            packetFieldsView.View = View.Details;
            packetFieldsView.FullRowSelect = true;
            packetFieldsView.GridLines = true;
            packetFieldsView.Columns.Add("Field", 160);
            packetFieldsView.Columns.Add("Value", 300);
            packetFieldsView.Dock = DockStyle.Fill;

            detailsSplit.Panel1.Controls.Add(packetHexDump);
            detailsSplit.Panel2.Controls.Add(packetFieldsView);

            split.Panel1.Controls.Add(packetListView);
            split.Panel2.Controls.Add(detailsSplit);

            var container = new Panel() { Dock = DockStyle.Fill };
            container.Controls.Add(split);
            container.Controls.Add(filtersPanel);

            PacketsTab.Controls.Add(container);
        }

        private Bitmap CreateCircleBitmap(Color color, int diameter)
        {
            Bitmap bmp = new Bitmap(diameter, diameter);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(color))
                {
                    g.FillEllipse(b, 0, 0, diameter - 1, diameter - 1);
                }
            }
            return bmp;
        }

        private void SetStatusIcon(PictureBox pb, bool state)
        {
            pb.Image = state ? greenCircle : redCircle;
        }

        private void UpdateStatusIcons(bool loginClient, bool loginServer, bool gameClient, bool gameServer)
        {
            SetStatusIcon(LoginClientStatusIcon, loginClient);
            SetStatusIcon(LoginServerStatusIcon, loginServer);
            SetStatusIcon(GameClientStatusIcon, gameClient);
            SetStatusIcon(GameServerStatusIcon, gameServer);
        }

        private void OnStartProxyClicked(object? sender, EventArgs e)
        {
            if (!int.TryParse(LoginPortEntry.Text, out int loginPort) ||
                !int.TryParse(GamePortEntry.Text, out int gamePort))
            {
                StatusLabel.ForeColor = Color.Red;
                StatusLabel.Text = "Invalid port numbers.";
                return;
            }

            if (string.IsNullOrWhiteSpace(RemoteServerAddressEntry.Text))
            {
                StatusLabel.ForeColor = Color.Red;
                StatusLabel.Text = "Remote Server IP cannot be empty.";
                return;
            }

            if (HandlerPicker.SelectedIndex == -1)
            {
                StatusLabel.ForeColor = Color.Red;
                StatusLabel.Text = "Please select a handler.";
                return;
            }

            var ports = new Dictionary<string, int> { { "Login", loginPort }, { "Game", gamePort } };
            var selectedHandler = handlers[HandlerPicker.SelectedIndex];

            activeClientCounts["Login"] = 0;
            activeClientCounts["Game"] = 0;

            proxy = new ConquerProxyLimitedClients(ports, selectedHandler, RemoteServerAddressEntry.Text, activeClientCounts);

            proxy.OnClientConnected += (serverType, connected) =>
            {
                this.BeginInvoke(() =>
                {
                    switch (serverType)
                    {
                        case "Login":
                            UpdateStatusIcons(connected,
                                LoginServerStatusIcon.Image == greenCircle,
                                GameClientStatusIcon.Image == greenCircle,
                                GameServerStatusIcon.Image == greenCircle);
                            break;
                        case "Game":
                            UpdateStatusIcons(
                                LoginClientStatusIcon.Image == greenCircle,
                                LoginServerStatusIcon.Image == greenCircle,
                                connected,
                                GameServerStatusIcon.Image == greenCircle);
                            break;
                    }
                });
            };

            proxy.OnRemoteConnected += (serverType, connected) =>
            {
                this.BeginInvoke(() =>
                {
                    switch (serverType)
                    {
                        case "Login":
                            UpdateStatusIcons(LoginClientStatusIcon.Image == greenCircle,
                                connected,
                                GameClientStatusIcon.Image == greenCircle,
                                GameServerStatusIcon.Image == greenCircle);
                            break;
                        case "Game":
                            UpdateStatusIcons(
                                LoginClientStatusIcon.Image == greenCircle,
                                LoginServerStatusIcon.Image == greenCircle,
                                GameClientStatusIcon.Image == greenCircle,
                                connected);
                            break;
                    }
                });
            };

            proxy.OnPacketCaptured += info =>
            {
                // UI thread marshal
                this.BeginInvoke(() => AddPacketToList(info));
            };

            proxy.Start();

            StartProxyButton.Enabled = false;
            StopProxyButton.Enabled = true;

            StatusLabel.ForeColor = Color.Green;
            StatusLabel.Text = $"Proxy started on Login:{loginPort}, Game:{gamePort} relaying to {RemoteServerAddressEntry.Text}";
        }

        private void OnStopProxyClicked(object? sender, EventArgs e)
        {
            if (proxy is null)
            {
                return;
            }
            proxy.Stop();
            StartProxyButton.Enabled = true;
            StopProxyButton.Enabled = false;

            UpdateStatusIcons(false, false, false, false);

            StatusLabel.ForeColor = Color.Red;
            StatusLabel.Text = "Proxy stopped!";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            if (proxy != null)
            {
                proxy.Stop();
            }
        }

        // ----- Packet visualization helpers -----

        private void AddPacketToList(PacketInfo info)
        {
            packetBinding.Add(info);

            if (!PassesFilter(info))
                return;

            var item = new ListViewItem(new[]
            {
                info.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
                info.Direction.ToString(),
                info.ServerType,
                $"0x{info.Type:X4}",
                info.DeclaredLength.ToString(),
                info.Tag,
                info.Description
            })
            {
                Tag = info
            };

            // Color by direction
            if (info.Direction == PacketDirection.ClientToServer)
            {
                item.ForeColor = Color.DarkBlue;
            }
            else
            {
                item.ForeColor = Color.DarkGreen;
            }

            packetListView.Items.Add(item);
            // Keep newest visible
            item.EnsureVisible();
        }

        private bool PassesFilter(PacketInfo info)
        {
            if (filterDirection.SelectedIndex > 0)
            {
                var wantDir = (filterDirection.SelectedItem?.ToString() ?? "All");
                if (!string.Equals(info.Direction.ToString(), wantDir, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (filterServerType.SelectedIndex > 0)
            {
                var wantServer = (filterServerType.SelectedItem?.ToString() ?? "All");
                if (!string.Equals(info.ServerType, wantServer, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var tagFilter = filterTagText.Text?.Trim();
            if (!string.IsNullOrEmpty(tagFilter))
            {
                if (info.Tag?.IndexOf(tagFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    info.Description?.IndexOf(tagFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        private void RefreshPacketList()
        {
            packetListView.BeginUpdate();
            try
            {
                packetListView.Items.Clear();
                foreach (var info in packetBinding.Where(PassesFilter))
                {
                    var item = new ListViewItem(new[]
                    {
                        info.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
                        info.Direction.ToString(),
                        info.ServerType,
                        $"0x{info.Type:X4}",
                        info.DeclaredLength.ToString(),
                        info.Tag,
                        info.Description
                    })
                    {
                        Tag = info,
                        ForeColor = info.Direction == PacketDirection.ClientToServer ? Color.DarkBlue : Color.DarkGreen
                    };
                    packetListView.Items.Add(item);
                }
            }
            finally
            {
                packetListView.EndUpdate();
            }
        }

        private void OnPacketSelected(object? sender, EventArgs e)
        {
            if (packetListView.SelectedItems.Count == 0)
            {
                packetHexDump.Clear();
                packetFieldsView.Items.Clear();
                return;
            }

            var info = packetListView.SelectedItems[0].Tag as PacketInfo;
            if (info == null) return;

            // Hex dump (header+payload)
            packetHexDump.Text = FormatHexDump(info.RawFrame);

            // Fields
            packetFieldsView.BeginUpdate();
            try
            {
                packetFieldsView.Items.Clear();

                packetFieldsView.Items.Add(new ListViewItem(new[] { "ConnectionId", info.ConnectionId.ToString() }));
                packetFieldsView.Items.Add(new ListViewItem(new[] { "ServerType", info.ServerType }));
                packetFieldsView.Items.Add(new ListViewItem(new[] { "Direction", info.Direction.ToString() }));
                packetFieldsView.Items.Add(new ListViewItem(new[] { "Type", $"0x{info.Type:X4}" }));
                packetFieldsView.Items.Add(new ListViewItem(new[] { "DeclaredLength", info.DeclaredLength.ToString() }));
                packetFieldsView.Items.Add(new ListViewItem(new[] { "Tag", info.Tag }));
                packetFieldsView.Items.Add(new ListViewItem(new[] { "Description", info.Description }));

                foreach (var kv in info.Fields)
                {
                    packetFieldsView.Items.Add(new ListViewItem(new[] { kv.Key, kv.Value?.ToString() ?? "" }));
                }
            }
            finally
            {
                packetFieldsView.EndUpdate();
            }
        }

        private void OnExportPackets(object? sender, EventArgs e)
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "JSON Lines (*.jsonl)|*.jsonl|CSV (*.csv)|*.csv|Text (*.txt)|*.txt",
                FileName = $"packets_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var path = sfd.FileName;
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".csv")
                {
                    using var w = new System.IO.StreamWriter(path, false, Encoding.UTF8);
                    w.WriteLine("Time,Direction,Server,Type,Length,Tag,Info");
                    foreach (var p in packetBinding)
                    {
                        w.WriteLine($"{p.TimestampUtc:o},{p.Direction},{p.ServerType},0x{p.Type:X4},{p.DeclaredLength},\"{p.Tag}\",\"{p.Description}\"");
                    }
                }
                else if (ext == ".jsonl")
                {
                    using var w = new System.IO.StreamWriter(path, false, Encoding.UTF8);
                    foreach (var p in packetBinding)
                    {
                        var obj = new
                        {
                            time = p.TimestampUtc,
                            direction = p.Direction.ToString(),
                            server = p.ServerType,
                            type = p.Type,
                            length = p.DeclaredLength,
                            tag = p.Tag,
                            description = p.Description,
                            fields = p.Fields,
                            raw = Convert.ToBase64String(p.RawFrame)
                        };
                        string json = System.Text.Json.JsonSerializer.Serialize(obj);
                        w.WriteLine(json);
                    }
                }
                else
                {
                    using var w = new System.IO.StreamWriter(path, false, Encoding.UTF8);
                    foreach (var p in packetBinding)
                    {
                        w.WriteLine($"{p.TimestampUtc:o} {p.Direction} {p.ServerType} Type=0x{p.Type:X4} Len={p.DeclaredLength} Tag={p.Tag} {p.Description}");
                    }
                }

                MessageBox.Show(this, "Export completed.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Export failed: {ex.Message}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --- Utility hex dump ---
        private static string FormatHexDump(byte[] data, int bytesPerLine = 16)
        {
            if (data == null || data.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < data.Length; i += bytesPerLine)
            {
                int count = Math.Min(bytesPerLine, data.Length - i);
                var slice = new Span<byte>(data, i, count);

                // Offset
                sb.Append(i.ToString("X4"));
                sb.Append("  ");

                // Hex bytes
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (j < count)
                        sb.Append(slice[j].ToString("X2"));
                    else
                        sb.Append("  ");
                    sb.Append(' ');
                    if (j == 7) sb.Append(' ');
                }

                sb.Append(" | ");

                // ASCII
                for (int j = 0; j < count; j++)
                {
                    byte b = slice[j];
                    sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
