using System.IO;

namespace BetterDns.Service;

public sealed class ServiceInstanceLock : IDisposable
{
    private readonly FileStream stream;
    private ServiceInstanceLock(FileStream stream) => this.stream = stream;

    public static ServiceInstanceLock? TryAcquire(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            return new(new FileStream(Path.Combine(directory, "service.instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => stream.Dispose();
}
