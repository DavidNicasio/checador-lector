using System;
using System.Collections.Generic;

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

    // Un empleado activo y los dedos que ya tiene registrados (GET /huellas/resumen)
    public class ResumenHuella
    {
        public int EmpleadoId { get; set; }
        public string NumeroEmpleado { get; set; }
        public string Nombre { get; set; }
        public List<int> Dedos { get; set; } = new List<int>();
    }

    // Una plantilla descargada de GET /lector/plantillas (Plantilla va en base64)
    public class PlantillaRemota
    {
        public int EmpleadoId { get; set; }
        public string NumeroEmpleado { get; set; }
        public string Nombre { get; set; }
        public int Dedo { get; set; }
        public string Plantilla { get; set; }
    }

    public class RespuestaPlantillas
    {
        public string Version { get; set; }
        public bool SinCambios { get; set; }
        public List<PlantillaRemota> Plantillas { get; set; } = new List<PlantillaRemota>();
    }

    // ---- Respuesta de POST /lector/checadas ----

    public class DatosChecada
    {
        public int Id { get; set; }
        public string FechaHoraUtc { get; set; }
        public string FechaHoraLocal { get; set; }
    }

    public class DatosEmpleado
    {
        public int Id { get; set; }
        public string NumeroEmpleado { get; set; }
        public string Nombre { get; set; }
    }

    // Estado: REGISTRADA, DUPLICADA, REBOTE o RECHAZADA (con Motivo y Reintentable)
    public class ResultadoChecada
    {
        public string IdLocal { get; set; }
        public string Estado { get; set; }
        public string Motivo { get; set; }
        public bool? Reintentable { get; set; }
        public DatosChecada Checada { get; set; }
        public DatosEmpleado Empleado { get; set; }
        public int? NumeroDelDia { get; set; }
        public int? EsperadasDelDia { get; set; }
    }

    // Respuesta de POST /lector/checadas/lote: un resultado por checada
    public class RespuestaLote
    {
        public List<ResultadoChecada> Resultados { get; set; } = new List<ResultadoChecada>();
    }

    // Mensaje que manda la pantalla (HTML) al programa
    public class MensajeUi
    {
        public string Accion { get; set; }
        public string Llave { get; set; }
        public int EmpleadoId { get; set; }
        public int Dedo { get; set; }
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