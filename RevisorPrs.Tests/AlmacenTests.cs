using System;
using System.IO;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests
{
    [TestFixture]
    public class AlmacenTests
    {
        private string CrearBaseTemporal()
        {
            string carpeta = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(carpeta);
            return Path.Combine(carpeta, "testdb.db");
        }

        [Test]
        public void Almacen_SobreBaseVacia_AplicaLasMigraciones()
        {
            string ruta = CrearBaseTemporal();
            using var almacen = new Almacen(ruta);

            // La conexión ya abre y aplica migraciones
            Assert.That(true); // No excepción
        }

        [Test]
        public void Almacen_SobreBaseYaMigrada_NoPierdeDatos()
        {
            string ruta = CrearBaseTemporal();
            using (var almacen = new Almacen(ruta))
            {
                // Marcar una revisión para test
                almacen.MarcarRevisado("repo", 1, "commit1");
            }

            // Reabrir para validar que la revisión persiste y migraciones no borran
            using (var almacen = new Almacen(ruta))
            {
                Assert.IsTrue(almacen.Revisado("repo", 1, "commit1"));
            }
        }

        [Test]
        public void Almacen_AplicarMigracionesDosVeces_NoRompeNiDuplica()
        {
            string ruta = CrearBaseTemporal();
            using (var almacen = new Almacen(ruta))
            {
                // Primera apertura aplica migraciones
            }

            using (var almacen = new Almacen(ruta))
            {
                // Segunda apertura re-aplica migraciones sin error ni duplicados
            }

            using (var conexion = new SqliteConnection($"Data Source={ruta}"))
            {
                conexion.Open();
                using (var cmd = conexion.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM EsquemaVersion";
                    var count = (long)cmd.ExecuteScalar();
                    Assert.AreEqual(2, count, "Debe haber exactamente 2 migraciones aplicadas y registradas");
                }
            }
        }
    }
}
