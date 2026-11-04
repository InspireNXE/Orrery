using Orrery.Core.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Orrery.App.Logging;

/// <summary>
/// Hides names and addresses (<see cref="LogMask"/>) before a line reaches the file, unless <paramref name="showNames"/>
/// says not to. Properties named for what they hold (Host, User, Path...) become tags; other text, such as a library's
/// error message, has known names, addresses and web addresses taken out. Numbers, versions, protocols and the like
/// stay. An exception is written as text through the same pass, as <see cref="FailureProperty"/>.
/// </summary>
sealed class MaskingSink(ILogEventSink inner, LogMask mask, Func<bool> showNames) : ILogEventSink
{
    public const string FailureProperty = "Failure";

    static readonly Dictionary<string, LogMaskKind> Kinds = new(StringComparer.Ordinal)
    {
        ["Host"] = LogMaskKind.Host, ["Address"] = LogMaskKind.Host, ["Next"] = LogMaskKind.Host, ["Destination"] = LogMaskKind.Host,
        ["User"] = LogMaskKind.User,
        ["Name"] = LogMaskKind.Name, ["Title"] = LogMaskKind.Name, ["Left"] = LogMaskKind.Name, ["Right"] = LogMaskKind.Name,
        ["Credential"] = LogMaskKind.Name, ["Command"] = LogMaskKind.Name,
        ["Path"] = LogMaskKind.Path, ["Local"] = LogMaskKind.Path, ["Remote"] = LogMaskKind.Path, ["From"] = LogMaskKind.Path,
        ["To"] = LogMaskKind.Path, ["File"] = LogMaskKind.Path, ["Backup"] = LogMaskKind.Path, ["Key"] = LogMaskKind.Path,
        ["Folder"] = LogMaskKind.Path, ["DataDir"] = LogMaskKind.Path, ["OldDataDir"] = LogMaskKind.Path,
        ["Site"] = LogMaskKind.Web, ["Page"] = LogMaskKind.Web, ["Url"] = LogMaskKind.Web,
        ["Token"] = LogMaskKind.Token, ["Cookie"] = LogMaskKind.Token,
        ["Mac"] = LogMaskKind.Mac,
    };

    // Text that says how, never who or where; left as it is.
    static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "SourceContext", "UtcTimestamp", "Template", "Version", "ServerVersion", "ProtocolVersion", "Os", "Architecture", "Runtime",
        "Mode", "Protocol", "Cipher", "KeyExchange", "Codec", "Format", "Session", "State", "Kind", "Scheme", "Quality", "Method",
        "ExceptionType", "EnumType", "Authenticator", "Shell", "Store", "Duration", "How", "Filter", "Level", "DataFolder", "Shown",
    };

    public void Emit(LogEvent logEvent)
    {
        var show = showNames();
        var values = new Dictionary<string, LogEventPropertyValue>(logEvent.Properties, StringComparer.Ordinal);
        if (!show)
        {
            // Named values first, so the free text after them is checked against them too.
            foreach (var (name, value) in logEvent.Properties)
            {
                if (Kinds.TryGetValue(name, out var kind))
                {
                    values[name] = Clean(value, text => mask.Mask(kind, text));
                }
            }

            foreach (var (name, value) in logEvent.Properties)
            {
                if (!Kinds.ContainsKey(name) && !Plain.Contains(name))
                {
                    values[name] = Clean(value, mask.Redact);
                }
            }
        }

        var failure = logEvent.Exception is not { } e ? string.Empty : (show ? e.ToString() : mask.Redact(e.ToString())) + Environment.NewLine;
        values[FailureProperty] = new ScalarValue(failure);
        inner.Emit(new LogEvent(logEvent.Timestamp, logEvent.Level, null, logEvent.MessageTemplate,
            values.Select(pair => new LogEventProperty(pair.Key, pair.Value))));
    }

    // Text inside lists and objects is hidden too; numbers, dates and the like aren't text and pass as they are.
    static LogEventPropertyValue Clean(LogEventPropertyValue value, Func<string, string> hide) => value switch
    {
        ScalarValue { Value: string text } => new ScalarValue(hide(text)),
        ScalarValue { Value: Uri uri } => new ScalarValue(hide(uri.ToString())),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(e => Clean(e, hide))),
        StructureValue structure => new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Clean(p.Value, hide))), structure.TypeTag),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(pair =>
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, Clean(pair.Value, hide)))),
        _ => value,
    };
}
