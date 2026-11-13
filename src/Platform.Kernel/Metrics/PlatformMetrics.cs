using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Platform.Kernel.Metrics;

/// <summary>指标族类型，决定 Prometheus 文本里的 <c># TYPE</c>。</summary>
public enum MetricType
{
    /// <summary>只增不减的累计值。</summary>
    Counter,

    /// <summary>可覆盖的瞬时值。</summary>
    Gauge,

    /// <summary>固定分桶的分布（耗时）。</summary>
    Histogram,
}

/// <summary>
/// 进程内指标寄存器，渲染为 Prometheus 文本格式（exposition 0.0.4）。
/// </summary>
/// <remarks>
/// 只做「够告警」的最小实现，不引入采集库：每个副本各自暴露 <c>/metrics</c>，聚合交给采集端。
/// 指标名、help 与标签顺序以<b>首次注册</b>为准，所以新增指标必须复用
/// <see cref="PlatformMetricNames"/> 里的常量，不要就地拼新名字。
/// </remarks>
public sealed class PlatformMetrics
{
    /// <summary>HTTP 耗时分桶（秒），与常见告警阈值对齐。</summary>
    private static readonly double[] DurationBuckets = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly ConcurrentDictionary<string, MetricFamily> _families = new(StringComparer.Ordinal);

    /// <summary>累计计数。</summary>
    public void Increment(
        string name,
        string help,
        IReadOnlyDictionary<string, string>? labels = null,
        double by = 1)
    {
        var series = GetOrCreate(name, help, MetricType.Counter, labels).SeriesOf(labels);
        series.Add(by);
    }

    /// <summary>设置瞬时值（队列长度、积压条数这类可增可减的量）。</summary>
    public void Set(
        string name,
        string help,
        double value,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        var series = GetOrCreate(name, help, MetricType.Gauge, labels).SeriesOf(labels);
        series.Set(value);
    }

    /// <summary>记录一次耗时/分布值（未指定分桶时复用 HTTP 耗时分桶）。</summary>
    public void Observe(
        string name,
        string help,
        double value,
        IReadOnlyDictionary<string, string>? labels = null,
        double[]? buckets = null)
    {
        var family = GetOrCreate(
            name,
            help,
            MetricType.Histogram,
            labels,
            labels is null ? null : labels.Keys.ToList(),
            buckets ?? DurationBuckets);
        family.SeriesOf(labels).Observe(value);
    }

    /// <summary>记录一次 HTTP 请求。</summary>
    public void ObserveHttp(string method, string route, int statusCode, double durationSeconds)
    {
        var labelNames = new List<string> { "method", "route", "status_class" };
        var family = GetOrCreate(
            PlatformMetricNames.HttpRequestDuration,
            "HTTP 请求耗时（秒），按路由与状态码类别分桶。",
            MetricType.Histogram,
            labels: null,
            labelNames,
            DurationBuckets);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = method.ToUpperInvariant(),
            ["route"] = route,
            ["status_class"] = StatusCodeClass(statusCode),
        };

        family.SeriesOf(labels).Observe(durationSeconds);
    }

    /// <summary>当前已注册的指标名，供诊断与测试断言。</summary>
    public IReadOnlyCollection<string> RegisteredMetricNames() => _families.Keys.ToList();

    /// <summary>渲染为 Prometheus 文本。</summary>
    public string Render()
    {
        var builder = new StringBuilder(8192);
        foreach (var family in _families.Values.OrderBy(static f => f.Name, StringComparer.Ordinal))
        {
            builder.Append("# HELP ").Append(family.Name).Append(' ').Append(EscapeHelp(family.Help)).Append('\n');
            builder.Append("# TYPE ").Append(family.Name).Append(' ')
                .Append(family.Type switch
                {
                    MetricType.Counter => "counter",
                    MetricType.Gauge => "gauge",
                    _ => "histogram",
                })
                .Append('\n');

            foreach (var series in family.Series.Values.OrderBy(static s => s.LabelKey, StringComparer.Ordinal))
            {
                if (family.Type == MetricType.Histogram)
                {
                    series.RenderHistogram(builder, family.Name);
                }
                else
                {
                    builder.Append(family.Name).Append(series.LabelsText).Append(' ')
                        .Append(series.CurrentValue.ToString("0.######", CultureInfo.InvariantCulture))
                        .Append('\n');
                }
            }
        }

        return builder.ToString();
    }

    private MetricFamily GetOrCreate(
        string name,
        string help,
        MetricType type,
        IReadOnlyDictionary<string, string>? labels,
        IReadOnlyList<string>? labelNames = null,
        double[]? bucketBounds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var names = labelNames ?? (labels is null ? Array.Empty<string>() : labels.Keys.ToList());
        return _families.GetOrAdd(
            name,
            static (key, state) => new MetricFamily(
                key,
                state.Help,
                state.Type,
                state.LabelNames,
                state.BucketBounds),
            (Help: help, Type: type, LabelNames: names, BucketBounds: bucketBounds));
    }

    private static string StatusCodeClass(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => "2xx",
        >= 300 and < 400 => "3xx",
        >= 400 and < 500 => "4xx",
        >= 500 => "5xx",
        _ => "other",
    };

    private static string LabelText(IReadOnlyList<KeyValuePair<string, string>> labels) => labels.Count == 0
        ? string.Empty
        : "{" + string.Join(",", labels.Select(static l => $"{l.Key}=\"{EscapeValue(l.Value)}\"")) + "}";

    private static string EscapeValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeHelp(string help) =>
        help.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>一个指标族：固定的标签顺序 + 各标签组合的时间序列。</summary>
    private sealed class MetricFamily(
        string name,
        string help,
        MetricType type,
        IReadOnlyList<string> labelNames,
        double[]? bucketBounds)
    {
        public string Name { get; } = name;

        public string Help { get; } = help;

        public MetricType Type { get; } = type;

        public IReadOnlyList<string> LabelNames { get; } = labelNames;

        public double[]? BucketBounds { get; } = bucketBounds;

        public ConcurrentDictionary<string, MetricSeries> Series { get; } = new(StringComparer.Ordinal);

        public MetricSeries SeriesOf(IReadOnlyDictionary<string, string>? labels)
        {
            var pairs = new List<KeyValuePair<string, string>>(LabelNames.Count);
            var keyParts = new string[LabelNames.Count];
            for (var i = 0; i < LabelNames.Count; i++)
            {
                var labelName = LabelNames[i];
                var value = labels is not null && labels.TryGetValue(labelName, out var found) ? found : string.Empty;
                pairs.Add(new KeyValuePair<string, string>(labelName, value));
                keyParts[i] = value;
            }

            var labelKey = string.Join("\u0001", keyParts);
            return Series.GetOrAdd(
                labelKey,
                static (key, state) => new MetricSeries(key, state.Pairs, state.BucketBounds),
                (Pairs: pairs, BucketBounds: BucketBounds ?? Array.Empty<double>()));
        }
    }

    /// <summary>某个标签组合的取值。</summary>
    private sealed class MetricSeries(
        string labelKey,
        IReadOnlyList<KeyValuePair<string, string>> labels,
        double[] bucketBounds)
    {
        private readonly IReadOnlyList<KeyValuePair<string, string>> _labels = labels;
        private readonly double[] _bucketBounds = bucketBounds;
        private readonly object _sync = new();
        private readonly long[] _bucketCounts = new long[bucketBounds.Length];
        private double _value;
        private double _sum;
        private long _count;

        public string LabelKey { get; } = labelKey;

        public string LabelsText { get; } = LabelText(labels);

        /// <summary>计数器/仪表当前值。</summary>
        public double CurrentValue
        {
            get
            {
                lock (_sync)
                {
                    return _value;
                }
            }
        }

        public void Add(double by)
        {
            lock (_sync)
            {
                _value += by;
            }
        }

        public void Set(double value)
        {
            lock (_sync)
            {
                _value = value;
            }
        }

        public void Observe(double value)
        {
            lock (_sync)
            {
                for (var i = 0; i < _bucketBounds.Length; i++)
                {
                    if (value <= _bucketBounds[i])
                    {
                        _bucketCounts[i]++;
                    }
                }

                _count++;
                _sum += value;
            }
        }

        public void RenderHistogram(StringBuilder builder, string name)
        {
            long[] counts;
            long count;
            double sum;
            lock (_sync)
            {
                counts = (long[])_bucketCounts.Clone();
                count = _count;
                sum = _sum;
            }

            for (var i = 0; i < counts.Length; i++)
            {
                builder.Append(name).Append("_bucket")
                    .Append(WithLe(_bucketBounds[i].ToString("0.###", CultureInfo.InvariantCulture)))
                    .Append(' ').Append(counts[i].ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            builder.Append(name).Append("_bucket").Append(WithLe("+Inf"))
                .Append(' ').Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(name).Append("_sum").Append(LabelsText).Append(' ')
                .Append(sum.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(name).Append("_count").Append(LabelsText).Append(' ')
                .Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private string WithLe(string le)
        {
            var pairs = new List<KeyValuePair<string, string>>(_labels.Count + 1);
            pairs.AddRange(_labels);
            pairs.Add(new KeyValuePair<string, string>("le", le));
            return LabelText(pairs);
        }
    }
}
