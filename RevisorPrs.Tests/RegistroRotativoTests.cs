using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Xunit;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests
{
    /// <summary>
    /// Tests del log a fichero rotado por tamano (RV.21).
    ///
    /// Pautas comunes:
    /// - los tests usan ficheros en un directorio temporal que se borra al final;
    /// - la rotacion se fuerza inyectando un tamano limite minusculo, NUNCA
    ///   escribiendo megas reales ni esperando un minuto;
    /// - los secretos y el contenido del diff se bloquean a nivel de proveedor,
    ///   asi que estos asserts cubren el comportamiento de RV.21 sin debilitarse.
    /// </summary>
    public class RegistroRotativoTests : IDisposable
    {
        private readonly string _directorio;

        public RegistroRotativoTests()
        {
            _directorio = Path.Combine(Path.GetTempPath(), "revisor-prs-tests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_directorio);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directorio))
                {
                    Directory.Delete(_directorio, recursive: true);
                }
            }
            catch
            {
                // Ignorar: el sistema operativo limpiara %TEMP% tarde o temprano.
            }
        }

        private string CrearRotador(long tamanoMaximo, int ficherosConservados, Func<long>? tamanoFichero = null)
        {
            string ruta = Path.Combine(_directorio, "revisor.log");
            return ruta;
        }

        [Fact]
        public void Rotar_ConTamanoLimiteMinusculo_ConservaSoloElNumeroConfiguradoDeFicheros()
        {
            // Tamano limite de 1 byte: cada linea escrita fuerza una rotacion.
            // Tras rotar el activo queda vacio y los antiguos se desplazan;
            // el limite de ficherosConservados fija cuantos se conservan en total
            // contando el activo.
            string ruta = Path.Combine(_directorio, "revisor.log");
            var rotador = new RotadorRegistros(ruta, tamanoMaximoBytes: 1, ficherosConservados: 3);

            // Tras las 3 rotaciones el contenido del activo y de los 2 rotados
            // mas recientes debe ser el esperado, y NO debe haber un cuarto fichero.
            for (int i = 0; i < 3; i++)
            {
                File.AppendAllText(ruta, $"linea {i}" + Environment.NewLine);
                rotador.RotarSiEsNecesario();
            }

            Assert.True(File.Exists(ruta), "El fichero activo debe seguir existiendo tras rotar.");
            Assert.True(File.Exists(ruta + ".1"), "Debe existir la primera rotacion.");
            Assert.True(File.Exists(ruta + ".2"), "Debe existir la segunda rotacion.");
            Assert.False(File.Exists(ruta + ".3"), "El cuarto fichero debe haberse borrado para no superar FicherosConservados.");

            // El activo se vacio en la ultima rotacion.
            Assert.Equal(string.Empty, File.ReadAllText(ruta));
            Assert.Contains("linea 2", File.ReadAllText(ruta + ".1"));
            Assert.Contains("linea 1", File.ReadAllText(ruta + ".2"));
        }

        [Fact]
        public void Rotar_ConLimiteDeDosFicheros_ConservaSoloElActivoYUnRotado()
        {
            // Con ficherosConservados=2 el conjunto final tras N rotaciones es:
            // activo (vacio) + ruta.1. Ningun otro fichero puede quedar en disco.
            string ruta = Path.Combine(_directorio, "revisor.log");
            var rotador = new RotadorRegistros(ruta, tamanoMaximoBytes: 1, ficherosConservados: 2);

            for (int i = 0; i < 4; i++)
            {
                File.AppendAllText(ruta, $"linea {i}" + Environment.NewLine);
                rotador.RotarSiEsNecesario();
            }

            Assert.True(File.Exists(ruta), "El fichero activo debe seguir existiendo.");
            Assert.True(File.Exists(ruta + ".1"), "Debe conservarse exactamente una rotacion.");
            Assert.False(File.Exists(ruta + ".2"), "Con FicherosConservados=2 no debe haber una segunda rotacion.");
            Assert.Equal(string.Empty, File.ReadAllText(ruta));
            Assert.Contains("linea 3", File.ReadAllText(ruta + ".1"));
        }

        [Fact]
        public void Proveedor_NoEscribeSecretoDeConfiguracion_EnElFicheroDeLog()
        {
            string ruta = Path.Combine(_directorio, "revisor.log");
            var rotador = new RotadorRegistros(ruta, tamanoMaximoBytes: 0, ficherosConservados: 5);

            const string secreto = "token-super-secreto-1234567890";
            var saneador = new SaneadorSecretos(new[] { secreto });
            var proveedor = new ProveedorRegistrosRotativo(rotador, saneador, consola: null);

            ILogger logger = proveedor.CreateLogger("Test");
            logger.LogInformation("Inicio con clave {Clave}", secreto);

            string contenido = File.ReadAllText(ruta);
            Assert.DoesNotContain(secreto, contenido);
            Assert.Contains("***", contenido);
        }

        [Fact]
        public void Proveedor_NoEscribeContenidoDeDiff_EnElFicheroDeLog()
        {
            string ruta = Path.Combine(_directorio, "revisor.log");
            var rotador = new RotadorRegistros(ruta, tamanoMaximoBytes: 0, ficherosConservados: 5);

            var proveedor = new ProveedorRegistrosRotativo(rotador, saneador: null, consola: null);
            ILogger logger = proveedor.CreateLogger("Test");

            string diffFalso =
                "diff --git a/fichero.cs b/fichero.cs\n" +
                "index 1234..5678 100644\n" +
                "--- a/fichero.cs\n" +
                "+++ b/fichero.cs\n" +
                "@@ -1,3 +1,3 @@\n" +
                "-linea antigua\n" +
                "+linea nueva y sensible: CLAVE-QUE-NO-DEBE-VERSE\n";

            logger.LogInformation("{Diff}", diffFalso);

            string contenido = File.ReadAllText(ruta);
            Assert.DoesNotContain("CLAVE-QUE-NO-DEBE-VERSE", contenido);
            Assert.DoesNotContain("diff --git", contenido);
            Assert.Contains(ProveedorRegistrosRotativo.MarcaBloqueoDiff, contenido);
        }
    }
}