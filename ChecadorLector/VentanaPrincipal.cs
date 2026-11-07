using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChecadorLector
{
    // La ventana solo aloja la pantalla web (carpeta ui). Toda la lógica va en C#, no en la página.
    public class VentanaPrincipal : Form
    {
        private readonly bool _kiosco;
        private readonly WebView2 _web = new WebView2();

        public VentanaPrincipal(bool kiosco)
        {
            _kiosco = kiosco;

            Text = "Checador";
            BackColor = Color.FromArgb(0xF4, 0xF6, 0xF8);

            if (_kiosco)
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
                TopMost = true;
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(1280, 720);
            }

            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                string carpetaUi = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");

                // Datos del navegador en una carpeta propia del usuario (no junto al .exe)
                string datos = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChecadorLector", "webview");

                CoreWebView2Environment entorno = await CoreWebView2Environment.CreateAsync(null, datos);
                await _web.EnsureCoreWebView2Async(entorno);

                CoreWebView2 web = _web.CoreWebView2;
                web.SetVirtualHostNameToFolderMapping(
                    "checador.local", carpetaUi, CoreWebView2HostResourceAccessKind.Deny);
                web.Settings.AreDevToolsEnabled = !_kiosco;
                web.Settings.AreDefaultContextMenusEnabled = false;
                web.Settings.IsZoomControlEnabled = false;
                web.Settings.IsStatusBarEnabled = false;
                web.Navigate("https://checador.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "No se pudo iniciar la pantalla del checador.\n\n" + ex.Message,
                    "Checador", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }
    }
}