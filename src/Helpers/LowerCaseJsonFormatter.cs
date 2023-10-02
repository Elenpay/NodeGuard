using Blazorise;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace NodeGuard.Helpers;

/// <summary>
/// Custom formatter to lowercase the JSON output
/// </summary>
public class LowerCaseJsonFormatter : ITextFormatter
{
    private readonly JsonFormatter _formatter;

    public LowerCaseJsonFormatter()
    {
        _formatter = new JsonFormatter();
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var sw = new StringWriter();
        _formatter.Format(logEvent, sw);
        var json = sw.ToString().ToLower();
        output.WriteLine(json);
    }
}
