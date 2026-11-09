using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ChecadorLector
{
    // Llamadas de administración (encabezado x-api-key). La llave solo vive en memoria mientras
    // el modo administrador está abierto: no se guarda en disco ni se escribe en ningún log.
    public class ApiAdmin : IDisposable
    {
        private readonly HttpClient _http;

        public ApiAdmin(Configuracion config, string llave)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            // Lanza FormatException si la llave trae caracteres que no caben en un encabezado
            _http.DefaultRequestHeaders.Add("x-api-key", llave.Trim());
        }

        // Empleados activos y sus dedos registrados (sin datos biométricos)
        public async Task<List<ResumenHuella>> ResumenAsync()
        {
            using (HttpResponseMessage respuesta = await _http.GetAsync("huellas/resumen"))
            {
                int codigo = (int)respuesta.StatusCode;
                if (codigo == 401 || codigo == 403)
                {
                    throw new ErrorApi("La llave de administración no es válida.", codigo);
                }
                if (!respuesta.IsSuccessStatusCode)
                {
                    throw new ErrorApi("La API respondió " + codigo + ".", codigo);
                }

                string cuerpo = await respuesta.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<ResumenHuella>>(cuerpo) ?? new List<ResumenHuella>();
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}