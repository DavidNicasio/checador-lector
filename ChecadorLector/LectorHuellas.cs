using System;
using System.Collections.Generic;
using System.Diagnostics;
using DPFP;
using DPFP.Error;
using DPFP.Processing;
using CaptureFeedback = DPFP.Capture.CaptureFeedback;
using Capturador = DPFP.Capture.Capture;

namespace ChecadorLector
{
    // Envuelve el SDK del lector U.are.U 4500: abre el lector, avisa lo que pasa y revisa la
    // calidad de cada captura. No toca la pantalla ni la red.
    //
    // OJO: los eventos se disparan en un hilo propio del SDK, NO en el de la ventana.
    // Quien se suscriba debe pasar a su hilo de interfaz (BeginInvoke) antes de tocar controles.
    // MuestraCapturada se dispara síncronamente dentro del callback del SDK, como en el ejemplo oficial.
    public sealed class LectorHuellas : DPFP.Capture.EventHandler, IDisposable
    {
        private readonly object _candado = new object();
        private readonly object _candadoExtractor = new object();
        private readonly FeatureExtraction _extractor = new FeatureExtraction();
        private Capturador _captura;

        public event Action LectorConectado;
        public event Action LectorDesconectado;
        public event Action DedoPuesto;
        public event Action DedoRetirado;
        public event Action<Sample> MuestraCapturada;

        private static readonly Dictionary<string, string> TextosCalidad = new Dictionary<string, string>
        {
            { "TooLight", "Presiona un poco más fuerte" },
            { "TooDark", "Presionaste muy fuerte o el dedo está muy húmedo" },
            { "TooNoisy", "La lectura salió borrosa; limpia el dedo y vuelve a intentarlo" },
            { "LowContrast", "Poco contraste; limpia el dedo y el lector" },
            { "NotEnoughFeatures", "No se alcanzan a ver bien las líneas de la huella; vuelve a ponerlo" },
            { "NotCentered", "Centra el dedo en el lector" },
            { "NoFinger", "No se detectó el dedo" },
            { "TooFast", "Mantén el dedo puesto un poco más" },
            { "TooSlow", "Pon el dedo y retíralo sin arrastrarlo" },
            { "TooSkewed", "Pon el dedo derecho, sin inclinarlo" },
            { "TooShort", "Deja el dedo puesto un poco más" },
            { "TooSmall", "Pon más superficie del dedo sobre el lector" },
        };

        public bool Activo
        {
            get
            {
                lock (_candado) return _captura != null;
            }
        }

        // Devuelve null si el lector quedó funcionando, o un mensaje listo para mostrar.
        public string Iniciar()
        {
            if (Activo) return null;

            Capturador nueva = null;
            try
            {
                var lectores = new DPFP.Capture.ReadersCollection();
                if (lectores.Count == 0)
                {
                    return "No se detecta el lector de huellas. Revisa el cable USB.";
                }

                nueva = new Capturador(DPFP.Capture.Priority.Normal);
                nueva.EventHandler = this;
                // Se registra ANTES de StartCapture: si la primera muestra llega enseguida,
                // el callback ya debe ver la captura como activa.
                lock (_candado) _captura = nueva;
                nueva.StartCapture();
                return null;
            }
            catch (Exception ex)
            {
                lock (_candado)
                {
                    if (ReferenceEquals(_captura, nueva)) _captura = null;
                }
                if (nueva != null)
                {
                    try { nueva.Dispose(); } catch (Exception) { }
                }
                return "No se pudo iniciar el lector: " + Describir(ex);
            }
        }

        public void Detener()
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
            try { actual.StopCapture(); } catch (Exception ex) { Debug.WriteLine("StopCapture: " + ex.Message); }
            try { actual.Dispose(); } catch (Exception ex) { Debug.WriteLine("Dispose: " + ex.Message); }
        }

        public void Dispose()
        {
            Detener();
        }

        // Saca las características de una muestra. Devuelve null y deja en "problema" un mensaje
        // para el usuario si la calidad no alcanza. Lanza excepción si el SDK falla.
        public FeatureSet Extraer(Sample muestra, DataPurpose proposito, out string problema)
        {
            // Como en el ejemplo oficial: FeatureSet NUEVO en cada llamada y feedback inicial None.
            CaptureFeedback calidad = CaptureFeedback.None;
            FeatureSet caracteristicas = new FeatureSet();
            lock (_candadoExtractor)
            {
                _extractor.CreateFeatureSet(muestra, proposito, ref calidad, ref caracteristicas);
            }

            if (calidad != CaptureFeedback.Good)
            {
                Debug.WriteLine("Calidad de la muestra: " + calidad);
                string texto;
                problema = TextosCalidad.TryGetValue(calidad.ToString(), out texto)
                    ? texto
                    : "La lectura no fue buena (" + calidad + "); vuelve a intentarlo";
                return null;
            }

            problema = null;
            return caracteristicas;
        }

        // Para probar la lectura: null = buena, o el mensaje de qué corregir.
        public string Evaluar(Sample muestra)
        {
            try
            {
                string problema;
                Extraer(muestra, DataPurpose.Verification, out problema);
                return problema;
            }
            catch (Exception ex)
            {
                return "Error del lector: " + Describir(ex);
            }
        }

        private static string Describir(Exception ex)
        {
            var sdk = ex as SDKException;
            return sdk != null
                ? "error " + (int)sdk.ErrorCode + " del SDK (" + ex.Message + ")"
                : ex.Message;
        }

        // Una excepción que escape de un callback del SDK podría tirar el proceso.
        private static void Avisar(Action accion)
        {
            if (accion == null) return;
            try { accion(); }
            catch (Exception ex) { Debug.WriteLine("Error en un suscriptor del lector: " + ex.Message); }
        }

        // ---- Eventos del SDK (llegan en hilos del SDK) ----

        public void OnReaderConnect(object captura, string serie)
        {
            Avisar(LectorConectado);
        }

        public void OnReaderDisconnect(object captura, string serie)
        {
            Avisar(LectorDesconectado);
        }

        public void OnFingerTouch(object captura, string serie)
        {
            Avisar(DedoPuesto);
        }

        public void OnFingerGone(object captura, string serie)
        {
            Avisar(DedoRetirado);
        }

        public void OnSampleQuality(object captura, string serie, CaptureFeedback calidad)
        {
            Debug.WriteLine("OnSampleQuality: " + calidad);
        }

        public void OnComplete(object captura, string serie, Sample muestra)
        {
            lock (_candado)
            {
                if (_captura == null) return; // llegó después de detener
            }
            Action<Sample> manejador = MuestraCapturada;
            if (manejador != null) Avisar(() => manejador(muestra));
        }
    }
}