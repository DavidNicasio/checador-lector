using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ChecadorLector
{
    public class ApiCliente : IDisposable
    {
        private readonly HttpClient _http;

        public ApiCliente(Configuracion config)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/"),
                // Tope general; cada llamada pone además su propio límite más corto (ver abajo)
                Timeout = TimeSpan.FromSeconds(60)
            };
            _http.DefaultRequestHeaders.Add("x-lector-key", config.LlaveLector);
        }

        // Fecha ISO 8601 con zona, como la pide la API (por ejemplo 2026-11-09T08:02:00.000-06:00)
        public static string Iso(DateTimeOffset fecha)
        {
            return fecha.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }

        public async Task<ResultadoEstado> EstadoAsync()
        {
            DateTime antes = DateTime.UtcNow;
            using (var limite = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (HttpResponseMessage respuesta = await _http.GetAsync("lector/estado", limite.Token))
            {
                DateTime despues = DateTime.UtcNow;

                if ((int)respuesta.StatusCode == 401)
                {
                    throw new ErrorApi("La llave del lector no es válida o está desactivada.", 401);
                }
                if (!respuesta.IsSuccessStatusCode)
                {
                    throw new ErrorApi("La API respondió " + (int)respuesta.StatusCode + ".", (int)respuesta.StatusCode);
                }

                string cuerpo = await respuesta.Content.ReadAsStringAsync();

                // DateParseHandling.None: las horas se quedan como texto y las leemos nosotros
                var ajustes = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
                EstadoLector estado = JsonConvert.DeserializeObject<EstadoLector>(cuerpo, ajustes);

                // El servidor contestó a medio camino entre el envío y la respuesta
                DateTime mitad = antes + TimeSpan.FromTicks((despues - antes).Ticks / 2);
                DateTime horaServidor = DateTimeOffset.Parse(
                    estado.HoraServidorUtc, CultureInfo.InvariantCulture).UtcDateTime;

                return new ResultadoEstado { Estado = estado, Desfase = horaServidor - mitad };
            }
        }

        // Plantillas de los empleados activos, para comparar en local. Quedan solo en memoria.
        // Si se manda la versión que ya se tiene y no cambió, la API responde SinCambios sin plantillas.
        // La API limita esta consulta a 10 por minuto y cada descarga real queda en su bitácora.
        public async Task<RespuestaPlantillas> PlantillasAsync(string version)
        {
            string ruta = string.IsNullOrEmpty(version)
                ? "lector/plantillas"
                : "lector/plantillas?version=" + Uri.EscapeDataString(version);

            using (var limite = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (HttpResponseMessage respuesta = await _http.GetAsync(ruta, limite.Token))
            {
                int codigo = (int)respuesta.StatusCode;
                if (codigo == 401)
                {
                    throw new ErrorApi("La llave del lector no es válida o está desactivada.", 401);
                }
                if (codigo == 429)
                {
                    throw new ErrorApi("Demasiadas consultas seguidas. Espera un minuto e inténtalo de nuevo.", 429);
                }
                if (!respuesta.IsSuccessStatusCode)
                {
                    throw new ErrorApi("La API respondió " + codigo + ".", codigo);
                }

                string cuerpo = await respuesta.Content.ReadAsStringAsync();
                RespuestaPlantillas datos = JsonConvert.DeserializeObject<RespuestaPlantillas>(cuerpo)
                                            ?? new RespuestaPlantillas();
                if (datos.Plantillas == null) datos.Plantillas = new List<PlantillaRemota>();
                return datos;
            }
        }

        // Todas las plantillas, sin comparar versión (para el modo de registro)
        public async Task<List<PlantillaRemota>> PlantillasAsync()
        {
            RespuestaPlantillas datos = await PlantillasAsync(null);
            return datos.Plantillas;
        }

        // Manda una checada. fechaHora = cuándo se capturó; relojLector = la hora del reloj al enviar.
        // Espera como máximo 5 s: si no hay respuesta, el llamador la guarda en la cola.
        // Si la API alcanzó a guardarla y solo se perdió la respuesta, el reintento con el mismo
        // idLocal vuelve como DUPLICADA y no se cuenta dos veces.
        public async Task<ResultadoChecada> EnviarChecadaAsync(
            Guid idLocal, int empleadoId, DateTimeOffset fechaHora, DateTimeOffset relojLector)
        {
            string json = JsonConvert.SerializeObject(new
            {
                idLocal = idLocal.ToString(),
                empleadoId = empleadoId,
                fechaHora = Iso(fechaHora),
                relojLector = Iso(relojLector)
            });

            using (var limite = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var contenido = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage respuesta = await _http.PostAsync("lector/checadas", contenido, limite.Token))
            {
                int codigo = (int)respuesta.StatusCode;
                if (codigo == 401)
                {
                    throw new ErrorApi("La llave del lector no es válida o está desactivada.", 401);
                }
                if (codigo == 429)
                {
                    throw new ErrorApi("Demasiadas checadas seguidas. Espera un momento.", 429);
                }
                if (!respuesta.IsSuccessStatusCode)
                {
                    throw new ErrorApi("La API no aceptó la checada (código " + codigo + ").", codigo);
                }

                string cuerpo = await respuesta.Content.ReadAsStringAsync();
                var ajustes = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
                ResultadoChecada resultado = JsonConvert.DeserializeObject<ResultadoChecada>(cuerpo, ajustes);
                if (resultado == null) throw new ErrorApi("La API contestó algo que no se entiende.", codigo);
                return resultado;
            }
        }

        // Manda varias checadas guardadas (de 1 a 200), en orden cronológico
        public async Task<RespuestaLote> EnviarLoteAsync(IEnumerable<ChecadaPendiente> checadas, DateTimeOffset relojLector)
        {
            string json = JsonConvert.SerializeObject(new
            {
                relojLector = Iso(relojLector),
                checadas = checadas.Select(c => new
                {
                    idLocal = c.IdLocal,
                    empleadoId = c.EmpleadoId,
                    fechaHora = c.FechaHora
                }).ToList()
            });

            using (var limite = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var contenido = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage respuesta = await _http.PostAsync("lector/checadas/lote", contenido, limite.Token))
            {
                int codigo = (int)respuesta.StatusCode;
                if (codigo == 401)
                {
                    throw new ErrorApi("La llave del lector no es válida o está desactivada.", 401);
                }
                if (codigo == 429)
                {
                    throw new ErrorApi("Demasiados envíos seguidos. Se reintentará más tarde.", 429);
                }
                if (!respuesta.IsSuccessStatusCode)
                {
                    throw new ErrorApi("La API no aceptó el envío (código " + codigo + ").", codigo);
                }

                string cuerpo = await respuesta.Content.ReadAsStringAsync();
                var ajustes = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
                RespuestaLote lote = JsonConvert.DeserializeObject<RespuestaLote>(cuerpo, ajustes);
                if (lote == null || lote.Resultados == null) throw new ErrorApi("La API contestó algo que no se entiende.", codigo);
                return lote;
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}