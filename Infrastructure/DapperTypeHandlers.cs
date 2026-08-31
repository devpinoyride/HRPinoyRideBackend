using System.Data;
using Dapper;

namespace PinoyRideHrApi.Infrastructure;

/// <summary>
/// Dapper type handlers for Postgres date/time types.
/// Npgsql surfaces `date` as DateTime and `time` as TimeSpan on some hosts,
/// while the API models use DateOnly / TimeOnly. These handlers translate both
/// directions (parameter binding and row parsing) so Dapper can move values
/// between the database and the models transparently.
/// </summary>
public static class DapperTypeHandlers
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
        _registered = true;
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateOnly.")
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }
    }

    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override TimeOnly Parse(object value) => value switch
        {
            TimeOnly t => t,
            TimeSpan ts => TimeOnly.FromTimeSpan(ts),
            DateTime dt => TimeOnly.FromDateTime(dt),
            string s => TimeOnly.Parse(s),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to TimeOnly.")
        };

        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.DbType = DbType.Time;
            parameter.Value = value.ToTimeSpan();
        }
    }
}