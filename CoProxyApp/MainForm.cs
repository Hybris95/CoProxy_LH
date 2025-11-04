/*
 File: MainForm.cs
 Responsibility:
   - WinForms UI for configuring and running the proxy.
   - Provides inputs for Login/Game ports, remote server address, and handler selection.
   - Displays real-time connection status (client and remote) for Login and Game via icons.
   - Starts/stops the proxy and marshals event callbacks to the UI thread.

 UI/UX:
   - Start/Stop buttons manage proxy lifecycle.
   - Green/Red circular icons reflect connected/disconnected statuses per side/type.

 Threading:
   - Event handlers from proxy are invoked on background threads and marshaled via Control.Invoke.
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CoProxyApp
{
    /// <summary>
    /// Main WinForms window to configure and control the Conquer proxy.
    /// </summary>
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
        private Label StatusLabel = new Label() { Location = new Point(10, 260), Width = 420, ForeColor = Color.Green, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };

        // Status indicator icons for connections
        private PictureBox LoginClientStatusIcon = new PictureBox() { Location = new Point(340, 20), Size = new Size(16, 16) };
        private PictureBox LoginServerStatusIcon = new PictureBox() { Location = new Point(340, 50), Size = new Size(16, 16) };
        private PictureBox GameClientStatusIcon = new PictureBox() { Location = new Point(340, 90), Size = new Size(16, 16) };
        private PictureBox GameServerStatusIcon = new PictureBox() { Location = new Point(340, 120), Size = new Size(16, 16) };

        // Fixed description labels of the statuses
        private Label LoginClientLabelDesc = new Label() { Text = "Login Client", Location = new Point(260, 20), AutoSize = true };
        private Label LoginServerLabelDesc = new Label() { Text = "Login Server", Location = new Point(260, 50), AutoSize = true };
        private Label GameClientLabelDesc = new Label() { Text = "Game Client", Location = new Point(260, 90), AutoSize = true };
        private Label GameServerLabelDesc = new Label() { Text = "Game Server", Location = new Point(260, 120), AutoSize = true };

        private Dictionary<string, int> activeClientCounts = new Dictionary<string, int> { { "Login", 0 }, { "Game", 0 } };

        private Bitmap greenCircle;
        private Bitmap redCircle;

        /// <summary>
        /// Initializes the form, loads available handlers, and sets initial UI state.
        /// </summary>
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

            UpdateStatusIcons(false, false, false, false);
        }

        /// <summary>
        /// Creates and configures all controls, wires event handlers, and initializes status icons.
        /// </summary>
        private void InitializeControls()
        {
            this.Text = "Conquer Proxy Configuration";
            this.Size = new Size(450, 330);

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

            // Add status label descriptions
            this.Controls.Add(LoginClientLabelDesc);
            this.Controls.Add(LoginServerLabelDesc);
            this.Controls.Add(GameClientLabelDesc);
            this.Controls.Add(GameServerLabelDesc);

            // Add status icons
            this.Controls.Add(LoginClientStatusIcon);
            this.Controls.Add(LoginServerStatusIcon);
            this.Controls.Add(GameClientStatusIcon);
            this.Controls.Add(GameServerStatusIcon);

            // Create green/red circle bitmaps
            greenCircle = CreateCircleBitmap(Color.Green, 16);
            redCircle = CreateCircleBitmap(Color.Red, 16);

            // Initialize all status indicators to red (inactive)
            SetStatusIcon(LoginClientStatusIcon, false);
            SetStatusIcon(LoginServerStatusIcon, false);
            SetStatusIcon(GameClientStatusIcon, false);
            SetStatusIcon(GameServerStatusIcon, false);
        }

        /// <summary>
        /// Helper to create a filled circular bitmap with the given color and size.
        /// </summary>
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

        /// <summary>
        /// Sets the image of a PictureBox to red or green based on state.
        /// </summary>
        private void SetStatusIcon(PictureBox pb, bool state)
        {
            pb.Image = state ? greenCircle : redCircle;
        }

        /// <summary>
        /// Updates all status icons in one call.
        /// </summary>
        private void UpdateStatusIcons(bool loginClient, bool loginServer, bool gameClient, bool gameServer)
        {
            SetStatusIcon(LoginClientStatusIcon, loginClient);
            SetStatusIcon(LoginServerStatusIcon, loginServer);
            SetStatusIcon(GameClientStatusIcon, gameClient);
            SetStatusIcon(GameServerStatusIcon, gameServer);
        }

        /// <summary>
        /// Starts the proxy using input values; validates inputs and wires proxy events to UI updates.
        /// </summary>
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
                this.Invoke(() =>
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
                this.Invoke(() =>
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

            proxy.Start();

            StartProxyButton.Enabled = false;
            StopProxyButton.Enabled = true;

            StatusLabel.ForeColor = Color.Green;
            StatusLabel.Text = $"Proxy started on Login:{loginPort}, Game:{gamePort} relaying to {RemoteServerAddressEntry.Text}";
        }

        /// <summary>
        /// Stops the proxy and resets UI indicators.
        /// </summary>
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

        /// <summary>
        /// Ensures the proxy is stopped when the form is closing.
        /// </summary>
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
