using System.Diagnostics;
using Serilog.Events;
using Serilog.Core;

namespace FundsManager.Helpers;
public class DatadogLogEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activityTraceId = Activity.Current?.TraceId;
        var spanId = Activity.Current?.SpanId;

        if (activityTraceId != null && spanId != null)
        {
            var stringTraceId = activityTraceId.ToString();


            var stringSpanId = spanId.ToString();

            var ddTraceId = Convert.ToUInt64(stringTraceId.Substring(16), 16).ToString();
            var ddSpanId = Convert.ToUInt64(stringSpanId, 16).ToString();

            logEvent.AddPropertyIfAbsent(new LogEventProperty("dd.trace_id", new ScalarValue(ddTraceId)));
            logEvent.AddPropertyIfAbsent(new LogEventProperty("dd.span_id", new ScalarValue(ddSpanId)));
        }
    }
}