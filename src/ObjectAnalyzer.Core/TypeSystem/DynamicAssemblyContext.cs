using System.Runtime.Loader;

namespace ObjectAnalyzer.TypeSystem;

public class DynamicAssemblyContext() : AssemblyLoadContext(name: $"DynamicType_{Guid.NewGuid():N}", isCollectible: true), IDisposable
{
    public void Dispose()
    {
        Unload();
        GC.SuppressFinalize(this);
    }
}
