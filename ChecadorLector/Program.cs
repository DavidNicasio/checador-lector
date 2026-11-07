using System;
using System.Linq;
using System.Windows.Forms;

namespace ChecadorLector
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            //con --kiosco abre a pantalla completa, sin bordes. sin la bandera abre en ventana para desarrollar.
            bool kiosco = args.Contains("--kiosco", StringComparer.OrdinalIgnoreCase);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new VentanaPrincipal(kiosco));
        }
    }
}