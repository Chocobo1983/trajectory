namespace Infozahyst.RSAAS.Core.Tools;

public static class DateTimeHelper
{
    public const double FormattedNsToNs = 1e9 / 122.88e6;
    private const double NSDivider = 0.12288;

    public static DateTime GetDateTime(ulong timestamp, ulong formattedNs) {
        var ms = formattedNs * FormattedNsToNs / 1_000_000;

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(timestamp * 1000 + ms)).UtcDateTime.ToLocalTime();
    }

    public static (uint, uint) ToTimestamp(DateTime dateTime) {
        var ms = ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds();
        var seconds = ms / 1000;
        var nanoseconds = (ms - seconds * 1000) * 1_000_000 / FormattedNsToNs;

        return ((uint, uint))(seconds, nanoseconds);
    }

    public static DateTime NsToDateTime(long timestampInNs) {
        var timestamp = timestampInNs / 1_000_000_000L;
        var nanoseconds = timestampInNs % 1_000_000_000L;
        return DateTimeOffset.FromUnixTimeSeconds(timestamp).AddTicks((long)(nanoseconds / 100f)).UtcDateTime
            .ToLocalTime();
    }

    public static DateTime NsToDateTime(ulong timestampInNs) {
        return NsToDateTime((long)timestampInNs);
    }
    
    public static long ParseTimestampToNs(ReadOnlySpan<byte> data) {
        var timestamp = BitConverter.ToUInt32(data.Slice(4, 4));
        var nanoseconds = BitConverter.ToUInt32(data.Slice(0, 4)) / NSDivider;
        return timestamp * 1_000_000_000L + (uint)nanoseconds;
    }

    public static DateTime ParseTimestampToDateTime(ReadOnlySpan<byte> data) {
        var timestamp = BitConverter.ToUInt32(data.Slice(4, 4));
        var nanoseconds = BitConverter.ToUInt32(data.Slice(0, 4)) / NSDivider;
        return DateTimeOffset.FromUnixTimeSeconds(timestamp).AddTicks((long)nanoseconds).UtcDateTime.ToLocalTime();
    }

    public static DateTime ConvertUnixTimeWithMicroseconds(long timeSec, long timeUsec) {
        var dateTime = DateTimeOffset.FromUnixTimeSeconds(timeSec).UtcDateTime;
        dateTime = dateTime.AddTicks(timeUsec * (TimeSpan.TicksPerSecond / 1_000_000));
        return dateTime.ToLocalTime();
    }
}
