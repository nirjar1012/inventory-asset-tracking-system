using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace YardTracker.Service.Data
{
    /// <summary>Thin ADO.NET wrapper. All data access goes through stored procedures.</summary>
    internal sealed class Db
    {
        private static readonly Lazy<Db> DefaultInstance = new Lazy<Db>(FromConfig);
        private readonly string _connectionString;

        public Db(string connectionString)
        {
            _connectionString = connectionString;
        }

        public static Db Default => DefaultInstance.Value;

        private static Db FromConfig()
        {
            var setting = ConfigurationManager.ConnectionStrings["YardTracker"];
            if (setting == null || string.IsNullOrWhiteSpace(setting.ConnectionString))
                throw new ConfigurationErrorsException("Connection string 'YardTracker' is missing from YardTracker.Service.exe.config.");
            return new Db(setting.ConnectionString);
        }

        /// <summary>Opens a connection and returns "server/database (version)". Throws if unreachable.</summary>
        public string Describe()
        {
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand("SELECT CONCAT(@@SERVERNAME, '/', DB_NAME(), ' (SQL Server ', CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), ')')", connection))
            {
                connection.Open();
                return (string)command.ExecuteScalar();
            }
        }

        public async Task<List<T>> QueryAsync<T>(string procedure, Action<SqlParameterCollection>? parameters, Func<SqlDataReader, T> map)
        {
            using (var connection = new SqlConnection(_connectionString))
            using (var command = CreateCommand(connection, procedure, parameters))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    var rows = new List<T>();
                    while (await reader.ReadAsync().ConfigureAwait(false))
                        rows.Add(map(reader));

                    // Drain remaining results so errors raised after the first result set surface here.
                    while (await reader.NextResultAsync().ConfigureAwait(false))
                    {
                    }
                    return rows;
                }
            }
        }

        public async Task<T?> QuerySingleAsync<T>(string procedure, Action<SqlParameterCollection>? parameters, Func<SqlDataReader, T> map)
            where T : class
        {
            var rows = await QueryAsync(procedure, parameters, map).ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }

        public async Task<int> ExecuteAsync(string procedure, Action<SqlParameterCollection>? parameters)
        {
            using (var connection = new SqlConnection(_connectionString))
            using (var command = CreateCommand(connection, procedure, parameters))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private static SqlCommand CreateCommand(SqlConnection connection, string procedure, Action<SqlParameterCollection>? parameters)
        {
            var command = new SqlCommand(procedure, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            parameters?.Invoke(command.Parameters);
            return command;
        }
    }

    internal static class SqlParameterExtensions
    {
        public static void AddVarChar(this SqlParameterCollection p, string name, string? value, int size) =>
            p.Add(name, SqlDbType.VarChar, size).Value = (object?)value ?? DBNull.Value;

        public static void AddNVarChar(this SqlParameterCollection p, string name, string? value, int size) =>
            p.Add(name, SqlDbType.NVarChar, size).Value = (object?)value ?? DBNull.Value;

        public static void AddInt(this SqlParameterCollection p, string name, int? value) =>
            p.Add(name, SqlDbType.Int).Value = (object?)value ?? DBNull.Value;

        public static void AddSmallInt(this SqlParameterCollection p, string name, short? value) =>
            p.Add(name, SqlDbType.SmallInt).Value = (object?)value ?? DBNull.Value;

        public static void AddBit(this SqlParameterCollection p, string name, bool value) =>
            p.Add(name, SqlDbType.Bit).Value = value;

        public static void AddGuid(this SqlParameterCollection p, string name, Guid value) =>
            p.Add(name, SqlDbType.UniqueIdentifier).Value = value;

        public static void AddDateTime2(this SqlParameterCollection p, string name, DateTime? value)
        {
            var parameter = p.Add(name, SqlDbType.DateTime2);
            parameter.Scale = 3;
            parameter.Value = (object?)value ?? DBNull.Value;
        }

        public static void AddDecimal(this SqlParameterCollection p, string name, decimal? value, byte precision, byte scale)
        {
            var parameter = p.Add(name, SqlDbType.Decimal);
            parameter.Precision = precision;
            parameter.Scale = scale;
            parameter.Value = (object?)value ?? DBNull.Value;
        }

        public static void AddTable(this SqlParameterCollection p, string name, string typeName, DataTable table)
        {
            var parameter = p.Add(name, SqlDbType.Structured);
            parameter.TypeName = typeName;
            parameter.Value = table;
        }
    }

    internal static class DataRecordExtensions
    {
        public static string Str(this IDataRecord r, string column) => r[column] is string s ? s : string.Empty;
        public static string? StrOrNull(this IDataRecord r, string column) => r[column] as string;

        public static int Int(this IDataRecord r, string column) => Convert.ToInt32(r[column]);
        public static int? IntOrNull(this IDataRecord r, string column) => r[column] is DBNull ? (int?)null : Convert.ToInt32(r[column]);

        public static long? LongOrNull(this IDataRecord r, string column) => r[column] is DBNull ? (long?)null : Convert.ToInt64(r[column]);

        public static short Short(this IDataRecord r, string column) => Convert.ToInt16(r[column]);
        public static short? ShortOrNull(this IDataRecord r, string column) => r[column] is DBNull ? (short?)null : Convert.ToInt16(r[column]);

        public static byte Byte(this IDataRecord r, string column) => Convert.ToByte(r[column]);

        public static decimal Dec(this IDataRecord r, string column) => r[column] is DBNull ? 0m : Convert.ToDecimal(r[column]);
        public static decimal? DecOrNull(this IDataRecord r, string column) => r[column] is DBNull ? (decimal?)null : Convert.ToDecimal(r[column]);

        public static double Dbl(this IDataRecord r, string column) => Convert.ToDouble(r[column]);
        public static double? DblOrNull(this IDataRecord r, string column) => r[column] is DBNull ? (double?)null : Convert.ToDouble(r[column]);

        public static DateTime Utc(this IDataRecord r, string column) => DateTime.SpecifyKind((DateTime)r[column], DateTimeKind.Utc);

        public static DateTime? UtcOrNull(this IDataRecord r, string column) =>
            r[column] is DBNull ? (DateTime?)null : DateTime.SpecifyKind((DateTime)r[column], DateTimeKind.Utc);
    }
}
