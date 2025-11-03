using System;
using System.Collections.Generic;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace CoProxyApp
{
    public partial class MainPage : ContentPage
    {
        private List<IConquerProtocolHandler> handlers;
        private ConquerProxy? proxy;

        public MainPage()
        {
            InitializeComponent();

            // Initialize handlers list
            handlers = new List<IConquerProtocolHandler>
            {
                new ConquerClassicLordsHandler()
                // Add others here if needed
            };

            foreach(var handler in handlers)
            {
                HandlerPicker.Items.Add(handler.GetType().Name);
            }
            if (HandlerPicker.Items.Count > 0)
                HandlerPicker.SelectedIndex = 0;
        }

        private void OnStartProxyClicked(object sender, EventArgs e)
        {
            if (!int.TryParse(LoginPortEntry.Text, out int loginPort) || 
                !int.TryParse(GamePortEntry.Text, out int gamePort))
            {
                StatusLabel.TextColor = Colors.Red;
                StatusLabel.Text = "Invalid port numbers.";
                return;
            }

            if (HandlerPicker.SelectedIndex == -1)
            {
                StatusLabel.TextColor = Colors.Red;
                StatusLabel.Text = "Please select a handler.";
                return;
            }

            var ports = new Dictionary<string, int>
            {
                { "Login", loginPort },
                { "Game", gamePort }
            };
            var selectedHandler = handlers[HandlerPicker.SelectedIndex];

            proxy = new ConquerProxy(ports, selectedHandler);
            proxy.Start();

            StatusLabel.TextColor = Colors.Green;
            StatusLabel.Text = $"Proxy started on Login:{loginPort}, Game:{gamePort}";
        }
    }
}
