using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;

namespace ChecadorLector
{
    // La ventana aloja la pantalla web (carpeta ui). Toda la lógica va en C#, no en la página:
    // C# le manda mensajes a la pantalla y la pantalla solo los dibuja.
    public class VentanaPrincipal : Form
    {
        private readonly bool _kiosco;
        private readonly WebView2 _web = new WebView2();
        private readonly System.Windows.Forms.Timer _timerEstado = new System.Windows.Forms.Timer { Interval = 30000 };

        private ApiCliente _api;
        private bool _iniciado;
        private bool _actualizando;
        private string _ultimaSucursal = "Sin conectar";

        // Diferencia entre el reloj del servidor y el de esta PC (se usará para sellar las checadas)
        public TimeSpan DesfaseReloj { get; private set; }

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

            _timerEstado.Tick += async (s, e) => await ActualizarEstadoAsync();
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

                // Cuando la pantalla termina de cargar, ya puede recibir mensajes
                web.NavigationCompleted += async (s, ev) => await IniciarEstadoAsync();
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timerEstado.Stop();
            if (_api != null) _api.Dispose();
            base.OnFormClosed(e);
        }

        private async Task IniciarEstadoAsync()
        {
            if (_iniciado) return;
            _iniciado = true;

            await ActualizarEstadoAsync();
            _timerEstado.Start();
        }

        // Pregunta a la API cómo está este lector y se lo cuenta a la pantalla
        private async Task ActualizarEstadoAsync()
        {
            if (_actualizando) return;
            _actualizando = true;
            try
            {
                if (_api == null)
                {
                    Configuracion config = Configuracion.Cargar();
                    string problema;
                    if (!config.EsValida(out problema))
                    {
                        Mostrar("Sin configurar", "Sin configurar", false, problema, "");
                        return;
                    }
                    _api = new ApiCliente(config);
                }

                ResultadoEstado r = await _api.EstadoAsync();
                DesfaseReloj = r.Desfase;
                _ultimaSucursal = r.Estado.Sucursal.Nombre;

                TimeSpan tolerancia = TimeSpan.FromMinutes(r.Estado.Configuracion.ToleranciaRelojMin);
                string aviso = "";
                if (r.Desfase.Duration() > tolerancia)
                {
                    aviso = "Reloj del equipo desfasado · avisa a sistemas";
                }
                else if (!r.Estado.Sucursal.Activa)
                {
                    aviso = "Sucursal inactiva · avisa a sistemas";
                }

                Mostrar(_ultimaSucursal, "En línea", true,
                    r.Estado.Lector.Nombre + " · API conectada", aviso);
            }
            catch (ErrorApi ex)
            {
                Mostrar(_ultimaSucursal, "Sin autorización", false, ex.Message, "");
            }
            catch (Exception)
            {
                Mostrar(_ultimaSucursal, "Sin conexión", false, "Sin conexión con la API", "");
            }
            finally
            {
                _actualizando = false;
            }
        }

        private void Mostrar(string sucursal, string red, bool enLinea, string info, string aviso)
        {
            Enviar(new { tipo = "estado", sucursal = sucursal, red = red, enLinea = enLinea, info = info, aviso = aviso });
        }

        private void Enviar(object mensaje)
        {
            if (_web.CoreWebView2 == null) return;
            _web.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(mensaje));
        }
    }
}