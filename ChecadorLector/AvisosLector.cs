using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ChecadorLector
{
    // Un aviso del lector que todavía no se manda a la API
    public class AvisoPendiente
    {
        public string Tipo { get; set; }       // INICIO, NO_RECONOCIDA, COINCIDENCIA_MULTIPLE, ERROR_LECTOR u OTRO
        public string Detalle { get; set; }
        public DateTimeOffset FechaHora { get; set; }
    }

    // Avisos en memoria: se mandan a la API cuando hay conexión. Nunca llevan datos biométricos.
    // Se llama desde el hilo del SDK y desde el de la interfaz, por eso todo va con candado.
    public sealed class AvisosLector
    {
        private const int Maximo = 50;
        private static readonly TimeSpan VentanaAgrupar = TimeSpan.FromMinutes(5);

        private readonly object _candado = new object();
        private readonly List<AvisoPendiente> _avisos = new List<AvisoPendiente>();

        // Intentos sin reconocer: se cuentan y se mandan juntos en un solo aviso
        private int _noReconocidas;
        private DateTimeOffset _primera;
        private DateTimeOffset _ultima;

        // Agrega un aviso. Si ya hay uno igual esperando a enviarse, no se repite.
        public void Agregar(string tipo, string detalle, DateTimeOffset cuando)
        {
            lock (_candado)
            {
                if (_avisos.Any(a => a.Tipo == tipo && a.Detalle == detalle)) return;

                _avisos.Add(new AvisoPendiente
                {
                    Tipo = tipo,
                    Detalle = Recortar(detalle),
                    FechaHora = cuando
                });
                Acotar();
            }
        }

        // Cuenta un intento que no se pudo reconocer
        public void NoReconocida(DateTimeOffset cuando)
        {
            lock (_candado)
            {
                if (_noReconocidas == 0) _primera = cuando;
                _ultima = cuando;
                _noReconocidas++;
            }
        }

        // Saca todo lo que toca enviar. Los intentos sin reconocer salen hasta que pasan 5 minutos
        // desde el primero. Si el envío falla, se regresan con Devolver.
        public List<AvisoPendiente> Tomar(DateTimeOffset ahora)
        {
            lock (_candado)
            {
                if (_noReconocidas > 0 && ahora - _primera >= VentanaAgrupar)
                {
                    _avisos.Add(new AvisoPendiente
                    {
                        Tipo = "NO_RECONOCIDA",
                        Detalle = Recortar(DetalleNoReconocidas()),
                        FechaHora = _primera
                    });
                    _noReconocidas = 0;
                    Acotar();
                }

                List<AvisoPendiente> salida = _avisos.ToList();
                _avisos.Clear();
                return salida;
            }
        }

        // Regresa los avisos que no se pudieron enviar (quedan al inicio de la fila)
        public void Devolver(IEnumerable<AvisoPendiente> avisos)
        {
            List<AvisoPendiente> lista = avisos.ToList();
            lock (_candado)
            {
                _avisos.InsertRange(0, lista);
                Acotar();
            }
        }

        private string DetalleNoReconocidas()
        {
            string desde = _primera.ToString("HH:mm", CultureInfo.InvariantCulture);
            string hasta = _ultima.ToString("HH:mm", CultureInfo.InvariantCulture);

            if (_noReconocidas == 1) return "1 intento sin reconocer a las " + desde;
            return _noReconocidas + " intentos sin reconocer entre " + desde + " y " + hasta;
        }

        // Si se acumulan demasiados, se descartan los más viejos
        private void Acotar()
        {
            while (_avisos.Count > Maximo) _avisos.RemoveAt(0);
        }

        // La API acepta máximo 500 caracteres
        private static string Recortar(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return texto;
            return texto.Length <= 500 ? texto : texto.Substring(0, 500);
        }
    }
}