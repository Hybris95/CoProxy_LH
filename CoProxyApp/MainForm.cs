using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CoProxyApp
{
    public partial class MainForm : Form
    {
        private List<IConquerProtocolHandler> handlers;
        private ConquerProxyLimitedClients? proxy;

        private TextBox LoginPortEntry = new TextBox() { Text = "9958", Location = new Point(120, 20), Width = 200 };
        private TextBox GamePortEntry = new TextBox() { Text = "5816", Location = new Point(120, 60), Width = 200 };
        private TextBox RemoteServerAddressEntry = new TextBox() { Text = "127.0.0.1", Location = new Point(120, 100), Width = 200 };
        private ComboBox HandlerPicker = new ComboBox() { Location = new Point(120, 140), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        private Button StartProxyButton = new Button() { Text = "Start Proxy", Location = new Point(120, 180), Width = 200 };
        private Button StopProxyButton = new Button() { Text = "Stop Proxy", Location = new Point(120, 220), Width = 200 };
        private Label StatusLabel = new Label() { Location = new Point(10, 260), Width = 350, ForeColor = Color.Green, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };

        // Store client connection counts per server type for limiting to 1 client each
        private Dictionary<string, int> activeClientCounts = new Dictionary<string, int> { { "Login", 0 }, { "Game", 0 } };

        public MainForm()
        {
            InitializeControls();

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
        }

        private void InitializeControls()
        {
            this.Text = "Conquer Proxy Configuration";
            this.Size = new Size(400, 350);

            Label loginLabel = new Label() { Text = "Login Port:", Location = new Point(10, 20) };
            Label gameLabel = new Label() { Text = "Game Port:", Location = new Point(10, 60) };
            Label remoteServerLabel = new Label() { Text = "Remote Server IP:", Location = new Point(10, 100) };
            Label handlerLabel = new Label() { Text = "Select Handler:", Location = new Point(10, 140) };

            StartProxyButton.Click += OnStartProxyClicked;
            StopProxyButton.Click += OnStopProxyClicked;
            StopProxyButton.Enabled = false;

            this.Controls.Add(loginLabel);
            this.Controls.Add(LoginPortEntry);
            this.Controls.Add(gameLabel);
            this.Controls.Add(GamePortEntry);
            this.Controls.Add(remoteServerLabel);
            this.Controls.Add(RemoteServerAddressEntry);
            this.Controls.Add(handlerLabel);
            this.Controls.Add(HandlerPicker);
            this.Controls.Add(StartProxyButton);
            this.Controls.Add(StopProxyButton);
            this.Controls.Add(StatusLabel);
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

            // Reset active client counts on new proxy start
            activeClientCounts["Login"] = 0;
            activeClientCounts["Game"] = 0;

            proxy = new ConquerProxyLimitedClients(ports, selectedHandler, RemoteServerAddressEntry.Text, activeClientCounts);
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

            StatusLabel.ForeColor = Color.Red;
            StatusLabel.Text = $"Proxy stopped!";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            if (proxy != null)
            {
                proxy.Stop();
            }
        }
    }
}
