using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
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
                Timeout = TimeSpan.FromSeconds(10)
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
            using (HttpResponseMessage respuesta = await _http.GetAsync("lector/estado"))
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

            using (HttpResponseMessage respuesta = await _http.GetAsync(ruta))
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
        // Si la API no se alcanza lanza una excepción de red (el llamador decide qué hacer).
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

            using (var contenido = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage respuesta = await _http.PostAsync("lector/checadas", contenido))
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

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}