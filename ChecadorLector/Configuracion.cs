using System;
using System.IO;
using Newtonsoft.Json;

namespace ChecadorLector
{
    // Configuración local de este equipo. Vive en %LocalAppData%\ChecadorLector\config.json,
    // fuera del repositorio, porque lleva la llave del lector.
    public class Configuracion
    {
        public string ApiUrl { get; set; } = "";
        public string LlaveLector { get; set; } = "";

        public static string Ruta
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChecadorLector", "config.json");
            }
        }

        // Si el archivo no existe, deja una plantilla para llenar.
        public static Configuracion Cargar()
        {
            if (!File.Exists(Ruta))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Ruta));
                var plantilla = new Configuracion { ApiUrl = "http://localhost:3000/api/v1" };
                File.WriteAllText(Ruta, JsonConvert.SerializeObject(plantilla, Formatting.Indented));
                return plantilla;
            }
            return JsonConvert.DeserializeObject<Configuracion>(File.ReadAllText(Ruta)) ?? new Configuracion();
        }

        public bool EsValida(out string problema)
        {
            if (string.IsNullOrWhiteSpace(LlaveLector))
            {
                problema = "Falta la llave del lector. Edita " + Ruta;
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out uri))
            {
                problema = "ApiUrl no es una dirección válida. Edita " + Ruta;
                return false;
            }

            // Las huellas y las llaves solo viajan por HTTPS; http se permite únicamente en esta misma PC.
            bool https = uri.Scheme == Uri.UriSchemeHttps;
            bool httpLocal = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
            if (!https && !httpLocal)
            {
                problema = "La API debe usar HTTPS (http solo se permite con localhost).";
                return false;
            }

            problema = null;
            return true;
        }
    }
}