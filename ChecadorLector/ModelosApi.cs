using System;

namespace ChecadorLector
{
    public class LectorInfo
    {
        public int Id { get; set; }
        public string Nombre { get; set; }
    }

    public class SucursalInfo
    {
        public int Id { get; set; }
        public string Nombre { get; set; }
        public bool Activa { get; set; }
    }

    public class ConfiguracionServidor
    {
        public int ToleranciaRelojMin { get; set; }
        public int ReboteSeg { get; set; }
    }

    // Respuesta de GET /lector/estado
    public class EstadoLector
    {
        public LectorInfo Lector { get; set; }
        public SucursalInfo Sucursal { get; set; }
        public string HoraServidorUtc { get; set; }
        public string HoraServidorLocal { get; set; }
        public ConfiguracionServidor Configuracion { get; set; }
    }

    public class ResultadoEstado
    {
        public EstadoLector Estado { get; set; }

        // Hora del servidor menos hora de esta PC. Positivo = la PC va atrasada.
        public TimeSpan Desfase { get; set; }
    }

    // La API contestó, pero con un error (por ejemplo 401: llave inválida)
    public class ErrorApi : Exception
    {
        public int Codigo { get; private set; }

        public ErrorApi(string mensaje, int codigo) : base(mensaje)
        {
            Codigo = codigo;
        }
    }
}