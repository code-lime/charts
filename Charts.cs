#!/usr/bin/dotnet run

using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

CancellationToken cancellationToken = CancellationToken.None;

const int days = 100;

static async ValueTask<Dictionary<DateKey, long>> ExportPluginDataAsync(string pluginId, string chartKey, int days, CancellationToken cancellationToken)
{
    const int elementsPerDay = 24 * 2;
    int elements = days * elementsPerDay;

    using var client = new HttpClient();
    await using var stream = await client.GetStreamAsync($"https://bstats.org/api/v1/plugins/{pluginId}/charts/{chartKey}/data?maxElements={elements}", cancellationToken);
    var raw = (await JsonSerializer.DeserializeAsync(stream, JsonContexts.Default.Int64ArrayArray, cancellationToken))!;
    return raw
        .Select(v => new
        {
            Date = DateKey.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(v[0]).UtcDateTime),
            Value = v[1],
        })
        .GroupBy(v => v.Date)
        .ToDictionary(v => v.Key, v => v.Max(v => v.Value)) ?? [];
}
static async ValueTask CreateImageAsync(JsonObject chartRaw, string outputFile, CancellationToken cancellationToken)
{
    using var client = new HttpClient();

    JsonObject json = new()
    {
        ["width"] = "800",
        ["height"] = "200",
        ["format"] = "png",
        ["backgroundColor"] = "transparent",
        ["chart"] = chartRaw,
    };

    using var response = await client.PostAsync($"https://quickchart.io/chart/create", new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
    
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    var raw = (await JsonSerializer.DeserializeAsync(stream, JsonContexts.Default.QuickChartResponse, cancellationToken))!;
    using var imageStream = await client.GetStreamAsync(raw.Url, cancellationToken);
    if (Path.GetDirectoryName(outputFile) is string dir)
        Directory.CreateDirectory(dir);
    using var outputStream = File.Open(outputFile, FileMode.Create, FileAccess.Write);
    await imageStream.CopyToAsync(outputStream, cancellationToken);
}
static async ValueTask CreateBStatsAsync(int pluginId, string outputFile, CancellationToken cancellationToken)
{
    var serversRawData = await ExportPluginDataAsync(pluginId.ToString(), "servers", days, cancellationToken);
    //var playersRawData = await ExportPluginDataAsync(pluginId.ToString(), "players", days, cancellationToken);

    (DateKey min, DateKey max) range = serversRawData.Keys
        //.Concat(playersRawData.Keys)
        .Aggregate(
            (min: DateKey.MaxValue, max: DateKey.MinValue),
            (a, c) => (a.min > c ? c : a.min, a.max < c ? c : a.max));

    int length = range.max.Number - range.min.Number + 1;

    var serversPoints = new JsonArray(); 

    for (int i = 0; i < length; i++)
    {
        var date = range.min.AddNumber(i);
        serversPoints.Add((JsonNode)new JsonObject()
        {
            ["x"] = date.ToDateFormat(),
            ["y"] = serversRawData.GetValueOrDefault(date, 0),
        });
    }

    JsonObject chartRaw = new()
    {
        ["type"] = "line",
        ["data"] = new JsonObject()
        {
            ["datasets"] = new JsonArray
            {
                /*
                (JsonNode)new JsonObject()
                {
                    ["label"] = "Players",
                    ["backgroundColor"] = "rgb(255, 99, 132)",
                    ["borderColor"] = "rgb(255, 99, 132)",
                    ["pointRadius"] = 0,
                    ["borderWidth"] = 2,
                    ["data"] = JsonSerializer.SerializeToNode(playersValues, JsonContexts.Default.Int64Array),
                    ["fill"] = false,
                    ["yAxisID"] = "Y2",
                },
                */
                (JsonNode)new JsonObject()
                {
                    ["label"] = "Servers",
                    ["backgroundColor"] = "rgb(54, 162, 235)",
                    ["borderColor"] = "rgb(54, 162, 235)",
                    ["pointRadius"] = 0,
                    ["borderWidth"] = 1,
                    ["data"] = serversPoints,
                    ["fill"] = false,
                },
            },
        },
        ["options"] = new JsonObject()
        {
            ["title"] = new JsonObject()
            {
                ["display"] = true,
                ["text"] = "bStats.org",
            },
            ["legend"] = new JsonObject()
            {
                ["display"] = true,
                ["position"] = "bottom",
            },
            ["scales"] = new JsonObject()
            {
                ["xAxes"] = new JsonArray
                {
                    (JsonNode)new JsonObject()
                    {
                        ["type"] = "time",
                        ["time"] = new JsonObject()
                        {
                            ["parser"] = "MM/DD/YYYY HH:mm",
                        },
                    },
                },
                ["yAxes"] = new JsonArray
                {
                    (JsonNode)new JsonObject()
                    {
                        ["id"] = "Y1",
                        ["position"] = "right",
                        ["display"] = true,
                    },
                },
            },
        },
    };
    await CreateImageAsync(chartRaw, outputFile, cancellationToken);
}

await CreateBStatsAsync(26312, "output/bstats/velocircon.png", cancellationToken);

public readonly record struct DateKey(DateOnly Date/*, int Hour*/)
    : IEquatable<DateKey>,
    IComparable<DateKey>,
    IMinMaxValue<DateKey>,
    IEqualityOperators<DateKey, DateKey, bool>,
    IComparisonOperators<DateKey, DateKey, bool>
{
    public static DateKey MaxValue { get; } = new DateKey(DateOnly.MaxValue/*, 23*/);
    public static DateKey MinValue { get; } = new DateKey(DateOnly.MinValue/*, 0*/);

    public int Number => Date.DayNumber;// * 24 + Hour;

    public int CompareTo(DateKey other)
    {
        int comparison = Date.CompareTo(other.Date);
        if (comparison != 0)
            return comparison;
        return 0;
        //return Hour.CompareTo(other.Hour);
    }

    public DateKey AddNumber(int offset) => FromNumber(Number + offset);

    public override string ToString()
        => Date.ToString("MM-dd");
    public string ToDateFormat()
        => $"{Date.ToString("MM/dd/yyyy")} 00:00";

    public static bool operator <(DateKey left, DateKey right) => left.CompareTo(right) < 0;
    public static bool operator >(DateKey left, DateKey right) => left.CompareTo(right) > 0;
    public static bool operator <=(DateKey left, DateKey right) => left.CompareTo(right) <= 0;
    public static bool operator >=(DateKey left, DateKey right) => left.CompareTo(right) >= 0;

    public static DateKey FromNumber(int number)
        => new DateKey(DateOnly.FromDayNumber(number/* / 24*/)/*, number % 24*/);
    public static DateKey FromDateTime(DateTime dateTime)
        => new DateKey(DateOnly.FromDateTime(dateTime)/*, dateTime.Hour*/);
}

internal class QuickChartResponse
{
    [JsonPropertyName("url")]
    public required string Url { get; set; }
}

[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(long[][]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(QuickChartResponse))]
internal partial class JsonContexts : JsonSerializerContext { }
