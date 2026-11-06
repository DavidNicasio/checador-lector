using System;
using System.Windows.Forms;

namespace PruebaLector
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Si una DLL del SDK no carga (BadImageFormat, FileLoad...), se muestra el detalle
            // en lugar de cerrar la ventana sin explicación.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Mostrar("Error no controlado en la interfaz", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Mostrar("Error no controlado", e.ExceptionObject as Exception);

            try
            {
                Application.Run(new FormPrincipal());
            }
            catch (Exception ex)
            {
                Mostrar("No se pudo iniciar la aplicación", ex);
            }
        }

        private static void Mostrar(string titulo, Exception ex)
        {
            string detalle = ex == null ? "(sin detalle)" : ex.GetType().FullName + ": " + ex.Message;
            if (ex != null && ex.InnerException != null)
            {
                detalle += Environment.NewLine + "Interna: " + ex.InnerException.GetType().FullName
                           + ": " + ex.InnerException.Message;
            }
            MessageBox.Show(detalle, titulo, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
