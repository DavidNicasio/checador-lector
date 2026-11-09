using System;
using System.Collections.Generic;
using System.Diagnostics;
using DPFP;
using DPFP.Processing;
using Verificador = DPFP.Verification.Verification;
using ResultadoVerificacion = DPFP.Verification.Verification.Result;

namespace ChecadorLector
{
    public enum EstadoIdentificacion
    {
        SinPlantillas, //todavia no hay huellas cargadas
        MalaLectura, //la captura no tuvo calidad
        NoReconocida, //no coincide con nadie
        Reconocida, //coincide con exactamente un empleado
        Multiple //coincide con mas de un empleado: no se acepta
    }
    public class Identificacion
    {
        public EstadoIdentificacion Estado { get; set; }
        public string Mensaje { get; set; } //que corregir, solo con MalaLectura
        public int EmpleadoId { get; set; }
        public string Numero { get; set; }
        public string Nombre { get; set; }
    }

    //Guarda en MEMORIA las plantillas de los empleados activos y compara una captura contra todas (1 a N).
    //Nunca escribe plantillas a disco, Identificar corre en el hilo del SDK; Cargar, en el de la interfaz.
    public sealed class Identificador
    {
        private const int ProbabilityOne = 0x7FFFFFFF;
        private const int FarIdentificacion = ProbabilityOne / 100000; // 1 falsa aceptacion en 100 000 

        private sealed class Item
        {
            public int EmpleadoId;
            public string Numero;
            public string Nombre;
            public Template Plantilla;
        }

        private readonly object _candado = new object();
        private readonly Verificador _verificador = new Verificador();
        private List<Item> _items = new List<Item>();
        private string _version = "";

        public int Total
        {
            get
            {
                lock (_candado) return _items.Count;
            }
        }
        public string Version
        {
            get
            {
                lock (_candado) return _version;
            }
        }

        //Reemplaza todo lo que habia por lo que mando a la api
        public void Cargar(IEnumerable<PlantillaRemota> remotas, string version)
        {
            var lista = new List<Item>();
            foreach (PlantillaRemota p in remotas)
            {
                try
                {
                    var plantilla = new Template();
                    plantilla.DeSerialize(Convert.FromBase64String(p.Plantilla));
                    lista.Add(new Item
                    {
                        EmpleadoId = p.EmpleadoId,
                        Numero = p.NumeroEmpleado,
                        Nombre = p.Nombre,
                        Plantilla = plantilla
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Plantilla que no se pudo leer: "+ ex.Message);
                }
            }
            lock (_candado)
            {
                _items = lista;
                _version = version ?? "";
            }
        }
        //compara una captura contra todas las plantiallas cargadas
        public Identificacion Identificar(LectorHuellas lector, Sample muestra)
        {
            if (Total == 0)
            {
                return new Identificacion { Estado = EstadoIdentificacion.SinPlantillas };
            }

            string problema;
            FeatureSet caracteristicas = lector.Extraer(muestra, DataPurpose.Verification, out problema);
            if (caracteristicas == null)
            {
                return new Identificacion { Estado = EstadoIdentificacion.MalaLectura, Mensaje = problema };
            }
            lock (_candado)
            {
                _verificador.FARRequested = FarIdentificacion;

                //un mismo empleado puede coincidir con dos de sus dedos;
                var coincidencias = new Dictionary<int, Item>();
                foreach (Item i in _items)
                {
                    var resultado = new ResultadoVerificacion();
                    _verificador.Verify(caracteristicas, i.Plantilla, ref resultado);
                    if (resultado.Verified && !coincidencias.ContainsKey(i.EmpleadoId))
                    {
                        coincidencias.Add(i.EmpleadoId, i);
                    }
                }
                if (coincidencias.Count == 0)
                {
                    return new Identificacion { Estado = EstadoIdentificacion.NoReconocida };
                }
                if (coincidencias.Count > 1)
                {
                    return new Identificacion { Estado = EstadoIdentificacion.Multiple };
                }
                foreach (Item unica in coincidencias.Values)
                {
                    return new Identificacion
                    {
                        Estado = EstadoIdentificacion.Reconocida,
                        EmpleadoId = unica.EmpleadoId,
                        Numero = unica.Numero,
                        Nombre = unica.Nombre
                    };
                }
                return new Identificacion { Estado = EstadoIdentificacion.NoReconocida };
            }
        }
    }
}