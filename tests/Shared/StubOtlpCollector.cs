using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace n8Tracks.TestSupport;

/// <summary>
/// An in-process stand-in for an OpenTelemetry collector: accepts OTLP over HTTP (protobuf) on the
/// loopback interface and keeps every request it was sent. It is a bare <see cref="HttpListener"/>,
/// not an ASP.NET Core host, so the instrumentation under test never traces the collector itself.
/// Any path other than the three OTLP ones is answered with <see cref="OtherResponseBody"/>, so the
/// same listener can also play the gateway's upstream.
/// </summary>
internal sealed class StubOtlpCollector : IAsyncDisposable
{
    public const string TracesPath = "/v1/traces";
    public const string MetricsPath = "/v1/metrics";
    public const string LogsPath = "/v1/logs";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpListener listener;
    private readonly ConcurrentQueue<Request> requests = new();
    private readonly Task loop;

    private StubOtlpCollector(HttpListener listener, string endpoint)
    {
        this.listener = listener;
        Endpoint = endpoint;
        loop = Task.Run(Serve);
    }

    /// <summary>The value for <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>: scheme, loopback address, and port.</summary>
    public string Endpoint { get; }

    /// <summary>The JSON body returned for a path that is not an OTLP one.</summary>
    public string OtherResponseBody { get; init; } = "{}";

    /// <summary>Every request received so far, in order.</summary>
    public IReadOnlyList<Request> Requests => [.. requests];

    public static StubOtlpCollector Start(string otherResponseBody = "{}")
    {
        // The port can be taken between the probe and the listen; try another.
        for (var attempt = 0; ; attempt++)
        {
            var endpoint = $"http://127.0.0.1:{FreePort()}";
            var listener = new HttpListener();
            listener.Prefixes.Add(endpoint + "/");
            try
            {
                listener.Start();
                return new StubOtlpCollector(listener, endpoint) { OtherResponseBody = otherResponseBody };
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    /// <summary>The bodies sent to one path (<see cref="TracesPath"/>, <see cref="MetricsPath"/>, <see cref="LogsPath"/>).</summary>
    public IReadOnlyList<byte[]> Bodies(string path) => [.. requests.Where(request => request.Path == path).Select(request => request.Body)];

    /// <summary>Every span received, with the service name of the resource it came under.</summary>
    public IReadOnlyList<OtlpSpan> Spans() => [.. Bodies(TracesPath).SelectMany(OtlpReader.Spans)];

    /// <summary>The service names of the resources received on one path.</summary>
    public IReadOnlyList<string> ServiceNames(string path) => [.. Bodies(path).SelectMany(OtlpReader.ServiceNames)];

    /// <summary>
    /// Everything received on the OTLP paths, as text: protobuf holds strings as plain UTF-8, so a
    /// value that was exported in any field is found by searching this.
    /// </summary>
    public string ReceivedText(params string[] paths) =>
        string.Join('\n', requests.Where(request => paths.Contains(request.Path)).Select(request => Encoding.UTF8.GetString(request.Body)));

    /// <summary>Polls until <paramref name="condition"/> holds, running <paramref name="flush"/> before each look.</summary>
    public static async Task Eventually(Func<bool> condition, Action? flush, string failure)
    {
        var deadline = DateTimeOffset.UtcNow + WaitTimeout;
        while (true)
        {
            flush?.Invoke();
            if (condition())
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, failure);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    public async ValueTask DisposeAsync()
    {
        listener.Stop();
        await loop;
        listener.Close();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task Serve()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped.
                return;
            }

            try
            {
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                requests.Enqueue(new Request(context.Request.HttpMethod, path, context.Request.ContentType, body.ToArray()));

                if (path is TracesPath or MetricsPath or LogsPath)
                {
                    // An empty protobuf message is a valid "all accepted" export response.
                    context.Response.ContentType = "application/x-protobuf";
                    context.Response.ContentLength64 = 0;
                }
                else
                {
                    var json = Encoding.UTF8.GetBytes(OtherResponseBody);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = json.Length;
                    await context.Response.OutputStream.WriteAsync(json);
                }

                context.Response.Close();
            }
            catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
            {
                // The client went away; the next request is served as usual.
            }
        }
    }

    internal sealed record Request(string Method, string Path, string? ContentType, byte[] Body);
}

/// <summary>One exported span: the resource's <c>service.name</c>, the span name, and its kind.</summary>
internal sealed record OtlpSpan(string? ServiceName, string Name, int Kind)
{
    public const int ServerKind = 2;
    public const int ClientKind = 3;
}

/// <summary>
/// Reads the few fields the tests need out of OTLP protobuf export requests, by field number (see
/// opentelemetry-proto): request.1 = resource block; block.1 = resource, block.2 = scope block;
/// resource.1 = attribute (1 = key, 2 = value, value.1 = string); scope block.2 = span;
/// span.5 = name, span.6 = kind.
/// </summary>
internal static class OtlpReader
{
    private const int VarintWire = 0;
    private const int Fixed64Wire = 1;
    private const int LengthDelimitedWire = 2;
    private const int Fixed32Wire = 5;

    public static IEnumerable<string> ServiceNames(byte[] exportRequest) =>
        [.. ResourceBlocks(exportRequest).Select(block => ServiceName(block)).OfType<string>()];

    public static IEnumerable<OtlpSpan> Spans(byte[] exportTraceRequest)
    {
        var spans = new List<OtlpSpan>();

        foreach (var block in ResourceBlocks(exportTraceRequest))
        {
            var serviceName = ServiceName(block);

            foreach (var scopeBlock in Messages(block, field: 2))
            {
                foreach (var span in Messages(scopeBlock, field: 2))
                {
                    var name = string.Empty;
                    var kind = 0;
                    foreach (var field in Fields(span))
                    {
                        if (field is { Number: 5, Wire: LengthDelimitedWire })
                        {
                            name = Encoding.UTF8.GetString(field.Bytes.Span);
                        }
                        else if (field is { Number: 6, Wire: VarintWire })
                        {
                            kind = (int)field.Varint;
                        }
                    }

                    spans.Add(new OtlpSpan(serviceName, name, kind));
                }
            }
        }

        return spans;
    }

    private static List<ReadOnlyMemory<byte>> ResourceBlocks(byte[] exportRequest) => Messages(exportRequest, field: 1);

    private static string? ServiceName(ReadOnlyMemory<byte> resourceBlock)
    {
        foreach (var resource in Messages(resourceBlock, field: 1))
        {
            foreach (var attribute in Messages(resource, field: 1))
            {
                string? key = null;
                string? text = null;
                foreach (var field in Fields(attribute))
                {
                    if (field is { Number: 1, Wire: LengthDelimitedWire })
                    {
                        key = Encoding.UTF8.GetString(field.Bytes.Span);
                    }
                    else if (field is { Number: 2, Wire: LengthDelimitedWire })
                    {
                        text = Messages(field.Bytes, field: 1).Select(value => Encoding.UTF8.GetString(value.Span)).FirstOrDefault();
                    }
                }

                if (key == "service.name")
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static List<ReadOnlyMemory<byte>> Messages(ReadOnlyMemory<byte> message, int field) =>
        [.. Fields(message).Where(entry => entry.Number == field && entry.Wire == LengthDelimitedWire).Select(entry => entry.Bytes)];

    private static List<Field> Fields(ReadOnlyMemory<byte> message)
    {
        var fields = new List<Field>();
        var span = message.Span;
        var position = 0;

        while (position < span.Length)
        {
            var tag = ReadVarint(span, ref position);
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);

            switch (wire)
            {
                case VarintWire:
                    fields.Add(new Field(number, wire, ReadVarint(span, ref position), ReadOnlyMemory<byte>.Empty));
                    break;
                case Fixed64Wire:
                    position += 8;
                    break;
                case Fixed32Wire:
                    position += 4;
                    break;
                case LengthDelimitedWire:
                    var length = (int)ReadVarint(span, ref position);
                    fields.Add(new Field(number, wire, 0, message.Slice(position, length)));
                    position += length;
                    break;
                default:
                    throw new InvalidDataException($"Unexpected protobuf wire type {wire} at offset {position}.");
            }
        }

        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int position)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var next = span[position++];
            value |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private readonly record struct Field(int Number, int Wire, ulong Varint, ReadOnlyMemory<byte> Bytes);
}

/// <summary>Finds OpenTelemetry in a host's service registrations.</summary>
internal static class TelemetryRegistrations
{
    private static readonly string[] TelemetryAssemblyPrefixes = ["OpenTelemetry", "Serilog.Sinks.OpenTelemetry"];

    /// <summary>
    /// Every registration that involves a type from an OpenTelemetry assembly: as the service, the
    /// implementation, the instance, what a factory returns, or a generic argument of any of those
    /// (so <c>IConfigureOptions&lt;OtlpExporterOptions&gt;</c> counts).
    /// </summary>
    public static IReadOnlyList<string> Find(IEnumerable<Microsoft.Extensions.DependencyInjection.ServiceDescriptor> services) =>
        [
            .. services
                .Where(service => InvolvedTypes(service).Any(IsTelemetryType))
                .Select(service => $"{service.ServiceType} -> {string.Join(", ", InvolvedTypes(service).Skip(1).Distinct())}"),
        ];

    public static bool IsTelemetryType(Type type) =>
        TelemetryAssemblyPrefixes.Any(prefix => type.Assembly.GetName().Name!.StartsWith(prefix, StringComparison.Ordinal));

    private static IEnumerable<Type> InvolvedTypes(Microsoft.Extensions.DependencyInjection.ServiceDescriptor service)
    {
        var direct = new List<Type?> { service.ServiceType };

        if (service.IsKeyedService)
        {
            direct.Add(service.KeyedImplementationType);
            direct.Add(service.KeyedImplementationInstance?.GetType());
            direct.Add(service.KeyedImplementationFactory?.Method.ReturnType);
        }
        else
        {
            direct.Add(service.ImplementationType);
            direct.Add(service.ImplementationInstance?.GetType());
            direct.Add(service.ImplementationFactory?.Method.ReturnType);

            // A lambda's declaring type sits in the assembly that registered it.
            direct.Add(service.ImplementationFactory?.Method.DeclaringType);
        }

        return direct.OfType<Type>().SelectMany(Flatten);
    }

    private static IEnumerable<Type> Flatten(Type type) =>
        type.IsGenericType ? type.GetGenericArguments().SelectMany(Flatten).Prepend(type) : [type];
}
