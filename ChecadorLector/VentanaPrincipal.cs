using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
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

        // Se crean con la ventana, ANTES de abrir el lector (los objetos del SDK se crean una sola vez)
        private readonly RegistroHuella _registro = new RegistroHuella();
        private readonly Identificador _identificador = new Identificador();
        private readonly ColaChecadas _cola = new ColaChecadas();
        private readonly AvisosLector _avisos = new AvisosLector();

        private ApiCliente _api;
        private ApiAdmin _admin;
        private LectorHuellas _lector;
        private bool _iniciado;
        private bool _actualizando;
        private bool _vaciando;                    // enviando la cola: no se empalman dos envíos
        private bool _enviandoAvisos;              // mandando avisos del lector: no se empalman dos envíos
        private bool _lectorSeDesconecto;          // para avisar cuando el lector regresa
        private bool _falloInicioReportado;        // el lector no pudo iniciar y ya se avisó
        private int _fallosCola;                   // envíos seguidos que la API rechazó (no por falta de red)
        private int _tokenRegistro;                // para ignorar un registro que se canceló mientras se preparaba
        private volatile bool _guardando;          // subiendo una huella: se ignoran capturas nuevas
        private volatile bool _checando;           // mandando una checada: se ignoran capturas nuevas
        private volatile bool _modoAdmin;          // pantalla de administrador abierta: las capturas no checan
        private string _ultimaSucursal = "Sin conectar";

        // Diferencia entre el reloj del servidor y el de esta PC (sirve para sellar las checadas)
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

            MostrarCola(); // por si quedaron checadas pendientes de antes de cerrar el programa

            // Aviso de que el programa arrancó (sale a la API en cuanto haya conexión)
            _avisos.Agregar("INICIO", TextoInicio(), DateTimeOffset.Now);

            IntentarIniciarLector();
            _timerLector.Start();

            await ActualizarEstadoAsync();
            _timerEstado.Start();
        }

        private static string TextoInicio()
        {
            string version = typeof(VentanaPrincipal).Assembly.GetName().Version.ToString();
            return "Programa iniciado en " + Environment.MachineName + " (versión " + version + ")";
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
                    case "abrirAdmin":
                        _modoAdmin = true;
                        break;
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
            CerrarSesionAdmin(); // limpia cualquier sesión anterior (la pantalla de administrador sigue abierta)

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

        // Cierra el modo administrador: olvida la llave y cancela cualquier registro
        private void SalirAdmin()
        {
            _modoAdmin = false;
            CerrarSesionAdmin();
        }

        private void CerrarSesionAdmin()
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
                _avisos.Agregar("ERROR_LECTOR", "Error del lector durante el registro de una huella", Ahora());
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

                // La huella nueva ya debe poder reconocerse al checar
                await ActualizarPlantillasAsync();
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
                _lector.LectorConectado += () => EnInterfaz(() =>
                {
                    MostrarLector(true, "Lector de huellas listo");
                    if (_lectorSeDesconecto)
                    {
                        _lectorSeDesconecto = false;
                        _avisos.Agregar("OTRO", "Lector de huellas conectado de nuevo", Ahora());
                    }
                });
                _lector.LectorDesconectado += () => EnInterfaz(() =>
                {
                    MostrarLector(false, "Lector de huellas desconectado");
                    _lectorSeDesconecto = true;
                    _avisos.Agregar("ERROR_LECTOR", "Lector de huellas desconectado", Ahora());
                });
                _lector.DedoPuesto += () => EnInterfaz(() => Enviar(new { tipo = "dedo", puesto = true }));
                _lector.DedoRetirado += () => EnInterfaz(() => Enviar(new { tipo = "dedo", puesto = false }));
                _lector.MuestraCapturada += AlCapturarMuestra;
            }

            if (_lector.Activo) return;

            string problema = _lector.Iniciar();
            MostrarLector(problema == null, problema ?? "Lector de huellas listo");

            // Un fallo al iniciar se avisa una sola vez (este método se reintenta cada 5 s)
            if (problema != null)
            {
                if (!_falloInicioReportado)
                {
                    _falloInicioReportado = true;
                    _avisos.Agregar("ERROR_LECTOR", problema, Ahora());
                }
            }
            else if (_falloInicioReportado)
            {
                _falloInicioReportado = false;
                _avisos.Agregar("OTRO", "Lector de huellas listo después de un fallo al iniciar", Ahora());
            }
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

            // Con la pantalla de administrador abierta, las capturas no cuentan como checadas
            if (_modoAdmin) return;

            // Todavía se está mandando la checada anterior
            if (_checando) return;

            ProcesarChecada(muestra);
        }

        private void MostrarLector(bool listo, string texto)
        {
            Enviar(new { tipo = "lector", listo = listo, texto = texto });
        }

        // ---------------------------------------------------------------- Checar

        // Hora actual corregida con la del servidor (para no depender de que el reloj de la PC esté bien)
        private DateTimeOffset Ahora()
        {
            return (DateTimeOffset.UtcNow + DesfaseReloj).ToLocalTime();
        }

        // Corre en el hilo del SDK
        private void ProcesarChecada(DPFP.Sample muestra)
        {
            Identificacion quien;
            try
            {
                quien = _identificador.Identificar(_lector, muestra);
            }
            catch (Exception)
            {
                _avisos.Agregar("ERROR_LECTOR", "Error del lector al identificar una huella", Ahora());
                EnInterfaz(() => MostrarChecada("error", "Error del lector", "Vuelve a poner el dedo", ""));
                return;
            }

            switch (quien.Estado)
            {
                case EstadoIdentificacion.SinPlantillas:
                    EnInterfaz(() => MostrarChecada("error", "Sin huellas cargadas",
                        "Revisa la conexión con la API o avisa a sistemas", ""));
                    break;

                case EstadoIdentificacion.MalaLectura:
                    EnInterfaz(() => MostrarChecada("aviso", "Lectura no válida", quien.Mensaje, ""));
                    break;

                case EstadoIdentificacion.NoReconocida:
                    _avisos.NoReconocida(Ahora());
                    EnInterfaz(() => MostrarChecada("aviso", "No te reconocí",
                        "Limpia y seca el dedo y vuelve a intentarlo", ""));
                    break;

                case EstadoIdentificacion.Multiple:
                    _avisos.Agregar("COINCIDENCIA_MULTIPLE",
                        "Una huella coincidió con más de un empleado", Ahora());
                    EnInterfaz(() => MostrarChecada("error", "Huella no válida para checar",
                        "Avisa a sistemas", ""));
                    break;

                case EstadoIdentificacion.Reconocida:
                    _checando = true;
                    DateTimeOffset cuando = Ahora();   // la hora en que se capturó
                    Guid idLocal = Guid.NewGuid();     // un reintento de ESTA checada mandaría el mismo
                    EnInterfaz(() =>
                    {
                        Task tarea = RegistrarChecadaAsync(quien, cuando, idLocal);
                    });
                    break;
            }
        }

        // Hilo de la interfaz: manda la checada. Si no se puede, la guarda en la cola para enviarla después.
        private async Task RegistrarChecadaAsync(Identificacion quien, DateTimeOffset cuando, Guid idLocal)
        {
            try
            {
                if (_api == null)
                {
                    GuardarEnCola(quien, cuando, idLocal);
                    return;
                }

                // relojLector va con la hora ya corregida: el desfase de la PC se avisa en pantalla (arriba)
                // y la API queda como red de seguridad por si esa corrección estuviera vieja.
                ResultadoChecada r = await _api.EnviarChecadaAsync(idLocal, quien.EmpleadoId, cuando, Ahora());
                MostrarResultadoChecada(quien, cuando, r);

                // Si hubo respuesta, hay conexión: se aprovecha para vaciar lo que estuviera pendiente
                if (_cola.Cantidad > 0)
                {
                    Task vaciado = VaciarColaAsync();
                }
            }
            catch (ErrorApi ex) when (NoSePuedeReintentar(ex))
            {
                MostrarChecada("error", "No se pudo checar", ex.Message + " La checada NO se guardó.", "");
            }
            catch (Exception)
            {
                // Sin red, tiempo agotado o la API con problemas: se guarda y se manda después
                GuardarEnCola(quien, cuando, idLocal);
            }
            finally
            {
                _checando = false;
            }
        }

        // Errores 4xx (menos 401 y 429): reintentar no serviría de nada
        private static bool NoSePuedeReintentar(ErrorApi ex)
        {
            return ex.Codigo >= 400 && ex.Codigo < 500 && ex.Codigo != 401 && ex.Codigo != 429;
        }

        private void GuardarEnCola(Identificacion quien, DateTimeOffset cuando, Guid idLocal)
        {
            try
            {
                _cola.Agregar(new ChecadaPendiente
                {
                    IdLocal = idLocal.ToString(),
                    EmpleadoId = quien.EmpleadoId,
                    FechaHora = ApiCliente.Iso(cuando)
                });
            }
            catch (Exception)
            {
                MostrarChecada("error", "No se pudo guardar la checada",
                    "La checada NO se guardó. Avisa a sistemas.", "");
                return;
            }

            MostrarCola();
            string hora = cuando.ToString("HH:mm", CultureInfo.InvariantCulture);
            MostrarChecada("aviso", quien.Nombre, "Checada guardada sin conexión · " + hora,
                "Se enviará cuando regrese el internet");
        }

        private void MostrarResultadoChecada(Identificacion quien, DateTimeOffset cuando, ResultadoChecada r)
        {
            string hora = cuando.ToString("HH:mm", CultureInfo.InvariantCulture);

            switch (r.Estado)
            {
                case "REGISTRADA":
                case "DUPLICADA":
                    string detalle = "";
                    if (r.Estado == "REGISTRADA" && r.NumeroDelDia.HasValue)
                    {
                        detalle = r.EsperadasDelDia.HasValue && r.EsperadasDelDia.Value > 0
                            ? r.NumeroDelDia.Value + " de " + r.EsperadasDelDia.Value + " hoy"
                            : "Checada " + r.NumeroDelDia.Value + " de hoy";
                    }
                    MostrarChecada("ok", quien.Nombre, "Checada registrada · " + hora, detalle);
                    break;

                case "REBOTE":
                    string previa = "";
                    DateTimeOffset anterior;
                    if (r.Checada != null && DateTimeOffset.TryParse(
                            r.Checada.FechaHoraUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out anterior))
                    {
                        previa = "Tu checada de las "
                                 + anterior.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                                 + " ya está registrada";
                    }
                    MostrarChecada("aviso", quien.Nombre, "Ya checaste hace un momento", previa);
                    break;

                default: // RECHAZADA
                    MostrarRechazo(quien, r.Motivo);
                    break;
            }
        }

        private void MostrarRechazo(Identificacion quien, string motivo)
        {
            switch (motivo)
            {
                case "RELOJ_DESFASADO":
                    MostrarChecada("error", "Reloj del equipo desfasado",
                        "La checada NO se guardó. Avisa a sistemas.", "");
                    break;
                case "SUCURSAL_INACTIVA":
                    MostrarChecada("error", "Sucursal inactiva",
                        "La checada NO se guardó. Avisa a sistemas.", "");
                    break;
                case "EMPLEADO_BAJA":
                case "EMPLEADO_NO_EXISTE":
                    MostrarChecada("error", quien.Nombre, "No puedes checar. Habla con recursos humanos.", "");
                    break;
                case "ANTES_DE_INGRESO":
                    MostrarChecada("error", quien.Nombre,
                        "Tu fecha de ingreso es posterior a hoy. Habla con recursos humanos.", "");
                    break;
                default: // fechas inválidas, antigua o futura
                    MostrarChecada("error", "Fecha del equipo incorrecta",
                        "La checada NO se guardó. Avisa a sistemas.", "");
                    break;
            }
        }

        // clase: "ok", "aviso" o "error". Hilo de la interfaz.
        private void MostrarChecada(string clase, string titulo, string subtitulo, string detalle)
        {
            if (clase == "ok") SystemSounds.Asterisk.Play();
            else if (clase == "aviso") SystemSounds.Exclamation.Play();
            else SystemSounds.Hand.Play();

            Enviar(new { tipo = "checada", clase = clase, titulo = titulo, subtitulo = subtitulo, detalle = detalle });
        }

        // ---------------------------------------------------------------- Cola de checadas pendientes

        // Manda a la API las checadas guardadas, de 200 en 200, de la más vieja a la más nueva
        private async Task VaciarColaAsync()
        {
            if (_vaciando || _api == null) return;
            _vaciando = true;
            try
            {
                while (_cola.Cantidad > 0)
                {
                    List<ChecadaPendiente> lote = _cola.Primeras(200);
                    RespuestaLote respuesta = await _api.EnviarLoteAsync(lote, Ahora());

                    // Se quitan las que la API ya resolvió. Las rechazadas con motivo reintentable
                    // (reloj desfasado, sucursal inactiva) se conservan hasta que se arregle.
                    var resueltas = new HashSet<string>();
                    foreach (ResultadoChecada r in respuesta.Resultados)
                    {
                        if (r != null && !string.IsNullOrEmpty(r.IdLocal) && EstaResuelta(r))
                        {
                            resueltas.Add(r.IdLocal);
                        }
                    }

                    _fallosCola = 0;
                    if (resueltas.Count == 0) break; // nada avanzó: se reintenta en el próximo ciclo

                    _cola.Quitar(resueltas);
                    MostrarCola();
                }
            }
            catch (ErrorApi)
            {
                _fallosCola++; // la API contestó pero no aceptó el envío
            }
            catch (Exception)
            {
                // Sin conexión u otra falla pasajera: se conserva todo y se reintenta
            }
            finally
            {
                _vaciando = false;
                MostrarCola();
            }
        }

        private static bool EstaResuelta(ResultadoChecada r)
        {
            switch (r.Estado)
            {
                case "REGISTRADA":
                case "DUPLICADA":
                case "REBOTE":
                    return true;
                case "RECHAZADA":
                    return r.Reintentable != true; // la API ya dejó el rechazo en su bitácora
                default:
                    return false;
            }
        }

        private void MostrarCola()
        {
            bool mal;
            string texto = TextoCola(out mal);
            Enviar(new { tipo = "cola", texto = texto, mal = mal });
        }

        private string TextoCola(out bool mal)
        {
            mal = false;
            int pendientes = _cola.Cantidad;

            if (pendientes == 0)
            {
                return _cola.ArchivoDanado ? "La cola de checadas anterior estaba dañada; se guardó una copia" : "";
            }

            string texto = pendientes == 1
                ? "1 checada pendiente de enviar"
                : pendientes + " checadas pendientes de enviar";

            DateTimeOffset? masAntigua = _cola.MasAntigua();
            if (masAntigua.HasValue)
            {
                TimeSpan edad = Ahora() - masAntigua.Value;
                if (edad.TotalMinutes >= 5)
                {
                    texto += " (la más antigua, de hace " + Duracion(edad) + ")";
                    mal = edad.TotalHours >= 1;
                }
            }

            if (_fallosCola >= 3)
            {
                texto += " · no se pueden enviar; avisa a sistemas";
                mal = true;
            }
            return texto;
        }

        private static string Duracion(TimeSpan edad)
        {
            return edad.TotalHours >= 1
                ? Math.Floor(edad.TotalHours) + " h"
                : Math.Floor(edad.TotalMinutes) + " min";
        }

        // ---------------------------------------------------------------- Avisos del lector

        // Manda a la API los avisos que se juntaron. Si falla, se quedan para el siguiente ciclo.
        private async Task EnviarAvisosAsync()
        {
            if (_enviandoAvisos || _api == null) return;
            _enviandoAvisos = true;
            try
            {
                List<AvisoPendiente> lista = _avisos.Tomar(Ahora());
                for (int i = 0; i < lista.Count; i++)
                {
                    try
                    {
                        await _api.EnviarEventoAsync(lista[i].Tipo, lista[i].Detalle, lista[i].FechaHora);
                    }
                    catch (ErrorApi ex) when (NoSePuedeReintentar(ex))
                    {
                        // La API lo rechazó por su contenido: reintentar no serviría, se descarta
                    }
                    catch (Exception)
                    {
                        // Sin conexión, límite de envíos o llave inválida: este y los que faltan se conservan
                        _avisos.Devolver(lista.Skip(i));
                        break;
                    }
                }
            }
            finally
            {
                _enviandoAvisos = false;
            }
        }

        // ---------------------------------------------------------------- Huellas cargadas

        // Baja las plantillas solo si cambiaron. Si falla se conserva lo que ya había.
        private async Task ActualizarPlantillasAsync()
        {
            if (_api == null) return;
            try
            {
                RespuestaPlantillas r = await _api.PlantillasAsync(_identificador.Version);
                if (!r.SinCambios) _identificador.Cargar(r.Plantillas, r.Version);
            }
            catch (Exception)
            {
                // Se reintenta en el siguiente ciclo
            }
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

                await ActualizarPlantillasAsync();

                // Hay conexión: se manda lo que haya quedado pendiente
                if (_cola.Cantidad > 0)
                {
                    Task vaciado = VaciarColaAsync();
                }
                Task avisos = EnviarAvisosAsync();

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

                int huellas = _identificador.Total;
                string cargadas = huellas == 0 ? "sin huellas cargadas" : huellas + " huellas cargadas";
                Mostrar(_ultimaSucursal, "En línea", true,
                    r.Estado.Lector.Nombre + " · API conectada · " + cargadas, aviso);
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