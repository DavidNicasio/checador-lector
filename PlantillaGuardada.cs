using DPFP;

namespace PruebaLector
{
    /// <summary>
    /// Una plantilla registrada. Vive solo en memoria: nunca se escribe en disco ni se muestra su
    /// contenido; la pantalla solo enseña su tamaño en bytes.
    /// </summary>
    internal sealed class PlantillaGuardada
    {
        public PlantillaGuardada(string persona, string dedo, Template plantilla)
        {
            Persona = persona;
            Dedo = dedo;
            Plantilla = plantilla;
        }

        public string Persona { get; private set; }
        public string Dedo { get; private set; }
        public Template Plantilla { get; private set; }

        public string Etiqueta
        {
            get { return Persona + " (" + Dedo + ")"; }
        }
    }
}
