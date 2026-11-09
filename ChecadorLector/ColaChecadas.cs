using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ChecadorLector
{
    // Una checada que ya se capturó pero todavía no llega a la API
    public class ChecadaPendiente
    {
        public string IdLocal { get; set; }
        public int EmpleadoId { get; set; }
        public string FechaHora { get; set; }   // ISO 8601 con zona, la hora en que se capturó
    }

    // Cola de checadas pendientes de enviar, guardada en %LocalAppData%\ChecadorLector\cola.json
    // (fuera del repositorio). Solo lleva número de empleado, hora e identificador: nunca huellas.
    // Cada cambio se escribe a disco enseguida, así una falla de luz o un cierre no pierden checadas.
    public sealed class ColaChecadas
    {
        private readonly object _candado = new object();
        private readonly string _ruta;
        private readonly List<ChecadaPendiente> _items = new List<ChecadaPendiente>();
        private string _error;

        // true si el archivo anterior estaba dañado: se apartó como cola.danada-fecha.json
        public bool ArchivoDanado { get; private set; }

        public ColaChecadas()
        {
            _ruta = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChecadorLector", "cola.json");
            Cargar();
        }

        public int Cantidad
        {
            get
            {
                lock (_candado) return _items.Count;
            }
        }

        // La hora de la checada pendiente más vieja (null si no hay)
        public DateTimeOffset? MasAntigua()
        {
            lock (_candado)
            {
                DateTimeOffset? masVieja = null;
                foreach (ChecadaPendiente c in _items)
                {
                    DateTimeOffset t;
                    if (DateTimeOffset.TryParse(c.FechaHora, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)
                        && (masVieja == null || t < masVieja.Value))
                    {
                        masVieja = t;
                    }
                }
                return masVieja;
            }
        }

        // Lanza excepción si no se pudo guardar en disco (entonces la checada NO quedó en la cola)
        public void Agregar(ChecadaPendiente checada)
        {
            lock (_candado)
            {
                if (_error != null) throw new InvalidOperationException(_error);

                _items.Add(checada);
                try
                {
                    Guardar();
                }
                catch (Exception)
                {
                    _items.Remove(checada);
                    throw;
                }
            }
        }

        // Hasta "max" checadas, las más viejas primero (copias: no se modifican las de la cola)
        public List<ChecadaPendiente> Primeras(int max)
        {
            lock (_candado)
            {
                return _items.Take(max).Select(c => new ChecadaPendiente
                {
                    IdLocal = c.IdLocal,
                    EmpleadoId = c.EmpleadoId,
                    FechaHora = c.FechaHora
                }).ToList();
            }
        }

        // Quita las que la API ya resolvió
        public void Quitar(ICollection<string> idsLocales)
        {
            lock (_candado)
            {
                if (_items.RemoveAll(c => idsLocales.Contains(c.IdLocal)) > 0)
                {
                    Guardar();
                }
            }
        }

        private void Cargar()
        {
            try
            {
                if (!File.Exists(_ruta)) return;

                string texto = File.ReadAllText(_ruta);
                List<ChecadaPendiente> leidas;
                try
                {
                    leidas = JsonConvert.DeserializeObject<List<ChecadaPendiente>>(texto);
                }
                catch (JsonException)
                {
                    Apartar();
                    return;
                }

                if (leidas != null)
                {
                    _items.AddRange(leidas.Where(c => c != null && !string.IsNullOrEmpty(c.IdLocal)));
                }
            }
            catch (Exception ex)
            {
                // No se pudo ni leer el archivo: no se sobrescribe a ciegas
                _error = "No se pudo leer la cola de checadas: " + ex.Message;
            }
        }

        // Aparta un archivo ilegible para no perderlo y empezar con una cola limpia
        private void Apartar()
        {
            ArchivoDanado = true;
            try
            {
                string nombre = "cola.danada-"
                                + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
                File.Move(_ruta, Path.Combine(Path.GetDirectoryName(_ruta), nombre));
            }
            catch (Exception ex)
            {
                _error = "No se pudo apartar la cola dañada: " + ex.Message;
            }
        }

        // Escribe en un archivo temporal y luego lo cambia por el bueno: nunca queda a medias
        private void Guardar()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ruta));
            string temporal = _ruta + ".tmp";
            File.WriteAllText(temporal, JsonConvert.SerializeObject(_items, Formatting.Indented));
            if (File.Exists(_ruta)) File.Replace(temporal, _ruta, null);
            else File.Move(temporal, _ruta);
        }
    }
}