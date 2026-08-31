using Npgsql;
using PinoyRideHrApi.Infrastructure;

namespace PinoyRideHrApi.Data;

public class Db
{
    private readonly string? _connectionString;

    public Db(IConfiguration config)
    {
        _connectionString = config["DATABASE_URL"];
    }

    public NpgsqlConnection Open()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new ApiException(500, "DATABASE_URL is not configured.");
        }

        var con = new NpgsqlConnection(_connectionString);
        con.Open();
        return con;
    }
}