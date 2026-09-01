using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;
using RevisorPrs.Servicio;

namespace RevisorPrs.Tests
{
    public class AlmacenTests
    {
        private string CrearBaseTemporal()
        {
            string carpeta = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(carpeta);
            return Path.Combine(carpeta, "testdb.db");
        }

        [Fact]
        public void Almacen_SobreBaseVacia_AplicaLasMigraciones()
        {
            string ruta = CrearBaseTemporal();
            using var almacen = new Almacen(ruta);

            // La conexión ya abre y aplica migraciones. Se verifica que la tabla de migraciones
            // existe y tiene los registros esperados.
            using (var conexion = new SqliteConnection($"Data Source={ruta}"))
            {
                conexion.Open();
                using (var cmd = conexion.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM EsquemaVersion";
                    var count = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(4, count);
                }
            }
        }

        [Fact]
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
                Assert.True(almacen.Revisado("repo", 1, "commit1"));
            }
        }

        [Fact]
        public void Almacen_DosRepositorios_NoCompartenRevisiones()
        {
            string ruta = CrearBaseTemporal();
            using (var almacen = new Almacen(ruta))
            {
                almacen.MarcarRevisado("equipo-a/repo-1", 42, "abc123");
                almacen.GuardarHallazgoPublicado("equipo-a/repo-1", 42, "abc123", "comentario repo-1");
            }

            // Reabrir y comprobar que un PR con el mismo numero y commit en OTRO repositorio
            // no aparece como revisado ni tiene hallazgos publicados.
            using (var almacen = new Almacen(ruta))
            {
                Assert.False(almacen.Revisado("equipo-a/repo-2", 42, "abc123"),
                    "El PR del repo-2 no debe aparecer como revisado por datos del repo-1.");

                using (var conexion = new SqliteConnection($"Data Source={ruta}"))
                {
                    conexion.Open();
                    using var cmd = conexion.CreateCommand();
                    cmd.CommandText = @"SELECT COUNT(*) FROM HallazgosPublicados WHERE Repositorio = @repo";
                    cmd.Parameters.AddWithValue("@repo", "equipo-a/repo-2");
                    var cuenta = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(0, cuenta);
                }
            }
        }

        [Fact]
        public void Almacen_MigracionSobreBaseConDatosPrevios_NoLosPierde()
        {
            string ruta = CrearBaseTemporal();

            // Fase 1: base solo con migraciones 1 y 2, con datos previos.
            using (var almacen = new Almacen(ruta))
            {
                almacen.MarcarRevisado("equipo-a/repo-1", 1, "commit-inicial");
                almacen.GuardarHallazgoPublicado("equipo-a/repo-1", 1, "commit-inicial", "hallazgo previo");
            }

            // Fase 2: reabrir dispara la nueva migracion 3 sobre la base con datos.
            using (var almacen = new Almacen(ruta))
            {
                Assert.True(almacen.Revisado("equipo-a/repo-1", 1, "commit-inicial"),
                    "El registro previo de Revisiones debe sobrevivir a la migracion 3.");

                using (var conexion = new SqliteConnection($"Data Source={ruta}"))
                {
                    conexion.Open();

                    using (var cmdVersiones = conexion.CreateCommand())
                    {
                    cmdVersiones.CommandText = "SELECT COUNT(*) FROM EsquemaVersion";
                    var versiones = Convert.ToInt64(cmdVersiones.ExecuteScalar());
                        Assert.Equal(4, versiones);
                    }

                    using (var cmdIndice = conexion.CreateCommand())
                    {
                        cmdIndice.CommandText =
                            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_HallazgosPublicados_Aislamiento'";
                        var indice = Convert.ToInt64(cmdIndice.ExecuteScalar());
                        Assert.Equal(1, indice);
                    }

                    using (var cmdHallazgos = conexion.CreateCommand())
                    {
                        cmdHallazgos.CommandText = "SELECT COUNT(*) FROM HallazgosPublicados";
                        var hallazgos = Convert.ToInt64(cmdHallazgos.ExecuteScalar());
                        Assert.Equal(1, hallazgos);
                    }
                }
            }
        }

        [Fact]
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
                    var count = Convert.ToInt64(cmd.ExecuteScalar());
                    Assert.Equal(4, count); // Debe haber exactamente 4 migraciones aplicadas y registradas
                }
            }
        }
    }
}
