using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure.Health;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Health;

/// <summary>
/// The fault-injecting connection factory: the real one until a test tells it to throw or to hang.
/// The database starts up normally, so the fault always arrives after startup.
/// </summary>
internal sealed class FaultInjectingConnectionFactory : IDatabaseConnectionFactory, IDisposable
{
    private readonly ManualResetEventSlim released = new(initialState: true);
    private IDatabaseConnectionFactory? inner;

    /// <summary>When set, every connection request throws it.</summary>
    public Exception? Fault { get; set; }

    public int Calls { get; private set; }

    public void Hang() => released.Reset();

    public void Release() => released.Set();

    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IDatabaseConnectionFactory>();
        services.AddSingleton<IDatabaseConnectionFactory>(provider =>
        {
            inner = new SqliteConnectionFactory(provider.GetRequiredService<N8TracksOptions>());
            return this;
        });
    }

    public DbConnection CreateForExistingDatabase()
    {
        Calls++;
        released.Wait();

        return Fault is { } fault ? throw fault : inner!.CreateForExistingDatabase();
    }

    public void Dispose()
    {
        released.Set();
        released.Dispose();
    }
}

/// <summary>A media probe a test can switch between readable, unreadable, and hanging.</summary>
internal sealed class SwitchableMediaProbe : IMediaMountProbe, IDisposable
{
    private readonly ManualResetEventSlim released = new(initialState: true);

    public bool Readable { get; set; } = true;

    public int Calls { get; private set; }

    public void Hang() => released.Reset();

    public void Release() => released.Set();

    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IMediaMountProbe>();
        services.AddSingleton<IMediaMountProbe>(this);
    }

    public bool IsReadable(string path)
    {
        Calls++;
        released.Wait();

        return Readable;
    }

    public void Dispose()
    {
        released.Set();
        released.Dispose();
    }
}
