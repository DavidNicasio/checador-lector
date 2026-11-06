using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using DPFP;
using DPFP.Error;
using DPFP.Processing;
using CaptureFeedback = DPFP.Capture.CaptureFeedback;
using Capturador = DPFP.Capture.Capture;
using Verificador = DPFP.Verification.Verification;
using ResultadoVerificacion = DPFP.Verification.Verification.Result;

namespace PruebaLector
{
    /// <summary>
    /// Prueba de humo del lector U.are.U 4500: lista lectores, captura, registra plantillas en
    /// memoria e identifica 1 a N. Sin red, sin API y sin escribir nada en disco.
    /// Los eventos del SDK llegan en un hilo propio. Como en el ejemplo oficial, cada muestra se
    /// procesa síncronamente dentro del callback OnComplete (extraer, registrar, comparar). Solo las
    /// actualizaciones de pantalla pasan al hilo de la interfaz con BeginInvoke, y el estado
    /// compartido (plantillas, registro en curso, captura) se protege con lock (_candado).
    /// </summary>
    internal sealed class FormPrincipal : Form, DPFP.Capture.EventHandler
    {
        // PROBABILITY_ONE del SDK: FAR de 1.0. El FAR pedido es PROBABILITY_ONE / N.
        private const int ProbabilityOne = 0x7FFFFFFF;
        private const int ValorInicialN = 100000;

        private static readonly string[] Dedos =
        {
            "Pulgar derecho", "Índice derecho", "Medio derecho", "Anular derecho", "Meñique derecho",
            "Pulgar izquierdo", "Índice izquierdo", "Medio izquierdo", "Anular izquierdo", "Meñique izquierdo",
        };

        private enum Modo { Observar, Registrar, Identificar }

        // --- Controles ---
        private ListView _lvLectores;
        private ListView _lvPlantillas;
        private Button _btnActualizar;
        private Button _btnIniciar;
        private Button _btnDetener;
        private Button _btnIdaVuelta;
        private RadioButton _rbObservar;
        private RadioButton _rbRegistrar;
        private RadioButton _rbIdentificar;
        private TextBox _txtNombre;
        private TextBox _txtLog;
        private ComboBox _cboDedo;
        private NumericUpDown _numN;
        private Label _lblEstado;
        private Label _lblFaltan;
        private Label _lblFar;
        private Label _lblResultado;
        private Panel _panelRegistrar;
        private Panel _panelIdentificar;

        // --- Estado ---
        // Todo lo de esta sección lo tocan el hilo del SDK (callback OnComplete) y el de la interfaz
        // (botones), así que SIEMPRE se lee o escribe dentro de lock (_candado).
        // No se llama a nada que bloquee (Invoke, StopCapture) mientras se sostiene el candado.
        private readonly object _candado = new object();
        private Capturador _captura;
        private readonly List<PlantillaGuardada> _plantillas = new List<PlantillaGuardada>();
        private string _claveRegistro;                 // null = no hay un registro en curso
        private PlantillaGuardada _idaVueltaOriginal;
        private Template _idaVueltaNueva;

        // Los tres objetos del SDK se crean UNA sola vez (aquí, antes de cualquier captura) y no se
        // sueltan: el registro se reinicia con Clear() en vez de crear otro Enrollment.
        private readonly FeatureExtraction _extractor = new FeatureExtraction();
        private readonly Verificador _verificador = new Verificador();
        private readonly Enrollment _registro = new Enrollment();

        // Copia de lo que muestra la pantalla. El hilo del SDK procesa la muestra y no puede leer
        // controles, así que la interfaz actualiza estos valores cuando el usuario cambia algo.
        private Modo _modo = Modo.Observar;
        private string _nombre = "";
        private string _dedo = "";
        private int _far = ProbabilityOne / ValorInicialN;

        public FormPrincipal()
        {
            Text = "Prueba de lector de huellas - U.are.U 4500";
            ClientSize = new Size(1120, 690);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;

            ConstruirInterfaz();

            Load += (s, e) =>
            {
                Log("Aplicación lista. Plantillas solo en memoria: se pierden al cerrar.");
                RefrescarLectores();
                ActualizarBotones();
                ActualizarFar();
                GuardarEstadoDePantalla();
                ActualizarFaltan();
            };
            FormClosing += (s, e) => DetenerCaptura();
        }

        // =====================================================================
        //  Interfaz
        // =====================================================================

        private void ConstruirInterfaz()
        {
            var izquierda = new Panel { Dock = DockStyle.Left, Width = 545, AutoScroll = true, Padding = new Padding(8) };
            var derecha = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 8, 8) };

            // Se agrega primero el que rellena, para que el acoplamiento (Dock) quede bien.
            Controls.Add(derecha);
            Controls.Add(izquierda);

            int y = 8;

            // --- Lectores ---
            var gbLectores = Grupo("1. Lectores detectados", y, 128);
            _lvLectores = new ListView
            {
                Location = new Point(8, 20),
                Size = new Size(504, 72),
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
            };
            _lvLectores.Columns.Add("Producto", 200);
            _lvLectores.Columns.Add("Número de serie", 295);
            _btnActualizar = new Button { Text = "Actualizar lista", Location = new Point(8, 97), Size = new Size(130, 26) };
            _btnActualizar.Click += (s, e) => RefrescarLectores();
            gbLectores.Controls.Add(_lvLectores);
            gbLectores.Controls.Add(_btnActualizar);
            izquierda.Controls.Add(gbLectores);
            y += gbLectores.Height + 6;

            // --- Captura ---
            var gbCaptura = Grupo("2. Captura", y, 62);
            _btnIniciar = new Button { Text = "Iniciar captura", Location = new Point(8, 22), Size = new Size(120, 28) };
            _btnDetener = new Button { Text = "Detener", Location = new Point(136, 22), Size = new Size(100, 28) };
            _lblEstado = new Label { Text = "Captura detenida", Location = new Point(248, 28), AutoSize = true };
            _btnIniciar.Click += (s, e) => IniciarCaptura();
            _btnDetener.Click += (s, e) => DetenerCaptura();
            gbCaptura.Controls.Add(_btnIniciar);
            gbCaptura.Controls.Add(_btnDetener);
            gbCaptura.Controls.Add(_lblEstado);
            izquierda.Controls.Add(gbCaptura);
            y += gbCaptura.Height + 6;

            // --- Modo ---
            var gbModo = Grupo("3. Qué hacer con cada captura", y, 142);
            _rbObservar = new RadioButton { Text = "Solo observar", Location = new Point(8, 20), AutoSize = true, Checked = true };
            _rbRegistrar = new RadioButton { Text = "Registrar", Location = new Point(130, 20), AutoSize = true };
            _rbIdentificar = new RadioButton { Text = "Identificar 1 a N", Location = new Point(232, 20), AutoSize = true };
            // CheckedChanged se dispara también en el que se desmarca: solo se atiende el marcado.
            EventHandler cambioModo = (s, e) =>
            {
                if (!((RadioButton)s).Checked) return;
                GuardarEstadoDePantalla();
                ActualizarModo();
            };
            _rbObservar.CheckedChanged += cambioModo;
            _rbRegistrar.CheckedChanged += cambioModo;
            _rbIdentificar.CheckedChanged += cambioModo;

            _panelRegistrar = new Panel { Location = new Point(8, 50), Size = new Size(504, 86), Visible = false };
            _panelRegistrar.Controls.Add(new Label { Text = "Nombre:", Location = new Point(0, 6), AutoSize = true });
            _txtNombre = new TextBox { Location = new Point(62, 2), Width = 436, MaxLength = 100 };
            _txtNombre.TextChanged += (s, e) => GuardarEstadoDePantalla();
            _panelRegistrar.Controls.Add(_txtNombre);
            _panelRegistrar.Controls.Add(new Label { Text = "Dedo:", Location = new Point(0, 38), AutoSize = true });
            _cboDedo = new ComboBox { Location = new Point(62, 34), Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboDedo.Items.AddRange(Dedos);
            _cboDedo.SelectedIndex = 1;
            _cboDedo.SelectedIndexChanged += (s, e) => GuardarEstadoDePantalla();
            _panelRegistrar.Controls.Add(_cboDedo);
            _lblFaltan = new Label { Location = new Point(244, 38), AutoSize = true };
            _panelRegistrar.Controls.Add(_lblFaltan);
            _panelRegistrar.Controls.Add(new Label
            {
                Text = "Se necesitan varias capturas del mismo dedo (levanta y vuelve a poner).",
                Location = new Point(0, 64),
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            });

            _panelIdentificar = new Panel { Location = new Point(8, 50), Size = new Size(504, 86), Visible = false };
            _panelIdentificar.Controls.Add(new Label { Text = "FAR pedido: 1 en", Location = new Point(0, 6), AutoSize = true });
            _numN = new NumericUpDown
            {
                Location = new Point(100, 2),
                Width = 110,
                Minimum = 1,
                Maximum = 1000000000,
                Value = ValorInicialN,
                ThousandsSeparator = true,
            };
            _numN.ValueChanged += (s, e) =>
            {
                ActualizarFar();
                GuardarEstadoDePantalla();
            };
            _panelIdentificar.Controls.Add(_numN);
            _lblFar = new Label { Location = new Point(218, 6), AutoSize = true };
            _panelIdentificar.Controls.Add(_lblFar);
            _panelIdentificar.Controls.Add(new Label
            {
                Text = "FAR = PROBABILITY_ONE / N. Un N más grande es más estricto.",
                Location = new Point(0, 38),
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            });

            gbModo.Controls.Add(_rbObservar);
            gbModo.Controls.Add(_rbRegistrar);
            gbModo.Controls.Add(_rbIdentificar);
            gbModo.Controls.Add(_panelRegistrar);
            gbModo.Controls.Add(_panelIdentificar);
            izquierda.Controls.Add(gbModo);
            y += gbModo.Height + 6;

            // --- Plantillas ---
            var gbPlantillas = Grupo("4. Plantillas en memoria", y, 190);
            _lvPlantillas = new ListView
            {
                Location = new Point(8, 20),
                Size = new Size(504, 126),
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
            };
            _lvPlantillas.Columns.Add("Persona", 200);
            _lvPlantillas.Columns.Add("Dedo", 200);
            _lvPlantillas.Columns.Add("Bytes", 80);
            _btnIdaVuelta = new Button { Text = "Prueba de ida y vuelta", Location = new Point(8, 154), Size = new Size(190, 28) };
            _btnIdaVuelta.Click += (s, e) => IniciarIdaYVuelta();
            gbPlantillas.Controls.Add(_lvPlantillas);
            gbPlantillas.Controls.Add(_btnIdaVuelta);
            izquierda.Controls.Add(gbPlantillas);
            y += gbPlantillas.Height + 6;

            // --- Resultado ---
            var gbResultado = Grupo("Último resultado", y, 104);
            _lblResultado = new Label
            {
                Location = new Point(8, 20),
                Size = new Size(504, 78),
                Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
                Text = "—",
            };
            gbResultado.Controls.Add(_lblResultado);
            izquierda.Controls.Add(gbResultado);

            // --- Log ---
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
                BackColor = SystemColors.Window,
            };
            var pie = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 6, 0, 0) };
            var btnLimpiar = new Button { Text = "Limpiar log", Dock = DockStyle.Left, Width = 110 };
            btnLimpiar.Click += (s, e) => _txtLog.Clear();
            pie.Controls.Add(btnLimpiar);
            derecha.Controls.Add(_txtLog);
            derecha.Controls.Add(pie);
        }

        private static GroupBox Grupo(string titulo, int y, int alto)
        {
            return new GroupBox { Text = titulo, Location = new Point(8, y), Size = new Size(520, alto) };
        }

        private Modo ModoActual
        {
            get
            {
                if (_rbRegistrar.Checked) return Modo.Registrar;
                if (_rbIdentificar.Checked) return Modo.Identificar;
                return Modo.Observar;
            }
        }

        private void ActualizarModo()
        {
            _panelRegistrar.Visible = ModoActual == Modo.Registrar;
            _panelIdentificar.Visible = ModoActual == Modo.Identificar;
            Log("Modo: " + (ModoActual == Modo.Registrar ? "Registrar"
                : ModoActual == Modo.Identificar ? "Identificar 1 a N" : "Solo observar"));
        }

        private bool CapturaActiva
        {
            get
            {
                lock (_candado) return _captura != null;
            }
        }

        /// <summary>Hilo de la interfaz: copia lo que muestran los controles para que lo lea el hilo del SDK.</summary>
        private void GuardarEstadoDePantalla()
        {
            if (_rbRegistrar == null || _txtNombre == null || _cboDedo == null || _numN == null) return;
            Modo modo = ModoActual;
            string nombre = _txtNombre.Text.Trim();
            string dedo = _cboDedo.Text;
            int far = FarPedido();
            lock (_candado)
            {
                _modo = modo;
                _nombre = nombre;
                _dedo = dedo;
                _far = far;
            }
        }

        private void ActualizarBotones()
        {
            bool activa = CapturaActiva;
            _btnIniciar.Enabled = !activa;
            _btnDetener.Enabled = activa;
            _lblEstado.Text = activa ? "Captura en curso: pon un dedo en el lector" : "Captura detenida";
        }

        private int FarPedido()
        {
            int n = (int)_numN.Value;
            return ProbabilityOne / n;
        }

        private void ActualizarFar()
        {
            _lblFar.Text = "= " + FarPedido().ToString("N0", CultureInfo.CurrentCulture);
        }

        // Los tres siguientes se pueden llamar desde cualquier hilo: calculan el dato bajo el candado
        // y solo la actualización de pantalla se pasa al hilo de la interfaz (BeginInvoke).

        private void ActualizarFaltan()
        {
            string texto;
            lock (_candado)
            {
                texto = _claveRegistro == null
                    ? "Sin registro en curso"
                    : "Faltan " + _registro.FeaturesNeeded + " captura(s)";
            }
            EnInterfaz(() => _lblFaltan.Text = texto);
        }

        private void MostrarResultado(string texto, Color color)
        {
            EnInterfaz(() =>
            {
                _lblResultado.Text = texto;
                _lblResultado.ForeColor = color;
            });
        }

        private void RefrescarPlantillas()
        {
            string[][] filas;
            lock (_candado)
            {
                // Solo el tamaño: el contenido de una plantilla nunca se muestra ni se registra.
                filas = _plantillas
                    .Select(p => new[] { p.Persona, p.Dedo, p.Plantilla.Size.ToString(CultureInfo.CurrentCulture) })
                    .ToArray();
            }
            EnInterfaz(() =>
            {
                _lvPlantillas.Items.Clear();
                foreach (string[] fila in filas)
                {
                    var item = new ListViewItem(fila[0]);
                    item.SubItems.Add(fila[1]);
                    item.SubItems.Add(fila[2]);
                    _lvPlantillas.Items.Add(item);
                }
            });
        }

        // =====================================================================
        //  Registro (log)
        // =====================================================================

        private void Log(string mensaje)
        {
            if (IsDisposed) return;
            // La hora se toma aquí (en el hilo que reporta), no cuando la interfaz alcanza a pintarla.
            string linea = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                           + "  " + mensaje + Environment.NewLine;
            if (InvokeRequired)
            {
                EnInterfaz(() => _txtLog.AppendText(linea));
                return;
            }
            _txtLog.AppendText(linea);
        }

        private void LogSdk(string accion, SDKException ex)
        {
            Log("ERROR del SDK al " + accion + ": ErrorCode=" + ex.ErrorCode
                + " (" + (int)ex.ErrorCode + ") - " + ex.Message);
        }

        private void LogError(string accion, Exception ex)
        {
            var sdk = ex as SDKException;
            if (sdk != null)
            {
                LogSdk(accion, sdk);
                return;
            }
            Log("ERROR al " + accion + ": " + ex.GetType().FullName + " - " + ex.Message);
            if (ex.InnerException != null)
            {
                Log("   interna: " + ex.InnerException.GetType().FullName + " - " + ex.InnerException.Message);
            }
        }

        /// <summary>Pasa trabajo al hilo de la interfaz; ignora si la ventana ya se cerró.</summary>
        private void EnInterfaz(Action accion)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(accion);
            }
            catch (InvalidOperationException) { } // incluye ObjectDisposedException
        }

        // =====================================================================
        //  Lectores y captura
        // =====================================================================

        private void RefrescarLectores()
        {
            _lvLectores.Items.Clear();
            try
            {
                var lectores = new DPFP.Capture.ReadersCollection();
                foreach (DPFP.Capture.ReaderDescription d in lectores.Values)
                {
                    var item = new ListViewItem(d.ProductName ?? "(sin nombre)") { Tag = d.SerialNumber };
                    item.SubItems.Add(d.SerialNumber ?? "");
                    _lvLectores.Items.Add(item);
                }
                Log(lectores.Count == 0
                    ? "No se detectó ningún lector."
                    : "Lectores detectados: " + lectores.Count);
                if (_lvLectores.Items.Count > 0) _lvLectores.Items[0].Selected = true;
            }
            catch (Exception ex)
            {
                LogError("listar los lectores", ex);
            }
        }

        private void IniciarCaptura()
        {
            if (CapturaActiva) return;
            Capturador nueva = null;
            try
            {
                string serie = _lvLectores.SelectedItems.Count > 0
                    ? (string)_lvLectores.SelectedItems[0].Tag
                    : null;
                nueva = serie != null
                    ? new Capturador(serie, DPFP.Capture.Priority.Normal)
                    : new Capturador(DPFP.Capture.Priority.Normal);
                nueva.EventHandler = this;
                // Se registra ANTES de StartCapture: si la primera muestra llega enseguida, el
                // callback ya debe ver la captura como activa.
                lock (_candado) _captura = nueva;
                nueva.StartCapture();
                Log("Captura iniciada (Priority.Normal)" + (serie != null ? " en el lector seleccionado." : "."));
            }
            catch (Exception ex)
            {
                LogError("iniciar la captura", ex);
                lock (_candado)
                {
                    if (ReferenceEquals(_captura, nueva)) _captura = null;
                }
                if (nueva != null)
                {
                    try { nueva.Dispose(); } catch (Exception) { }
                }
            }
            ActualizarBotones();
        }

        private void DetenerCaptura()
        {
            Capturador actual;
            lock (_candado)
            {
                actual = _captura;
                _captura = null; // lo que llegue después de detener se ignora
            }
            if (actual == null) return;

            // StopCapture y Dispose van FUERA del candado: pueden esperar a que termine un callback
            // del SDK, y ese callback necesita el candado.
            try { actual.StopCapture(); }
            catch (Exception ex) { LogError("detener la captura", ex); }

            try { actual.Dispose(); }
            catch (Exception ex) { LogError("liberar el lector", ex); }

            if (!IsDisposed && IsHandleCreated)
            {
                Log("Captura detenida.");
                ActualizarBotones();
            }
        }

        // =====================================================================
        //  Eventos del SDK (llegan en hilos del SDK -> a la interfaz con BeginInvoke)
        // =====================================================================

        public void OnReaderConnect(object Capture, string ReaderSerialNumber)
        {
            EnInterfaz(() => Log("OnReaderConnect (lector " + ReaderSerialNumber + ")"));
        }

        public void OnReaderDisconnect(object Capture, string ReaderSerialNumber)
        {
            EnInterfaz(() => Log("OnReaderDisconnect (lector " + ReaderSerialNumber + ")"));
        }

        public void OnFingerTouch(object Capture, string ReaderSerialNumber)
        {
            EnInterfaz(() => Log("OnFingerTouch"));
        }

        public void OnFingerGone(object Capture, string ReaderSerialNumber)
        {
            EnInterfaz(() => Log("OnFingerGone"));
        }

        public void OnSampleQuality(object Capture, string ReaderSerialNumber, CaptureFeedback CaptureFeedback)
        {
            EnInterfaz(() => Log("OnSampleQuality: " + CaptureFeedback));
        }

        public void OnComplete(object Capture, string ReaderSerialNumber, Sample Sample)
        {
            // Igual que el ejemplo oficial (CaptureForm.OnComplete): la muestra se procesa AQUÍ MISMO,
            // síncronamente y en el hilo del SDK, tal como llega. Nada de pasarla a otro hilo.
            ProcesarMuestra(Sample);
        }

        // =====================================================================
        //  Procesamiento de cada captura: corre en el hilo del SDK, dentro del callback.
        //  Solo las actualizaciones de pantalla (Log, MostrarResultado...) pasan a la interfaz.
        // =====================================================================

        private void ProcesarMuestra(Sample muestra)
        {
            try
            {
                lock (_candado)
                {
                    if (_captura == null) return; // llegó después de pulsar "Detener"
                    Log("OnComplete: muestra capturada");

                    if (_idaVueltaNueva != null)
                    {
                        ProbarIdaYVuelta(muestra);
                        return;
                    }
                    switch (_modo)
                    {
                        case Modo.Registrar:
                            Registrar(muestra);
                            break;
                        case Modo.Identificar:
                            Identificar(muestra);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Una excepción que escape de un callback del SDK podría tirar el proceso.
                LogError("procesar la muestra", ex);
            }
        }

        /// <summary>Extrae el FeatureSet; devuelve null (y lo explica en el log) si la calidad no alcanza.</summary>
        private FeatureSet Extraer(Sample muestra, DataPurpose proposito)
        {
            // Como en el ejemplo oficial (CaptureForm.ExtractFeatures): el FeatureSet que se pasa con ref
            // es una instancia NUEVA en cada llamada (nunca null) y el feedback inicial es None.
            CaptureFeedback calidad = CaptureFeedback.None;
            FeatureSet caracteristicas = new FeatureSet();
            try
            {
                _extractor.CreateFeatureSet(muestra, proposito, ref calidad, ref caracteristicas);
            }
            catch (Exception ex)
            {
                // Diagnóstico: qué valor dejó CreateFeatureSet en CaptureFeedback y el mensaje completo.
                Log("CreateFeatureSet falló (" + proposito + "). CaptureFeedback que dejó: " + calidad
                    + " (" + (int)calidad + ")");
                Log("   excepción: " + ex.GetType().FullName + " - " + ex.Message);
                var sdk = ex as SDKException;
                if (sdk != null) Log("   ErrorCode: " + sdk.ErrorCode + " (" + (int)sdk.ErrorCode + ")");
                for (Exception interna = ex.InnerException; interna != null; interna = interna.InnerException)
                {
                    Log("   interna: " + interna.GetType().FullName + " - " + interna.Message);
                }
                throw;
            }
            if (calidad != CaptureFeedback.Good)
            {
                Log("Calidad insuficiente (" + calidad + "); vuelve a intentarlo.");
                return null;
            }
            return caracteristicas;
        }

        // ---------------------------------------------------------------- Registrar

        private void Registrar(Sample muestra)
        {
            // Nombre y dedo vienen de la copia que mantiene la interfaz; aquí no se tocan controles.
            string persona = _nombre;
            string dedo = _dedo;
            if (persona.Length == 0)
            {
                Log("Escribe el nombre de la persona antes de registrar.");
                MostrarResultado("Falta el nombre de la persona.", Color.Firebrick);
                return;
            }
            if (dedo.Length == 0)
            {
                Log("Elige el dedo.");
                return;
            }

            // Si cambian el nombre o el dedo a medias, el registro empieza de cero.
            string clave = persona + "|" + dedo;
            if (clave != _claveRegistro)
            {
                _registro.Clear(); // el mismo Enrollment de siempre, vacío
                _claveRegistro = clave;
            }

            FeatureSet caracteristicas = Extraer(muestra, DataPurpose.Enrollment);
            if (caracteristicas == null) return;

            _registro.AddFeatures(caracteristicas);
            Log("Registro de " + persona + " / " + dedo + ": faltan " + _registro.FeaturesNeeded + " captura(s).");

            switch (_registro.TemplateStatus)
            {
                case Enrollment.Status.Ready:
                    GuardarPlantilla(persona, dedo, _registro.Template); // copia los bytes antes del Clear
                    _registro.Clear();
                    _claveRegistro = null;
                    break;
                case Enrollment.Status.Failed:
                    Log("El registro falló; se reinicia. Vuelve a empezar con el mismo dedo.");
                    MostrarResultado("El registro falló. Vuelve a intentarlo.", Color.Firebrick);
                    _registro.Clear();
                    _claveRegistro = null;
                    break;
                default:
                    MostrarResultado("Registrando a " + persona + ": faltan " + _registro.FeaturesNeeded + " captura(s).",
                        SystemColors.ControlText);
                    break;
            }
            ActualizarFaltan();
        }

        private void GuardarPlantilla(string persona, string dedo, Template plantilla)
        {
            // Se guarda una copia propia (vía bytes) para no depender del objeto del registro.
            var copia = new Template();
            copia.DeSerialize(plantilla.Bytes);

            int existente = _plantillas.FindIndex(p => p.Persona == persona && p.Dedo == dedo);
            var nueva = new PlantillaGuardada(persona, dedo, copia);
            if (existente >= 0) _plantillas[existente] = nueva;
            else _plantillas.Add(nueva);
            RefrescarPlantillas();

            Log("Plantilla " + (existente >= 0 ? "reemplazada" : "guardada") + " en memoria: "
                + persona + " / " + dedo + " - " + copia.Size + " bytes.");
            MostrarResultado("Plantilla de " + persona + " (" + dedo + ") lista: " + copia.Size + " bytes.",
                Color.DarkGreen);
        }

        // ---------------------------------------------------------------- Identificar 1 a N

        private void Identificar(Sample muestra)
        {
            if (_plantillas.Count == 0)
            {
                Log("No hay plantillas en memoria. Registra al menos una.");
                MostrarResultado("No hay plantillas registradas.", Color.Firebrick);
                return;
            }

            FeatureSet caracteristicas = Extraer(muestra, DataPurpose.Verification);
            if (caracteristicas == null) return;

            int far = _far; // copia tomada de la pantalla
            _verificador.FARRequested = far;

            var coincidencias = new List<string>();
            Stopwatch reloj = Stopwatch.StartNew();
            foreach (PlantillaGuardada p in _plantillas)
            {
                var resultado = new ResultadoVerificacion();
                _verificador.Verify(caracteristicas, p.Plantilla, ref resultado);
                if (resultado.Verified)
                {
                    coincidencias.Add(p.Etiqueta + "  FARAchieved=" + resultado.FARAchieved
                                      + " " + Aproximado(resultado.FARAchieved));
                }
            }
            reloj.Stop();

            string tiempo = reloj.Elapsed.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture) + " ms";
            string resumen = "FAR pedido=" + far + " " + Aproximado(far) + ", " + _plantillas.Count
                             + " plantilla(s) comparadas, búsqueda: " + tiempo;

            if (coincidencias.Count == 0)
            {
                Log("Identificación: SIN COINCIDENCIA. " + resumen);
                MostrarResultado("Sin coincidencia\n" + resumen, Color.Firebrick);
                return;
            }

            Log("Identificación: " + coincidencias.Count + " coincidencia(s). " + resumen);
            foreach (string c in coincidencias) Log("   coincide: " + c);
            string titulo = coincidencias.Count == 1
                ? "Coincide: " + coincidencias[0]
                : "ATENCIÓN: " + coincidencias.Count + " coincidencias\n" + string.Join("\n", coincidencias);
            MostrarResultado(titulo + "\n" + resumen, coincidencias.Count == 1 ? Color.DarkGreen : Color.DarkOrange);
        }

        private static string Aproximado(int far)
        {
            if (far <= 0) return "";
            double uno = ProbabilityOne / (double)far;
            return "(~1 en " + uno.ToString("N0", CultureInfo.CurrentCulture) + ")";
        }

        // ---------------------------------------------------------------- Ida y vuelta

        private void IniciarIdaYVuelta()
        {
            // Hilo de la interfaz: lo compartido con el hilo del SDK se toca solo bajo el candado.
            int elegida = _lvPlantillas.SelectedIndices.Count > 0 ? _lvPlantillas.SelectedIndices[0] : 0;
            PlantillaGuardada origen = null;
            lock (_candado)
            {
                if (_plantillas.Count > 0) origen = _plantillas[Math.Min(elegida, _plantillas.Count - 1)];
            }
            if (origen == null)
            {
                Log("Ida y vuelta: no hay plantillas. Registra una primero.");
                MostrarResultado("Registra una plantilla antes de la prueba de ida y vuelta.", Color.Firebrick);
                return;
            }

            try
            {
                // plantilla -> bytes -> Base64 -> bytes -> plantilla nueva
                byte[] bytes = null;
                byte[] devuelto = origen.Plantilla.Serialize(ref bytes);
                if (bytes == null) bytes = devuelto;

                string base64 = Convert.ToBase64String(bytes);
                byte[] regreso = Convert.FromBase64String(base64);

                var nueva = new Template();
                nueva.DeSerialize(regreso);

                bool identicos = bytes.Length == regreso.Length && bytes.SequenceEqual(regreso)
                                 && nueva.Size == origen.Plantilla.Size;
                // Nunca se registra el Base64 ni los bytes: solo tamaños.
                Log("Ida y vuelta de " + origen.Etiqueta + ": " + bytes.Length + " bytes -> Base64 de "
                    + base64.Length + " caracteres -> " + regreso.Length + " bytes; idénticos: "
                    + (identicos ? "sí" : "NO"));

                lock (_candado)
                {
                    _idaVueltaOriginal = origen;
                    _idaVueltaNueva = nueva;
                }

                string aviso = "Pon el dedo registrado de " + origen.Persona + " (" + origen.Dedo + ") para comparar.";
                if (!CapturaActiva) aviso = "Inicia la captura y " + char.ToLowerInvariant(aviso[0]) + aviso.Substring(1);
                Log(aviso);
                MostrarResultado("Ida y vuelta lista (bytes idénticos: " + (identicos ? "sí" : "NO") + ").\n" + aviso,
                    identicos ? SystemColors.ControlText : Color.Firebrick);
            }
            catch (Exception ex)
            {
                lock (_candado)
                {
                    _idaVueltaOriginal = null;
                    _idaVueltaNueva = null;
                }
                LogError("hacer la prueba de ida y vuelta", ex);
            }
        }

        private void ProbarIdaYVuelta(Sample muestra)
        {
            FeatureSet caracteristicas = Extraer(muestra, DataPurpose.Verification);
            if (caracteristicas == null) return; // sigue pendiente: se reintenta con la siguiente captura

            _verificador.FARRequested = _far; // copia tomada de la pantalla

            var conNueva = new ResultadoVerificacion();
            _verificador.Verify(caracteristicas, _idaVueltaNueva, ref conNueva);
            var conOriginal = new ResultadoVerificacion();
            _verificador.Verify(caracteristicas, _idaVueltaOriginal.Plantilla, ref conOriginal);

            string detalle = "restaurada: " + (conNueva.Verified ? "coincide" : "no coincide")
                             + " (FARAchieved=" + conNueva.FARAchieved + "), original: "
                             + (conOriginal.Verified ? "coincide" : "no coincide")
                             + " (FARAchieved=" + conOriginal.FARAchieved + ")";
            Log("Ida y vuelta, " + detalle);

            if (conNueva.Verified && conOriginal.Verified)
            {
                MostrarResultado("IDA Y VUELTA CORRECTA: la plantilla restaurada aún coincide.\n" + detalle, Color.DarkGreen);
            }
            else if (conOriginal.Verified)
            {
                MostrarResultado("FALLÓ: coincide la original pero NO la restaurada.\n" + detalle, Color.Firebrick);
            }
            else
            {
                MostrarResultado("Inconcluso: la captura no coincide ni con la original (¿otro dedo?). "
                                 + "Pulsa el botón y vuelve a intentarlo.\n" + detalle, Color.DarkOrange);
            }

            _idaVueltaOriginal = null;
            _idaVueltaNueva = null;
        }
    }
}
