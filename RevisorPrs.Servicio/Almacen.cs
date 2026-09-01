using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace RevisorPrs.Servicio
{
    public class Almacen : IDisposable
    {
        private readonly string _connectionString;
        private SqliteConnection? _connection;

        // Definicion de migraciones numeradas
        private readonly List<(int version, Action migracion)> migraciones;

        public Almacen(string rutaBaseDatos)
        {
            migraciones = new List<(int, Action)>()
            {
                (1, Migracion1),
                (2, Migracion2),
            };

            if (string.IsNullOrWhiteSpace(rutaBaseDatos))
            {
                var rutaEjecutable = AppContext.BaseDirectory;
                rutaBaseDatos = Path.Combine(rutaEjecutable, "revisorprs.db");
            }
            else if (!Path.IsPathRooted(rutaBaseDatos))
            {
                var rutaEjecutable = AppContext.BaseDirectory;
                rutaBaseDatos = Path.Combine(rutaEjecutable, rutaBaseDatos);
            }

            _connectionString = $"Data Source={rutaBaseDatos}";
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            CrearTablaVersion();
            AplicarMigraciones();
        }

        private void CrearTablaVersion()
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS EsquemaVersion (
                    Version INTEGER PRIMARY KEY
                );
            ";
            cmd.ExecuteNonQuery();
        }

        private int ObtenerVersionActual()
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT MAX(Version) FROM EsquemaVersion";
            object? result = cmd.ExecuteScalar();
            if (result == DBNull.Value || result == null)
                return 0;
            return Convert.ToInt32(result);
        }

        private void InsertarVersion(int version)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "INSERT INTO EsquemaVersion (Version) VALUES (@version)";
            cmd.Parameters.AddWithValue("@version", version);
            cmd.ExecuteNonQuery();
        }

        private void Migracion1()
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Revisiones (
                    Repositorio TEXT NOT NULL,
                    PullRequest INTEGER NOT NULL,
                    ""Commit"" TEXT NOT NULL,
                    PRIMARY KEY(Repositorio, PullRequest, ""Commit"")
                );
            ";
            cmd.ExecuteNonQuery();
        }

        private void Migracion2()
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS HallazgosPublicados (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Repositorio TEXT NOT NULL,
                    PullRequest INTEGER NOT NULL,
                    ""Commit"" TEXT NOT NULL,
                    Comentario TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        public void AplicarMigraciones()
        {
            int versionActual = ObtenerVersionActual();
            foreach (var (version, migracion) in migraciones)
            {
                if (version > versionActual)
                {
                    migracion();
                    InsertarVersion(version);
                }
            }
        }

        public bool Revisado(string repositorio, int pullRequest, string commit)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"SELECT 1 FROM Revisiones WHERE Repositorio = @repositorio AND PullRequest = @pullRequest AND ""Commit"" = @commit LIMIT 1";
            cmd.Parameters.AddWithValue("@repositorio", repositorio);
            cmd.Parameters.AddWithValue("@pullRequest", pullRequest);
            cmd.Parameters.AddWithValue("@commit", commit);

            var result = cmd.ExecuteScalar();
            return result != null;
        }

        public void MarcarRevisado(string repositorio, int pullRequest, string commit)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO Revisiones (Repositorio, PullRequest, ""Commit"") VALUES (@repositorio, @pullRequest, @commit)";
            cmd.Parameters.AddWithValue("@repositorio", repositorio);
            cmd.Parameters.AddWithValue("@pullRequest", pullRequest);
            cmd.Parameters.AddWithValue("@commit", commit);
            cmd.ExecuteNonQuery();
        }

        public void GuardarHallazgoPublicado(string repositorio, int pullRequest, string commit, string comentario)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"INSERT INTO HallazgosPublicados (Repositorio, PullRequest, ""Commit"", Comentario) VALUES (@repositorio, @pullRequest, @commit, @comentario)";
            cmd.Parameters.AddWithValue("@repositorio", repositorio);
            cmd.Parameters.AddWithValue("@pullRequest", pullRequest);
            cmd.Parameters.AddWithValue("@commit", commit);
            cmd.Parameters.AddWithValue("@comentario", comentario);
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
