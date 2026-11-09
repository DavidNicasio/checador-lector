using System;
using System.Collections.Generic;
using System.Diagnostics;
using DPFP;
using DPFP.Processing;
using Verificador = DPFP.Verification.Verification;
using ResultadoVerificacion = DPFP.Verification.Verification.Result;

namespace ChecadorLector
{
    public enum EstadoCaptura
    {
        Progreso,    // captura buena, faltan más
        Listo,       // plantilla completa
        Reintentar,  // esta captura no sirvió, se vuelve a intentar
        Duplicada    // la huella ya pertenece a otro registro: se cancela
    }

    public class ResultadoCaptura
    {
        public EstadoCaptura Estado { get; set; }
        public string Mensaje { get; set; }
        public int Faltan { get; set; }
        public int EmpleadoId { get; set; }
        public int Dedo { get; set; }
        public byte[] Plantilla { get; set; }
    }

    // Arma la plantilla de un dedo con varias capturas (Enrollment del SDK) y, en cada captura,
    // comprueba que la huella no esté ya registrada para otro empleado u otro dedo.
    // Los objetos del SDK se crean UNA vez (esta clase se instancia antes de abrir el lector).
    // Procesar corre en el hilo del SDK; Iniciar y Cancelar en el de la interfaz: todo bajo candado.
    public sealed class RegistroHuella
    {
        private const int ProbabilityOne = 0x7FFFFFFF;
        private const int FarDuplicado = ProbabilityOne / 100000; // el mismo 1 en 100 000 de la identificación

        public static readonly string[] NombresDedo =
        {
            "Pulgar derecho", "Índice derecho", "Medio derecho", "Anular derecho", "Meñique derecho",
            "Pulgar izquierdo", "Índice izquierdo", "Medio izquierdo", "Anular izquierdo", "Meñique izquierdo",
        };

        private sealed class Existente
        {
            public int EmpleadoId;
            public string Numero;
            public string Nombre;
            public int Dedo;
            public Template Plantilla;
        }

        private readonly object _candado = new object();
        private readonly Enrollment _enrollment = new Enrollment();
        private readonly Verificador _verificador = new Verificador();
        private List<Existente> _existentes = new List<Existente>();
        private bool _activo;
        private int _empleadoId;
        private int _dedo;
        private int _comprobadas;

        public bool Activo
        {
            get
            {
                lock (_candado) return _activo;
            }
        }

        // Empieza el registro de un dedo. "remotas" son las plantillas ya guardadas en la API.
        // Devuelve cuántas capturas se necesitan.
        public int Iniciar(int empleadoId, int dedo, IEnumerable<PlantillaRemota> remotas)
        {
            var lista = new List<Existente>();
            foreach (PlantillaRemota p in remotas)
            {
                // La del mismo empleado y dedo es la que se va a reemplazar: no cuenta como duplicada
                if (p.EmpleadoId == empleadoId && p.Dedo == dedo) continue;
                try
                {
                    var plantilla = new Template();
                    plantilla.DeSerialize(Convert.FromBase64String(p.Plantilla));
                    lista.Add(new Existente
                    {
                        EmpleadoId = p.EmpleadoId,
                        Numero = p.NumeroEmpleado,
                        Nombre = p.Nombre,
                        Dedo = p.Dedo,
                        Plantilla = plantilla
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Plantilla guardada que no se pudo leer: " + ex.Message);
                }
            }

            lock (_candado)
            {
                _enrollment.Clear();
                _existentes = lista;
                _empleadoId = empleadoId;
                _dedo = dedo;
                _comprobadas = 0;
                _activo = true;
                return (int)_enrollment.FeaturesNeeded; // el SDK lo da como uint
            }
        }

        public void Cancelar()
        {
            lock (_candado)
            {
                _activo = false;
                _enrollment.Clear();
                _existentes = new List<Existente>();
            }
        }

        // Procesa una captura. Devuelve null si no hay registro en curso.
        public ResultadoCaptura Procesar(LectorHuellas lector, Sample muestra)
        {
            lock (_candado)
            {
                if (!_activo) return null;

                string problema;
                FeatureSet paraRegistro = lector.Extraer(muestra, DataPurpose.Enrollment, out problema);
                if (paraRegistro == null)
                {
                    return Resultado(EstadoCaptura.Reintentar, problema);
                }

                // Comparar esta captura con las huellas ya registradas
                string problemaComparar;
                FeatureSet paraComparar = lector.Extraer(muestra, DataPurpose.Verification, out problemaComparar);
                if (paraComparar != null)
                {
                    _comprobadas++;
                    Existente igual = BuscarIgual(paraComparar);
                    if (igual != null)
                    {
                        _activo = false;
                        _enrollment.Clear();
                        return Resultado(EstadoCaptura.Duplicada, TextoDuplicada(igual));
                    }
                }

                _enrollment.AddFeatures(paraRegistro);

                switch (_enrollment.TemplateStatus)
                {
                    case Enrollment.Status.Ready:
                        if (_comprobadas == 0)
                        {
                            // Nunca se pudo comprobar que la huella fuera nueva: no se acepta a ciegas
                            _enrollment.Clear();
                            return Resultado(EstadoCaptura.Reintentar,
                                "No se pudo comprobar que la huella sea nueva. Vuelve a empezar este dedo.");
                        }
                        byte[] bytes = (byte[])_enrollment.Template.Bytes.Clone(); // copia antes del Clear
                        _activo = false;
                        _enrollment.Clear();
                        return new ResultadoCaptura
                        {
                            Estado = EstadoCaptura.Listo,
                            Mensaje = "Plantilla completa",
                            Faltan = 0,
                            EmpleadoId = _empleadoId,
                            Dedo = _dedo,
                            Plantilla = bytes
                        };

                    case Enrollment.Status.Failed:
                        _enrollment.Clear();
                        _comprobadas = 0;
                        return Resultado(EstadoCaptura.Reintentar,
                            "El registro falló. Vuelve a empezar con el mismo dedo.");

                    default:
                        return Resultado(EstadoCaptura.Progreso, "Captura correcta");
                }
            }
        }

        private ResultadoCaptura Resultado(EstadoCaptura estado, string mensaje)
        {
            return new ResultadoCaptura
            {
                Estado = estado,
                Mensaje = mensaje,
                Faltan = (int)_enrollment.FeaturesNeeded // el SDK lo da como uint
            };
        }

        private Existente BuscarIgual(FeatureSet caracteristicas)
        {
            _verificador.FARRequested = FarDuplicado;
            foreach (Existente e in _existentes)
            {
                var resultado = new ResultadoVerificacion();
                _verificador.Verify(caracteristicas, e.Plantilla, ref resultado);
                if (resultado.Verified) return e;
            }
            return null;
        }

        private string TextoDuplicada(Existente e)
        {
            string dedo = e.Dedo >= 1 && e.Dedo <= 10
                ? NombresDedo[e.Dedo - 1].ToLowerInvariant()
                : "dedo " + e.Dedo;

            if (e.EmpleadoId == _empleadoId)
            {
                return "Esta huella ya está registrada como " + dedo + " de este mismo empleado. Usa otro dedo.";
            }
            return "Esta huella ya está registrada para " + e.Nombre + " (" + e.Numero
                   + "). Cada huella debe ser de una sola persona.";
        }
    }
}