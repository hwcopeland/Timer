using System;
using System.Data;
using Sharp.Shared.Units;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

internal abstract class BaseSteamIdEntity
{
    [SugarColumn(IsPrimaryKey = true, SqlParameterDbType = typeof(SteamIdDataConvert))]
    public SteamID SteamId { get; set; }
}

internal abstract class BaseSteamIdSerialEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
    public SteamID SteamId { get; set; }
}

internal sealed class SteamIdDataConvert : ISugarDataConverter
{
    public SugarParameter ParameterConverter<T>(object columnValue, int columnIndex)
    {
        var name = $"@SteamID{columnIndex}";

        if (columnValue is SteamID steamId)
        {
            return new SugarParameter(name, unchecked((long) steamId.AsPrimitive()), System.Data.DbType.Int64);
        }

        if (columnValue is ulong unsignedValue)
        {
            return new SugarParameter(name, unchecked((long) unsignedValue), System.Data.DbType.Int64);
        }

        if (columnValue is long signedValue)
        {
            return new SugarParameter(name, signedValue, System.Data.DbType.Int64);
        }

        return new SugarParameter(name, null);
    }

    public T QueryConverter<T>(IDataRecord dataRecord, int dataRecordIndex)
    {
        if (dataRecord.IsDBNull(dataRecordIndex))
        {
            return default!;
        }

        var rawValue = dataRecord.GetValue(dataRecordIndex);

        var steamIdValue = rawValue switch
        {
            ulong unsignedValue => unsignedValue,
            long signedValue => unchecked((ulong) signedValue),
            decimal decimalValue => unchecked((ulong) decimalValue),
            int intValue => unchecked((ulong) intValue),
            _ => Convert.ToUInt64(rawValue),
        };

        return (T) (object) new SteamID(steamIdValue);
    }
}
