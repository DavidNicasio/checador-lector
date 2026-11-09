using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
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
        // La API limita esta consulta a 10 por minuto y cada descarga queda en su bitácora.
        public async Task<List<PlantillaRemota>> PlantillasAsync()
        {
            using (HttpResponseMessage respuesta = await _http.GetAsync("lector/plantillas"))
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
                RespuestaPlantillas datos = JsonConvert.DeserializeObject<RespuestaPlantillas>(cuerpo);
                return datos != null && datos.Plantillas != null
                    ? datos.Plantillas
                    : new List<PlantillaRemota>();
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}