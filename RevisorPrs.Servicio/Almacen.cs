using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace RevisorPrs.Servicio
{
    public class Almacen : IAlmacen, IDisposable
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
                (3, Migracion3),
                (4, Migracion4),
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

        private void Migracion3()
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS IX_HallazgosPublicados_Aislamiento
                ON HallazgosPublicados (Repositorio, PullRequest, ""Commit"");
            ";
            cmd.ExecuteNonQuery();
        }

        private void Migracion4()
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS IntentosFallidos (
                    Repositorio TEXT NOT NULL,
                    PullRequest INTEGER NOT NULL,
                    ""Commit"" TEXT NOT NULL,
                    Motivo TEXT NOT NULL,
                    Intentos INTEGER NOT NULL,
                    ProximoReintentoUtc TEXT NOT NULL,
                    PRIMARY KEY(Repositorio, PullRequest)
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

        public IEnumerable<(string Repositorio, int Numero, string Commit)> ListarRevisiones()
        {
            var resultado = new List<(string, int, string)>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"SELECT Repositorio, PullRequest, ""Commit"" FROM Revisiones";
            using var lector = cmd.ExecuteReader();
            while (lector.Read())
            {
                resultado.Add((lector.GetString(0), lector.GetInt32(1), lector.GetString(2)));
            }
            return resultado;
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

        public void MarcarFallido(string repositorio, int pullRequest, string commit, string motivo)
        {
            // Backoff exponencial en minutos, con tope de 60 minutos, para que
            // un PR persistentemente roto no se reintente en cada vuelta.
            int intentosPrevios;
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = @"SELECT Intentos FROM IntentosFallidos WHERE Repositorio = @repositorio AND PullRequest = @pullRequest";
                cmd.Parameters.AddWithValue("@repositorio", repositorio);
                cmd.Parameters.AddWithValue("@pullRequest", pullRequest);
                var actual = cmd.ExecuteScalar();
                intentosPrevios = actual is null || actual is DBNull ? 0 : Convert.ToInt32(actual);
            }

            int nuevosIntentos = intentosPrevios + 1;
            int minutos = Math.Min(60, 1 << Math.Min(nuevosIntentos - 1, 30));
            var proximoReintento = DateTimeOffset.UtcNow.AddMinutes(minutos).ToString("O");

            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO IntentosFallidos (Repositorio, PullRequest, ""Commit"", Motivo, Intentos, ProximoReintentoUtc)
                                    VALUES (@repositorio, @pullRequest, @commit, @motivo, @intentos, @proximo)
                                    ON CONFLICT(Repositorio, PullRequest) DO UPDATE SET
                                        ""Commit"" = excluded.""Commit"",
                                        Motivo = excluded.Motivo,
                                        Intentos = excluded.Intentos,
                                        ProximoReintentoUtc = excluded.ProximoReintentoUtc";
                cmd.Parameters.AddWithValue("@repositorio", repositorio);
                cmd.Parameters.AddWithValue("@pullRequest", pullRequest);
                cmd.Parameters.AddWithValue("@commit", commit);
                cmd.Parameters.AddWithValue("@motivo", motivo);
                cmd.Parameters.AddWithValue("@intentos", nuevosIntentos);
                cmd.Parameters.AddWithValue("@proximo", proximoReintento);
                cmd.ExecuteNonQuery();
            }
        }

        public bool DebeReintentar(string repositorio, int pullRequest, DateTimeOffset ahora)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"SELECT ProximoReintentoUtc FROM IntentosFallidos WHERE Repositorio = @repositorio AND PullRequest = @pullRequest";
            cmd.Parameters.AddWithValue("@repositorio", repositorio);
            cmd.Parameters.AddWithValue("@pullRequest", pullRequest);
            var valor = cmd.ExecuteScalar();
            if (valor is null || valor is DBNull)
            {
                return true;
            }

            if (!DateTimeOffset.TryParse((string)valor, out var proximo))
            {
                return true;
            }

            return ahora >= proximo;
        }

        public IEnumerable<(string Repositorio, int PullRequest, string Commit, string Motivo)> ListarFallos()
        {
            var resultado = new List<(string, int, string, string)>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"SELECT Repositorio, PullRequest, ""Commit"", Motivo FROM IntentosFallidos";
            using var lector = cmd.ExecuteReader();
            while (lector.Read())
            {
                resultado.Add((lector.GetString(0), lector.GetInt32(1), lector.GetString(2), lector.GetString(3)));
            }
            return resultado;
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
