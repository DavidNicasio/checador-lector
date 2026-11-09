using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;

namespace ChecadorLector
{
    // La ventana aloja la pantalla web (carpeta ui). Toda la lógica va en C#, no en la página:
    // C# le manda mensajes a la pantalla y la pantalla solo los dibuja (y avisa lo que el usuario pide).
    public class VentanaPrincipal : Form
    {
        private readonly bool _kiosco;
        private readonly WebView2 _web = new WebView2();
        private readonly System.Windows.Forms.Timer _timerEstado = new System.Windows.Forms.Timer { Interval = 30000 };
        private readonly System.Windows.Forms.Timer _timerLector = new System.Windows.Forms.Timer { Interval = 5000 };

        // Se crea con la ventana, ANTES de abrir el lector (los objetos del SDK se crean una sola vez)
        private readonly RegistroHuella _registro = new RegistroHuella();

        private ApiCliente _api;
        private ApiAdmin _admin;
        private LectorHuellas _lector;
        private bool _iniciado;
        private bool _actualizando;
        private int _tokenRegistro;                // para ignorar un registro que se canceló mientras se preparaba
        private volatile bool _guardando;          // subiendo una huella: se ignoran capturas nuevas
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
            // Si el lector no estaba conectado al abrir el programa, se vuelve a intentar cada 5 s
            _timerLector.Tick += (s, e) => IntentarIniciarLector();
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

                // Lo que la pantalla le pide al programa (por ejemplo, entrar al modo administrador)
                web.WebMessageReceived += async (s, ev) => await AlRecibirMensajeAsync(ev.WebMessageAsJson);

                // Cuando la pantalla termina de cargar, ya puede recibir mensajes
                web.NavigationCompleted += async (s, ev) => await IniciarAsync();
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
            _timerLector.Stop();
            _registro.Cancelar();
            if (_lector != null) _lector.Dispose();
            if (_api != null) _api.Dispose();
            SalirAdmin();
            base.OnFormClosed(e);
        }

        private async Task IniciarAsync()
        {
            if (_iniciado) return;
            _iniciado = true;

            IntentarIniciarLector();
            _timerLector.Start();

            await ActualizarEstadoAsync();
            _timerEstado.Start();
        }

        // ---------------------------------------------------------------- Mensajes de la pantalla

        private async Task AlRecibirMensajeAsync(string json)
        {
            try
            {
                MensajeUi m = JsonConvert.DeserializeObject<MensajeUi>(json);
                if (m == null) return;

                switch (m.Accion)
                {
                    case "entrarAdmin":
                        await EntrarAdminAsync(m.Llave);
                        break;
                    case "salirAdmin":
                        SalirAdmin();
                        break;
                    case "iniciarRegistro":
                        await IniciarRegistroAsync(m.EmpleadoId, m.Dedo);
                        break;
                    case "cancelarRegistro":
                        CancelarRegistro();
                        break;
                }
            }
            catch (Exception)
            {
                // Un mensaje mal formado no debe tirar el programa
            }
        }

        // ---------------------------------------------------------------- Modo administrador

        private async Task EntrarAdminAsync(string llave)
        {
            SalirAdmin(); // limpia cualquier sesión anterior

            if (string.IsNullOrWhiteSpace(llave))
            {
                ErrorAdmin("Escribe la llave de administración.");
                return;
            }

            try
            {
                Configuracion config = Configuracion.Cargar();
                string problema;
                if (!config.EsValida(out problema))
                {
                    ErrorAdmin(problema);
                    return;
                }

                var admin = new ApiAdmin(config, llave);
                List<ResumenHuella> lista;
                try
                {
                    lista = await admin.ResumenAsync();
                }
                catch (Exception)
                {
                    admin.Dispose();
                    throw;
                }

                _admin = admin;
                Enviar(new { tipo = "admin", fase = "lista", empleados = ProyectarLista(lista) });
            }
            catch (ErrorApi ex)
            {
                ErrorAdmin(ex.Message);
            }
            catch (FormatException)
            {
                ErrorAdmin("La llave tiene caracteres no válidos.");
            }
            catch (Exception)
            {
                ErrorAdmin("Sin conexión con la API.");
            }
        }

        // Olvida la llave de administración (y cancela cualquier registro en curso)
        private void SalirAdmin()
        {
            CancelarRegistro();
            if (_admin == null) return;
            _admin.Dispose();
            _admin = null;
        }

        private void ErrorAdmin(string texto)
        {
            Enviar(new { tipo = "admin", fase = "error", texto = texto });
        }

        private static object ProyectarLista(List<ResumenHuella> lista)
        {
            return lista.Select(x => new
            {
                id = x.EmpleadoId,
                numero = x.NumeroEmpleado,
                nombre = x.Nombre,
                dedos = x.Dedos
            }).ToList();
        }

        // ---------------------------------------------------------------- Registro de huellas

        private void CancelarRegistro()
        {
            _tokenRegistro++;
            _registro.Cancelar();
        }

        private async Task IniciarRegistroAsync(int empleadoId, int dedo)
        {
            CancelarRegistro();
            int token = _tokenRegistro;

            if (_admin == null)
            {
                ErrorRegistro("Primero entra con la llave de administración.");
                return;
            }
            if (empleadoId <= 0 || dedo < 1 || dedo > 10)
            {
                ErrorRegistro("Empleado o dedo no válidos.");
                return;
            }
            if (_lector == null || !_lector.Activo)
            {
                ErrorRegistro("El lector de huellas no está listo. Revisa el cable USB.");
                return;
            }
            if (_api == null)
            {
                ErrorRegistro("El lector todavía no está conectado a la API. Revisa el estado en la pantalla principal.");
                return;
            }

            try
            {
                // Las huellas ya registradas se bajan una vez, solo para comparar y se quedan en memoria
                List<PlantillaRemota> remotas = await _api.PlantillasAsync();
                if (token != _tokenRegistro) return; // se canceló mientras se descargaban

                int total = _registro.Iniciar(empleadoId, dedo, remotas);
                Enviar(new { tipo = "admin", fase = "registroListo", total = total });
            }
            catch (ErrorApi ex)
            {
                ErrorRegistro(ex.Message);
            }
            catch (Exception)
            {
                ErrorRegistro("Sin conexión con la API.");
            }
        }

        private void ErrorRegistro(string texto)
        {
            Enviar(new { tipo = "admin", fase = "registroError", texto = texto });
        }

        // Corre en el hilo del SDK
        private void ProcesarRegistro(DPFP.Sample muestra)
        {
            ResultadoCaptura r;
            try
            {
                r = _registro.Procesar(_lector, muestra);
            }
            catch (Exception)
            {
                EnInterfaz(() => Enviar(new
                {
                    tipo = "admin", fase = "captura", ok = false, faltan = -1,
                    texto = "Error del lector. Vuelve a poner el dedo."
                }));
                return;
            }
            if (r == null) return;

            switch (r.Estado)
            {
                case EstadoCaptura.Duplicada:
                    EnInterfaz(() => ErrorRegistro(r.Mensaje));
                    break;

                case EstadoCaptura.Reintentar:
                    EnInterfaz(() => Enviar(new
                    {
                        tipo = "admin", fase = "captura", ok = false, faltan = r.Faltan, texto = r.Mensaje
                    }));
                    break;

                case EstadoCaptura.Progreso:
                    EnInterfaz(() => Enviar(new
                    {
                        tipo = "admin", fase = "captura", ok = true, faltan = r.Faltan, texto = r.Mensaje
                    }));
                    break;

                case EstadoCaptura.Listo:
                    _guardando = true;
                    EnInterfaz(() =>
                    {
                        Task tarea = GuardarHuellaAsync(r.EmpleadoId, r.Dedo, r.Plantilla);
                    });
                    break;
            }
        }

        // Hilo de la interfaz: sube la huella y refresca la lista
        private async Task GuardarHuellaAsync(int empleadoId, int dedo, byte[] plantilla)
        {
            try
            {
                if (_admin == null) throw new ErrorApi("La sesión de administrador se cerró.", 0);

                Enviar(new { tipo = "admin", fase = "guardando" });
                await _admin.GuardarHuellaAsync(empleadoId, dedo, plantilla);

                List<ResumenHuella> lista = await _admin.ResumenAsync();
                Enviar(new
                {
                    tipo = "admin",
                    fase = "guardado",
                    empleadoId = empleadoId,
                    dedo = dedo,
                    empleados = ProyectarLista(lista)
                });
            }
            catch (ErrorApi ex)
            {
                ErrorRegistro(ex.Message);
            }
            catch (Exception)
            {
                ErrorRegistro("No se pudo guardar la huella: sin conexión con la API.");
            }
            finally
            {
                Array.Clear(plantilla, 0, plantilla.Length); // la plantilla no se queda en memoria
                _guardando = false;
            }
        }

        // ---------------------------------------------------------------- Lector de huellas

        private void IntentarIniciarLector()
        {
            if (_lector == null)
            {
                _lector = new LectorHuellas();
                // Estos eventos llegan en el hilo del SDK: se pasan a la interfaz antes de usar la pantalla
                _lector.LectorConectado += () => EnInterfaz(() => MostrarLector(true, "Lector de huellas listo"));
                _lector.LectorDesconectado += () => EnInterfaz(() => MostrarLector(false, "Lector de huellas desconectado"));
                _lector.DedoPuesto += () => EnInterfaz(() => Enviar(new { tipo = "dedo", puesto = true }));
                _lector.DedoRetirado += () => EnInterfaz(() => Enviar(new { tipo = "dedo", puesto = false }));
                _lector.MuestraCapturada += AlCapturarMuestra;
            }

            if (_lector.Activo) return;

            string problema = _lector.Iniciar();
            MostrarLector(problema == null, problema ?? "Lector de huellas listo");
        }

        // Corre en el hilo del SDK
        private void AlCapturarMuestra(DPFP.Sample muestra)
        {
            if (_guardando) return;

            if (_registro.Activo)
            {
                ProcesarRegistro(muestra);
                return;
            }

            string problema = _lector.Evaluar(muestra);
            bool buena = problema == null;
            string texto = buena ? "Huella leída correctamente" : problema;
            EnInterfaz(() => Enviar(new { tipo = "lectura", buena = buena, texto = texto }));
        }

        private void MostrarLector(bool listo, string texto)
        {
            Enviar(new { tipo = "lector", listo = listo, texto = texto });
        }

        // ---------------------------------------------------------------- Estado de la API

        // Pregunta a la API cómo está este lector y se lo cuenta a la pantalla
        private async Task ActualizarEstadoAsync()
        {
            if (_actualizando) return;
            _actualizando = true;
            try
            {
                if (_api == null)
                {
                    Configuracion config;
                    try
                    {
                        config = Configuracion.Cargar();
                    }
                    catch (Exception)
                    {
                        Mostrar("Sin configurar", "Sin configurar", false,
                            "El archivo config.json tiene un error de formato. Revísalo en " + Configuracion.Ruta, "");
                        return;
                    }

                    string problema;
                    if (!config.EsValida(out problema))
                    {
                        Mostrar("Sin configurar", "Sin configurar", false, problema, "");
                        return;
                    }

                    try
                    {
                        _api = new ApiCliente(config);
                    }
                    catch (Exception)
                    {
                        Mostrar("Sin configurar", "Sin configurar", false,
                            "La llave o la dirección de config.json tienen caracteres no válidos (¿un espacio o salto de línea de más?). Revísalo en "
                            + Configuracion.Ruta, "");
                        return;
                    }
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

        // ---------------------------------------------------------------- Utilidades

        // Pasa trabajo al hilo de la interfaz; ignora si la ventana ya se cerró
        private void EnInterfaz(Action accion)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(accion);
            }
            catch (InvalidOperationException) { } // incluye ObjectDisposedException
        }

        // Solo desde el hilo de la interfaz
        private void Enviar(object mensaje)
        {
            if (_web.CoreWebView2 == null) return;
            _web.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(mensaje));
        }
    }
}