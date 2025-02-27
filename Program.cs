using System.Text.Json;
using System.Diagnostics;

record KlineData(
    DateTime Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume
);

class KlineMonitor
{
    private const string BASE_URL = "https://api.bybit.com";
    private static readonly HttpClient client = new()
    {
        BaseAddress = new Uri(BASE_URL)
    };

    private static readonly string LogFileName = $"kline_monitor_{DateTime.Now:yyyyMMdd_HHmmss}.log";
    private static DateTime _lastHourlyCheck = DateTime.MinValue;
    private static DateTime _lastDailyCheck = DateTime.MinValue;

    public static async Task RunMonitoring(CancellationToken cancellationToken = default)
    {
        await File.AppendAllTextAsync(LogFileName, $"監測開始時間: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} (UTC)\n\n");

        var stopwatch = new Stopwatch();

        while (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Restart();
            var now = DateTime.UtcNow;

            // 每小時K線檢查
            if (ShouldCheckHourly(now))
            {
                await CheckAndLogKlines("60", now, "每小時");
                _lastHourlyCheck = now;
            }

            // 每日K線檢查
            if (ShouldCheckDaily(now))
            {
                await CheckAndLogKlines("D", now, "每日");
                _lastDailyCheck = now;
            }

            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var delayMs = Math.Max(1000 - (int)elapsedMs, 1); // 確保至少等待 1ms，避免負數
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    private static bool ShouldCheckHourly(DateTime now)
    {
        return (now.Second == 0 || now.Second == 3 || now.Second == 5) && (now.Minute >= 55 || now.Minute <= 5);
    }

    private static bool ShouldCheckDaily(DateTime now)
    {
        return (now.Second == 0 || now.Second == 3 || now.Second == 5) && ((now.Hour == 23 && now.Minute >= 55) || (now.Hour == 0 && now.Minute <= 5));
    }

    private static async Task CheckAndLogKlines(string interval, DateTime checkTime, string checkType)
    {
        try
        {
            var parameters = new
            {
                Symbol = "BTCUSDT",
                Interval = interval,
                Limit = "10"
            };

            var endpoint = $"/v5/market/kline?category=spot&symbol={parameters.Symbol}&interval={parameters.Interval}&limit={parameters.Limit}";

            using var response = await client.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();

            using var responseStream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseStream);

            var klineDataList = document.RootElement
                .GetProperty("result")
                .GetProperty("list")
                .EnumerateArray()
                .Select(item =>
                {
                    var values = item.EnumerateArray().ToList();
                    return new KlineData(
                        Time: DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(values[0].GetString()!))
                              .ToOffset(TimeSpan.FromHours(8))  // 轉換為台灣時間 (UTC+8)
                              .DateTime,
                        Open: decimal.Parse(values[1].GetString()!),
                        High: decimal.Parse(values[2].GetString()!),
                        Low: decimal.Parse(values[3].GetString()!),
                        Close: decimal.Parse(values[4].GetString()!),
                        Volume: decimal.Parse(values[5].GetString()!)
                    );
                })
                .ToList();

            var logLines = new List<string>
            {
                $"{checkType}========== {checkType} K線檢查 ==========",
                $"檢查時間: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} (UTC), {DateTime.UtcNow.AddHours(8):yyyy-MM-dd HH:mm:ss} (台灣時間)",
                $"K線間隔: {interval}",
                "獲取到的K線數據:"
            };

            logLines.AddRange(klineDataList.Take(3).Select(kline =>
                $"K線時間: {kline.Time:yyyy-MM-dd HH:mm:ss} (台灣時間), " +
                $"Open: {kline.Open}, Close: {kline.Close}, " +
                $"High: {kline.High}, Low: {kline.Low}, " +
                $"Volume: {kline.Volume}"
            ));

            logLines.Add("");

            // **一次性寫入日誌，避免多次 I/O 操作**
            await File.AppendAllLinesAsync(LogFileName, logLines);
        }
        catch (Exception ex)
        {
            var errorLog = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {checkType} K線檢查錯誤: {ex.Message}\n\n";
            await File.AppendAllTextAsync(LogFileName, errorLog);
        }
    }
}

class Program
{
    static async Task Main()
    {
        Console.WriteLine("開始監測K線數據...");
        Console.WriteLine("監測時間範圍：");
        Console.WriteLine("- 每小時K線：每小時的55分到下一個小時的5分");
        Console.WriteLine("- 每日K線：UTC 23:50到次日00:10");
        Console.WriteLine("\n按 'Q' 鍵停止監測");

        using var cts = new CancellationTokenSource();

        // 在背景執行監測
        var monitoringTask = KlineMonitor.RunMonitoring(cts.Token);

        // 等待用戶按 'Q' 鍵
        while (Console.ReadKey(true).Key != ConsoleKey.Q)
        {
            await Task.Delay(100);
        }

        // 停止監測
        cts.Cancel();
        try
        {
            await monitoringTask;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("監測已停止");
        }
    }
}
