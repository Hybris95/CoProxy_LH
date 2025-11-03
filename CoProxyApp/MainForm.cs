using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CoProxyApp
{
    public partial class MainForm : Form
    {
        private List<IConquerProtocolHandler> handlers;
        private ConquerProxy? proxy;

        private TextBox LoginPortEntry = new TextBox() { Text = "9958", Location = new Point(120, 20), Width = 200 };
        private TextBox GamePortEntry = new TextBox() { Text = "5816", Location = new Point(120, 60), Width = 200 };
        private ComboBox HandlerPicker = new ComboBox() { Location = new Point(120, 100), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        private Button StartProxyButton = new Button() { Text = "Start Proxy", Location = new Point(120, 140), Width = 200 };
        private Label StatusLabel = new Label() { Location = new Point(10, 180), Width = 350, ForeColor = Color.Green, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };

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
            this.Size = new Size(400, 300);

            Label loginLabel = new Label() { Text = "Login Port:", Location = new Point(10, 20) };

            Label gameLabel = new Label() { Text = "Game Port:", Location = new Point(10, 60) };

            Label handlerLabel = new Label() { Text = "Select Handler:", Location = new Point(10, 100) };

            StartProxyButton.Click += OnStartProxyClicked;

            this.Controls.Add(loginLabel);
            this.Controls.Add(LoginPortEntry);
            this.Controls.Add(gameLabel);
            this.Controls.Add(GamePortEntry);
            this.Controls.Add(handlerLabel);
            this.Controls.Add(HandlerPicker);
            this.Controls.Add(StartProxyButton);
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

            if (HandlerPicker.SelectedIndex == -1)
            {
                StatusLabel.ForeColor = Color.Red;
                StatusLabel.Text = "Please select a handler.";
                return;
            }

            var ports = new Dictionary<string, int> { { "Login", loginPort }, { "Game", gamePort } };
            var selectedHandler = handlers[HandlerPicker.SelectedIndex];

            proxy = new ConquerProxy(ports, selectedHandler);
            proxy.Start();

            StatusLabel.ForeColor = Color.Green;
            StatusLabel.Text = $"Proxy started on Login:{loginPort}, Game:{gamePort}";
        }
    }
}
